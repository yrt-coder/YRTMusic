#requires -Version 5.1
<#
.SYNOPSIS
Embeds and independently verifies a YRT-GPT Authenticode signature.
.DESCRIPTION
Reuses an eligible certificate from CurrentUser\My, or creates a two-year
non-exportable RSA-3072 SHA-256 code-signing certificate in that store.
Exports only the public CER. Does not write Root or TrustedPublisher stores.
No timestamp service is contacted. Certificate expiry remains relevant.
The successful JSON report generates English and both Chinese signing records
through the adjacent Write-LocalizedSigning.ps1 helper.
Run only after the final executable has been built; any later PE modification
requires signing and verification again.
.EXAMPLE
.\Sign-Release.ps1 -ExePath 'D:\release\YRTMusic.exe' -PublicCertificatePath 'D:\release\YRT-GPT.cer' -ReportPath 'D:\release\signature-report.json'
.LINK
https://learn.microsoft.com/en-us/powershell/module/pki/new-selfsignedcertificate
.LINK
https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-authenticodesignature
.LINK
https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.pkcs.signedcms.checksignature
.LINK
https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$PublicCertificatePath,
    [Parameter(Mandatory = $true)][string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$report = [ordered]@{
    schemaVersion = 2
    applicationVersion = '1.0.0.0'
    localSigningStartedAtUtc = $null
    localSigningCompletedAtUtc = $null
    localSigningStartedAtShanghai = $null
    localSigningCompletedAtShanghai = $null
    localSigningTimeSource = 'Local system clock recorded immediately before and after Set-AuthenticodeSignature. This is an unsigned local operation record, not a signed signingTime attribute or a trusted timestamp.'
    trustedTimestampObtained = $false
    timestampNote = 'No trusted timestamp was obtained. No signing digest was sent to a third-party timestamp service. Windows may display the timestamp as unavailable.'
    localSigningRecordNote = 'Unsigned local system-clock record of the signing command start and completion; not third-party time certification.'
    certificateNotBeforeShanghai = $null
    certificateNotAfterShanghai = $null
    embeddedTimeInspection = $null
    editor = 'YRT-GPT'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    finishedAtUtc = $null
    passed = $false
    executable = $ExePath
    publicCertificate = $PublicCertificatePath
    certificateStore = 'Cert:\CurrentUser\My'
    certificateCreated = $false
    privateKeyExported = $false
    installedIntoRootOrTrustedPublisher = $false
    timestampRequested = $false
    certificateSubject = $null
    signer = $null
    thumbprint = $null
    certificateNotBeforeUtc = $null
    certificateNotAfterUtc = $null
    certificateIsSelfIssued = $null
    privateKeyAlgorithm = $null
    windowsSetSignatureStatus = $null
    windowsSignatureStatus = $null
    windowsSignatureStatusMessage = $null
    windowsSignatureType = $null
    windowsSignerThumbprint = $null
    independentVerification = $null
    executableSha256 = $null
    publicCertificateSha256 = $null
    error = $null
    trustNote = 'Cryptographic integrity and Windows trust are separate. The actual Windows verdict is reported verbatim. This script never installs a root or trusted publisher certificate. An untrusted self-signed certificate must not be described as publicly trusted.'
    verifierScope = 'PE32/PE32+ with contiguous raw sections and an EOF certificate table, matching this project compiler output. Unsupported layouts fail closed.'
    documentation = @(
        'https://learn.microsoft.com/en-us/powershell/module/pki/new-selfsignedcertificate',
        'https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-authenticodesignature',
        'https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.pkcs.signedcms.checksignature',
        'https://learn.microsoft.com/en-us/windows/win32/debug/pe-format'
    )
}

function Write-SigningReport {
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $reportParent = [System.IO.Path]::GetDirectoryName($ReportPath)
    if (-not [System.IO.Directory]::Exists($reportParent)) {
        [System.IO.Directory]::CreateDirectory($reportParent) | Out-Null
    }
    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($ReportPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Test-CodeSigningCertificate {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    if ($null -eq $Certificate -or
        -not [String]::Equals($Certificate.Subject, 'CN=YRT-GPT', [StringComparison]::Ordinal) -or
        -not $Certificate.HasPrivateKey) { return $false }
    $nowUtc = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $nowUtc -or
        $Certificate.NotAfter.ToUniversalTime() -le $nowUtc) { return $false }
    $ekuFound = $false
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.37') {
            $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new()
            $eku.CopyFrom($extension)
            foreach ($usage in $eku.EnhancedKeyUsages) {
                if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $ekuFound = $true }
            }
        }
    }
    return $ekuFound
}

