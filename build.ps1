# Editor: YRT-GPT. Release dates come only from release.json, never the build clock.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'Build' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($release.date_timezone -ne 'Asia/Shanghai' -or $release.assembly_version -ne '1.1.0.0' -or $release.tag -ne 'v1.1') {
    throw 'Unexpected release identity or timezone in release.json.'
}
$displayDate = '待正式发布'
$dateValue = ''
if ($release.status -eq 'Published') {
    if (-not $release.published_at -or $release.release_date -notmatch '^\d{4}-\d{2}-\d{2}$' -or -not $release.release_id) {
        throw 'Published releases require the actual GitHub published_at, release_id and release_date.'
    }
    $published = [DateTimeOffset]::Parse([string]$release.published_at, [Globalization.CultureInfo]::InvariantCulture)
    if ($published.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -ne $release.release_date) {
        throw 'release_date differs from the actual GitHub published_at in Asia/Shanghai.'
    }
    $dateValue = $release.release_date
    $displayDate = $dateValue + '（Asia/Shanghai）'
} elseif ($release.status -ne 'UnpublishedCandidate' -or $release.release_date -or $release.published_at -or $release.release_id) {
    throw 'An unpublished candidate must not claim a formal publication date or release identity.'
}
$history = 'v1.1 — ' + $displayDate
foreach ($previous in $release.historical_releases) {
    $published = [DateTimeOffset]::Parse([string]$previous.published_at, [Globalization.CultureInfo]::InvariantCulture)
    if ($published.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -ne $previous.release_date) { throw 'Historical release date mismatch.' }
    $history += "`r`n" + 'v' + $previous.display_version + ' — ' + $previous.release_date
}
function ConvertTo-CSharpString([string]$Value) {
    return $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n')
}
$generatedText = @"
// Generated from release.json. Never edit this file to invent a publication date.
namespace YrtMusic
{
    internal static class ReleaseInfo
    {
        internal const string Date = "$(ConvertTo-CSharpString $dateValue)";
        internal const string DisplayDate = "$(ConvertTo-CSharpString $displayDate)";
        internal const string TimeZone = "Asia/Shanghai";
        internal const string Status = "$(ConvertTo-CSharpString $release.status)";
        internal const string History = "$(ConvertTo-CSharpString $history)";
    }
}
"@
$generated = Join-Path $OutputDirectory 'ReleaseInfo.g.cs'
[IO.File]::WriteAllText($generated, $generatedText, [Text.UTF8Encoding]::new($false))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework x64 C# compiler was not found.' }
$src = Join-Path $PSScriptRoot 'src'
$exe = Join-Path $OutputDirectory 'YRT-GPT_MusicDownloader.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', ('/win32manifest:' + (Join-Path $src 'app.manifest')), ('/out:' + $exe))
$arguments += Get-ChildItem -LiteralPath $src -Filter '*.cs' | Select-Object -ExpandProperty FullName
$arguments += $generated
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne $release.assembly_version) { throw 'Built version does not match release.json.' }
Write-Output $exe
