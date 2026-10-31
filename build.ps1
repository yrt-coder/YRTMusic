# Editor: YRT-GPT. Build the historical v1.0 application without signing.
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'Build'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$src=Join-Path $PSScriptRoot 'src'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'The Windows .NET Framework x64 C# compiler was not found.'}
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll',('/win32manifest:'+(Join-Path $src 'app.manifest')),('/out:'+(Join-Path $OutputDirectory 'YRT-GPT_MusicBatch.exe')))
$arguments+=Get-ChildItem -LiteralPath $src -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Application build failed.'}
Write-Output (Join-Path $OutputDirectory 'YRT-GPT_MusicBatch.exe')