try {
    foreach ($inputPath in @($ExePath, $PublicCertificatePath, $ReportPath)) {
        if (-not [System.IO.Path]::IsPathRooted($inputPath)) {
            throw 'All three paths must be absolute filesystem paths.'
        }
    }
    $ExePath = [System.IO.Path]::GetFullPath($ExePath)
    $PublicCertificatePath = [System.IO.Path]::GetFullPath($PublicCertificatePath)
    $ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $report.executable = $ExePath
    $report.publicCertificate = $PublicCertificatePath
    if (-not [System.IO.File]::Exists($ExePath) -or [System.IO.Path]::GetExtension($ExePath) -ine '.exe') {
        throw 'ExePath must identify an existing .exe file.'
    }
    if ([System.IO.Path]::GetExtension($PublicCertificatePath) -ine '.cer' -or
        [System.IO.Path]::GetExtension($ReportPath) -ine '.json') {
        throw 'The public certificate must use .cer and the report must use .json.'
    }
    if ([String]::Equals($ExePath, $PublicCertificatePath, [StringComparison]::OrdinalIgnoreCase) -or
        [String]::Equals($ExePath, $ReportPath, [StringComparison]::OrdinalIgnoreCase) -or
        [String]::Equals($PublicCertificatePath, $ReportPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The executable, public certificate, and report paths must differ.'
    }
    if ([System.IO.Directory]::Exists($PublicCertificatePath) -or [System.IO.Directory]::Exists($ReportPath)) {
        throw 'An output path points to a directory.'
    }

    foreach ($requiredCommand in @('Get-AuthenticodeSignature', 'Set-AuthenticodeSignature', 'New-SelfSignedCertificate')) {
        if ($null -eq (Get-Command -Name $requiredCommand -ErrorAction SilentlyContinue)) {
            throw ('Required Windows command is unavailable: ' + $requiredCommand)
        }
    }
    $localizedWriter = Join-Path $PSScriptRoot 'Write-LocalizedSigning.ps1'
    if (-not [System.IO.File]::Exists($localizedWriter)) {
        throw ('Required signing-record text generator is missing: ' + $localizedWriter)
    }
    $helperPath = Join-Path $PSScriptRoot 'AuthenticodeVerifier.cs'
    if (-not [System.IO.File]::Exists($helperPath)) {
        throw ('Required independent verifier source is missing: ' + $helperPath)
    }
    if ($null -eq ('YrtReleaseSigning.AuthenticodeVerifier' -as [type])) {
        if ($PSVersionTable.PSEdition -eq 'Core') {
            Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop
            $referenceDirectory = Join-Path $PSHOME 'ref'
            if (-not [System.IO.Directory]::Exists($referenceDirectory)) {
                throw 'PowerShell reference assemblies are missing; run this script in Windows PowerShell 5.1.'
            }
            $references = @([System.IO.Directory]::GetFiles($referenceDirectory, '*.dll'))
            $references += [System.Security.Cryptography.Pkcs.SignedCms].Assembly.Location
            Add-Type -Path $helperPath -ReferencedAssemblies $references -ErrorAction Stop
        }
        else {
            Add-Type -AssemblyName System.Security -ErrorAction Stop
            Add-Type -Path $helperPath -ReferencedAssemblies 'System.dll', 'System.Security.dll', 'System.Core.dll' -ErrorAction Stop
        }
    }

    $selectedCertificate = $null
    $selectedKeyAlgorithm = $null
    $candidates = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' |
        Where-Object { Test-CodeSigningCertificate -Certificate $_ } |
        Sort-Object -Property NotAfter -Descending)
    foreach ($candidate in $candidates) {
        try { $keyAlgorithm = [YrtReleaseSigning.AuthenticodeVerifier]::ProbePrivateKey($candidate) }
        catch { $keyAlgorithm = $null }
        if (-not [String]::IsNullOrEmpty($keyAlgorithm)) {
            $selectedCertificate = $candidate
            $selectedKeyAlgorithm = $keyAlgorithm
            break
        }
    }

    if ($null -eq $selectedCertificate) {
        $certificateParameters = @{
            Subject = 'CN=YRT-GPT'
            Type = 'CodeSigningCert'
            FriendlyName = 'YRT-GPT code signing (self-signed)'
            CertStoreLocation = 'Cert:\CurrentUser\My'
            KeyAlgorithm = 'RSA'
            KeyLength = 3072
            HashAlgorithm = 'SHA256'
            KeyUsage = 'DigitalSignature'
            KeyExportPolicy = 'NonExportable'
            Provider = 'Microsoft Software Key Storage Provider'
            NotBefore = (Get-Date).AddMinutes(-5)
            NotAfter = (Get-Date).AddYears(2)
        }
        $selectedCertificate = New-SelfSignedCertificate @certificateParameters
        $report.certificateCreated = $true
        if (-not (Test-CodeSigningCertificate -Certificate $selectedCertificate)) {
            throw 'The new certificate failed the exact-subject, validity, private-key, or code-signing EKU requirements.'
        }
        $selectedKeyAlgorithm = [YrtReleaseSigning.AuthenticodeVerifier]::ProbePrivateKey($selectedCertificate)
        if ([String]::IsNullOrEmpty($selectedKeyAlgorithm)) {
            throw 'The new non-exportable private key cannot produce a verified signature.'
        }
    }

    $report.certificateSubject = $selectedCertificate.Subject
    $report.signer = $selectedCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $report.thumbprint = $selectedCertificate.Thumbprint
    $report.certificateNotBeforeUtc = $selectedCertificate.NotBefore.ToUniversalTime().ToString('o')
    $report.certificateNotAfterUtc = $selectedCertificate.NotAfter.ToUniversalTime().ToString('o')
    $report.certificateNotBeforeShanghai = ([DateTimeOffset]$selectedCertificate.NotBefore.ToUniversalTime()).ToOffset([TimeSpan]::FromHours(8)).ToString('o')
    $report.certificateNotAfterShanghai = ([DateTimeOffset]$selectedCertificate.NotAfter.ToUniversalTime()).ToOffset([TimeSpan]::FromHours(8)).ToString('o')
    $report.certificateIsSelfIssued = [String]::Equals($selectedCertificate.Subject, $selectedCertificate.Issuer, [StringComparison]::Ordinal)
    $report.privateKeyAlgorithm = $selectedKeyAlgorithm

    $certificateParent = [System.IO.Path]::GetDirectoryName($PublicCertificatePath)
    if (-not [System.IO.Directory]::Exists($certificateParent)) {
        [System.IO.Directory]::CreateDirectory($certificateParent) | Out-Null
    }
    # X509ContentType.Cert is public DER only; no private-key export is performed.
    $publicBytes = $selectedCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($PublicCertificatePath, $publicBytes)
    $publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PublicCertificatePath)
    try {
        if ($publicCertificate.HasPrivateKey -or
            -not [String]::Equals($publicCertificate.Thumbprint, $selectedCertificate.Thumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The exported public certificate did not match the selected signer.'
        }
    }
    finally { $publicCertificate.Dispose() }

    # No timestamp server is configured and no trust-store import is performed.
    $taskSigningStart = [DateTimeOffset]::UtcNow
    $setResult = Set-AuthenticodeSignature -LiteralPath $ExePath -Certificate $selectedCertificate -HashAlgorithm SHA256 -IncludeChain NotRoot
    $taskSigningFinish = [DateTimeOffset]::UtcNow
    $report.localSigningStartedAtUtc = $taskSigningStart.ToString('o')
    $report.localSigningCompletedAtUtc = $taskSigningFinish.ToString('o')
    $report.localSigningStartedAtShanghai = $taskSigningStart.ToOffset([TimeSpan]::FromHours(8)).ToString('o')
    $report.localSigningCompletedAtShanghai = $taskSigningFinish.ToOffset([TimeSpan]::FromHours(8)).ToString('o')
    Add-Type -Path (Join-Path $PSScriptRoot 'TimestampVerifier.cs') -ReferencedAssemblies 'System.dll','System.Security.dll','System.Core.dll'
    $report.embeddedTimeInspection = [YrtReleaseSigning.TimestampVerifier]::Inspect($ExePath)
    if ($report.embeddedTimeInspection.TimestampKind -ne 'None') { throw 'Unexpected timestamp: this release requires an offline signature with no TSA timestamp.' }
    $report.windowsSetSignatureStatus = $setResult.Status.ToString()

    # A separate parser checks the embedded CMS signatures AND the PE file digest,
    # even if Windows reports an untrusted certificate chain.
    $verified = [YrtReleaseSigning.AuthenticodeVerifier]::Verify($ExePath, $selectedCertificate.Thumbprint)
    $report.independentVerification = $verified

    $windowsSignature = Get-AuthenticodeSignature -LiteralPath $ExePath
    $report.windowsSignatureStatus = $windowsSignature.Status.ToString()
    $report.windowsSignatureStatusMessage = $windowsSignature.StatusMessage
    $report.windowsSignatureType = $windowsSignature.SignatureType.ToString()
    if ($null -ne $windowsSignature.SignerCertificate) {
        $report.windowsSignerThumbprint = $windowsSignature.SignerCertificate.Thumbprint
    }
    if ($windowsSignature.Status.ToString() -in @('HashMismatch', 'NotSigned', 'NotSupportedFileFormat', 'Incompatible')) {
        throw ('Windows rejected the embedded signature: ' + $windowsSignature.Status + ' - ' + $windowsSignature.StatusMessage)
    }
    if ($null -eq $windowsSignature.SignerCertificate -or
        -not [String]::Equals($windowsSignature.SignerCertificate.Thumbprint, $selectedCertificate.Thumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows did not identify the expected embedded signer.'
    }

    $report.executableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    $report.publicCertificateSha256 = (Get-FileHash -LiteralPath $PublicCertificatePath -Algorithm SHA256).Hash
    $report.passed = $true
    Write-SigningReport
    $taskInfoDirectory = Join-Path ([System.IO.Path]::GetDirectoryName($ExePath)) 'Info'
    [System.IO.Directory]::CreateDirectory($taskInfoDirectory) | Out-Null
    $taskLocalRecord = Join-Path $taskInfoDirectory 'Signing.json'
    [System.IO.File]::WriteAllText($taskLocalRecord, ($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    & $localizedWriter -SigningJson $taskLocalRecord -OutputDirectory $taskInfoDirectory | Out-Null
    [pscustomobject]@{
        Executable = $ExePath
        Signer = $report.signer
        Thumbprint = $report.thumbprint
        CryptographicSignatureValid = $verified.CmsSignatureValid
        AuthenticodeFileDigestMatches = $verified.AuthenticodeFileDigestMatches
        WindowsSignatureStatus = $report.windowsSignatureStatus
        WindowsStatusMessage = $report.windowsSignatureStatusMessage
        PublicCertificate = $PublicCertificatePath
        Report = $ReportPath
    }
}
catch {
    $report.error = $_.Exception.ToString()
    try { Write-SigningReport } catch { Write-Warning ('Could not write the signing report: ' + $_.Exception.Message) }
    throw
}
