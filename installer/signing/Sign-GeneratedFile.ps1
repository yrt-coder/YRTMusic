#requires -Version 5.1
# Editor: YRT-GPT. Offline signing callback for generated Inno Setup PE files.
param(
    [Parameter(Mandatory=$true)][string]$FilePath,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [Parameter(Mandatory=$true)][string]$ReportDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($FilePath)
$allowed=@([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\'),[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\'))
if(-not ($allowed|Where-Object {$target.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase)})){throw 'The signing target is outside the generated build/output directories.'}
if(-not (Test-Path -LiteralPath $target -PathType Leaf)){throw 'The generated signing target is missing.'}
$bytes=[IO.File]::ReadAllBytes($target)
if($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90){throw 'The installer callback only signs generated Windows PE files.'}
$thumb=$CertificateThumbprint.Replace(' ','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$thumb)
if($cert.Subject -ne 'CN=YRT-GPT' -or -not $cert.HasPrivateKey){throw 'The expected local YRT-GPT signing certificate and private key are unavailable.'}
if([DateTime]::Now -lt $cert.NotBefore -or [DateTime]::Now -gt $cert.NotAfter){throw 'The code-signing certificate is outside its validity period.'}
$codeSigning=$false
foreach($extension in $cert.Extensions){
    if($extension.Oid.Value -eq '2.5.29.37'){
        $eku=New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension
        $eku.CopyFrom($extension)
        foreach($usage in $eku.EnhancedKeyUsages){if($usage.Value -eq '1.3.6.1.5.5.7.3.3'){$codeSigning=$true}}
    }
}
if(-not $codeSigning){throw 'The certificate is not eligible for code signing.'}
Add-Type -AssemblyName System.Security
Add-Type -Path (Join-Path $PSScriptRoot 'AuthenticodeVerifier.cs') -ReferencedAssemblies System.dll,System.Security.dll,System.Core.dll
Add-Type -Path (Join-Path $PSScriptRoot 'TimestampVerifier.cs') -ReferencedAssemblies System.dll,System.Security.dll,System.Core.dll
[IO.Directory]::CreateDirectory($ReportDirectory)|Out-Null
$id=[Guid]::NewGuid().ToString('N')
$reportPath=Join-Path $ReportDirectory ('InstallerSigning-'+$id+'.json')
$record=[ordered]@{
    schemaVersion=1;editor='YRT-GPT';target=$target;certificateThumbprint=$thumb
    localStartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');localCompletedAtUtc=$null
    localTimeMeaning='Unsigned local system-clock operation record, not a trusted timestamp.'
    trustedTimestampRequested=$false;trustedTimestampObtained=$false
    privateKeyExported=$false;certificateTrustChanged=$false;certificateCreated=$false
    windowsSignatureStatus=$null;independentVerification=$null;embeddedTimeInspection=$null
    sha256=$null;passed=$false;error=$null
}
$signTarget=$target
$temporary=$null
try {
    # Some SIP registrations reject Inno's .e32/.tmp suffix even when the file
    # is PE. Sign an identical .exe copy, then return its complete signed bytes.
    if([IO.Path]::GetExtension($target) -notin @('.exe','.dll')){
        $temporary=Join-Path (Split-Path -Parent $target) ('yrt-sign-'+$id+'.exe')
        [IO.File]::WriteAllBytes($temporary,$bytes)
        $signTarget=$temporary
    }
    $null=Set-AuthenticodeSignature -LiteralPath $signTarget -Certificate $cert -HashAlgorithm SHA256 -IncludeChain NotRoot
    $record.independentVerification=[YrtReleaseSigning.AuthenticodeVerifier]::Verify($signTarget,$thumb)
    $record.embeddedTimeInspection=[YrtReleaseSigning.TimestampVerifier]::Inspect($signTarget)
    if($record.embeddedTimeInspection.TimestampKind -ne 'None'){throw 'An unexpected timestamp is embedded.'}
    $windows=Get-AuthenticodeSignature -LiteralPath $signTarget
    $record.windowsSignatureStatus=$windows.Status.ToString()
    if($null -eq $windows.SignerCertificate -or $windows.SignerCertificate.Thumbprint -ne $thumb){throw 'The embedded signer does not match YRT-GPT.'}
    if($temporary){[IO.File]::WriteAllBytes($target,[IO.File]::ReadAllBytes($temporary))}
    $record.independentVerification=[YrtReleaseSigning.AuthenticodeVerifier]::Verify($target,$thumb)
    $record.sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $record.passed=$true
} catch {
    $record.error=$_.Exception.Message
    throw
} finally {
    $record.localCompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $record|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $reportPath -Encoding UTF8
    if($temporary -and [IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
}
