# Editor: YRT-GPT. Build v1.0 with the declared GitHub release date.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'Build' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
if ($release.release_date -notmatch '^\d{4}-\d{2}-\d{2}$' -or $release.date_timezone -ne 'Asia/Shanghai') {
    throw 'release.json must specify a release_date in yyyy-MM-dd and date_timezone Asia/Shanghai.'
}
$date = [DateTime]::ParseExact($release.release_date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
if ($release.published_at) {
    if ($release.published_at -is [DateTime]) {
        $published = [DateTimeOffset]$release.published_at
    } else {
        $published = [DateTimeOffset]::Parse($release.published_at, [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($published.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -ne $release.release_date) {
        throw 'release_date does not match the original GitHub published_at in Asia/Shanghai.'
    }
}
$src = Join-Path $PSScriptRoot 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework x64 C# compiler was not found.' }
$generated = Join-Path $OutputDirectory 'ReleaseInfo.g.cs'
$generatedText = @"
// Generated from release.json. Never use the build clock as the release date.
namespace YrtMusic
{
    internal static class ReleaseInfo
    {
        internal const string Date = "$($release.release_date)";
        internal const string TimeZone = "Asia/Shanghai";
    }
}
"@
[IO.File]::WriteAllText($generated, $generatedText, [Text.UTF8Encoding]::new($false))
$exe = Join-Path $OutputDirectory 'YRT-GPT_MusicBatch.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', ('/win32manifest:' + (Join-Path $src 'app.manifest')), ('/out:' + $exe))
$arguments += Get-ChildItem -LiteralPath $src -Filter '*.cs' | Select-Object -ExpandProperty FullName
$arguments += $generated
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne $release.assembly_version) { throw 'Built application version differs from release.json.' }
Write-Output $exe
