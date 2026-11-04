#requires -Version 5.1
# Editor: YRT-GPT. Local build only; never uploads a file or publishes a release.
param(
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [string]$EvidenceDirectory,
    [switch]$NoSign
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
$payload=[IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$compiler=[IO.Path]::GetFullPath($CompilerPath)
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path $output 'installer-build-evidence'}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if($output -eq $payload -or $output.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Installer output must be outside the complete payload.'
}
$release=Get-Content -LiteralPath (Join-Path $repository 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($release.display_version -ne '1.1' -or $release.assembly_version -ne '1.1.0.0'){
    throw 'This installer project accepts only the historical v1.1 version.'
}
$application=Join-Path $payload 'YRT-GPT_MusicDownloader.exe'
foreach($required in @($application,(Join-Path $payload 'README.txt'),(Join-Path $payload 'LICENSE'),$compiler)){
    if(-not (Test-Path -LiteralPath $required -PathType Leaf)){throw ('Missing installer input: '+$required)}
}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($application).FileVersion -ne $release.assembly_version){
    throw 'The actual payload executable differs from release.json.'
}
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File)
$rejected=@($files | Where-Object {
    $_.Extension.ToLowerInvariant() -in @('.mp3','.pfx','.p12','.key','.zip') -or
    $_.FullName -match '[\\/](data|chrome-profile|node_modules|\.git)[\\/]'
})
if($rejected.Count){throw 'Payload contains runtime data, a private key, an archive, audio, or repository internals.'}
$compilerSignature=Get-AuthenticodeSignature -LiteralPath $compiler
if($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*'){
    throw 'The Inno Setup compiler publisher signature did not validate.'
}
New-Item -ItemType Directory -Path $output,$evidence -Force | Out-Null
$job=Join-Path $evidence ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $job -Force | Out-Null
$arguments=@('/Qp',('/DPayloadRoot='+$payload),('/DReleaseOutput='+$output),('/DAppVersion='+$release.display_version),('/DAssemblyVersion='+$release.assembly_version))
$thumb=$null
if(-not $NoSign){
    $signature=Get-AuthenticodeSignature -LiteralPath $application
    if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -ne 'CN=YRT-GPT'){
        throw 'The payload must first be signed with the expected YRT-GPT identity.'
    }
    $thumb=$signature.SignerCertificate.Thumbprint
    $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $callback=Join-Path $PSScriptRoot 'signing\Sign-GeneratedFile.ps1'
    $command='$q'+$powershell+'$q -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $q'+$callback+'$q -FilePath $f -CertificateThumbprint '+$thumb+' -BuildRoot $q'+$job+'$q -OutputRoot $q'+$output+'$q -ReportDirectory $q'+$job+'$q'
    $arguments+=@('/DSignedBuild=1',('/Syrtgpt='+$command))
}
$arguments+=(Join-Path $PSScriptRoot 'YRTMusic.iss')
function Quote-Native([string]$value){'"'+[Regex]::Replace([Regex]::Replace($value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$compiler
$start.Arguments=($arguments | ForEach-Object{Quote-Native $_}) -join ' '
$start.WorkingDirectory=$PSScriptRoot
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.EnvironmentVariables['TEMP']=$job
$start.EnvironmentVariables['TMP']=$job
$process=New-Object Diagnostics.Process
$process.StartInfo=$start
$started=[DateTimeOffset]::UtcNow
if(-not $process.Start()){throw 'The installer compiler did not start.'}
$stdout=$process.StandardOutput.ReadToEndAsync()
$stderr=$process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$log=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
[IO.File]::WriteAllText((Join-Path $job 'Compiler.log'),$log,[Text.UTF8Encoding]::new($false))
if($process.ExitCode -ne 0){throw ('Inno Setup compile failed: '+$process.ExitCode+[Environment]::NewLine+$log)}
$setup=Join-Path $output 'YRTMusic-Setup-v1.1-x64.exe'
if(-not (Test-Path -LiteralPath $setup -PathType Leaf)){throw 'The compiler returned zero without its expected output.'}
$setupSignature=Get-AuthenticodeSignature -LiteralPath $setup
if(-not $NoSign -and (-not $setupSignature.SignerCertificate -or $setupSignature.SignerCertificate.Thumbprint -ne $thumb)){
    throw 'The installer signer differs from the payload signer.'
}
$record=[ordered]@{
    schemaVersion=1;editor='YRT-GPT';displayVersion='1.1';assemblyVersion='1.1.0.0'
    startedAtUtc=$started.ToString('o');completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    releaseStatus=$release.status;formalReleaseDate=$release.release_date;releasePublishedAt=$release.published_at;githubPublishedByThisScript=$false
    passed=$true;compilerVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).FileVersion
    compilerSha256=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    applicationSha256=(Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash
    installer='YRTMusic-Setup-v1.1-x64.exe';installerSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    installerBytes=(Get-Item -LiteralPath $setup).Length;signed=(-not $NoSign);signedUninstaller=(-not $NoSign)
    windowsSignatureStatus=[string]$setupSignature.Status;certificateThumbprint=$thumb
    compilerExitCode=$process.ExitCode;payloadFileCount=$files.Count;privateKeyExported=$false
    certificateTrustChanged=$false;timestampRequested=$false;trustedTimestampObtained=$false
}
[IO.File]::WriteAllText((Join-Path $evidence 'BuildRecord.json'),($record|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
Write-Output $setup
