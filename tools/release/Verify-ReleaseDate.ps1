#requires -Version 5.1
# Editor: YRT-GPT. Fail the release check if publication dates disagree.
[CmdletBinding()]
param(
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$PackageDirectory,
    [switch]$CheckGitHub,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path
$meta = Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw | ConvertFrom-Json
$checks = [ordered]@{}
function Require([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('Release date check failed: ' + $Name) }
    $checks[$Name] = $true
}
function Read-Utf8([string]$Path) { return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) }
function As-Offset($Value) {
    if ($Value -is [DateTime]) { return [DateTimeOffset]$Value }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}
Require ($meta.release_date -match '^\d{4}-\d{2}-\d{2}$') 'ISO release date present'
[void][DateTime]::ParseExact($meta.release_date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
Require ($meta.date_timezone -eq 'Asia/Shanghai') 'Release timezone is explicit'
Require (-not [string]::IsNullOrWhiteSpace([string]$meta.published_at)) 'Official publication evidence present'
$published = As-Offset $meta.published_at
Require ($published.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -eq $meta.release_date) 'Date matches publication evidence'
foreach ($name in @('README.md', 'README.zh-CN.md', 'CHANGELOG.md')) {
    $text = Read-Utf8 (Join-Path $Root $name)
    Require ($text.Contains($meta.release_date)) ($name + ' uses release date')
}
$heading = '^## ' + [regex]::Escape($meta.display_version) + '.*' + [regex]::Escape($meta.release_date)
Require ([regex]::IsMatch((Read-Utf8 (Join-Path $Root 'CHANGELOG.md')), $heading, [Text.RegularExpressions.RegexOptions]::Multiline)) 'Changelog version heading uses release date'
Require ((Read-Utf8 (Join-Path $Root 'src/App.cs')).Contains('ReleaseInfo.Date')) 'About reads generated date'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ExePath).Path)
Require ($assembly.GetName().Version.ToString() -eq $meta.assembly_version) 'Executable version matches metadata'
$attributes = @{}
foreach ($attribute in $assembly.GetCustomAttributesData()) {
    if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
        $attributes[[string]$attribute.ConstructorArguments[0].Value] = [string]$attribute.ConstructorArguments[1].Value
    }
}
Require ($attributes['ReleaseDate'] -eq $meta.release_date) 'Final executable embeds release date'
Require ($attributes['ReleaseDateTimeZone'] -eq $meta.date_timezone) 'Final executable embeds release timezone'
if ($PackageDirectory) {
    $packageMeta = Get-Content -LiteralPath (Join-Path $PackageDirectory 'Info/Release.json') -Raw | ConvertFrom-Json
    Require ($packageMeta.release_date -eq $meta.release_date -and $packageMeta.published_at -eq $meta.published_at) 'Package metadata uses original publication'
    Require ((Read-Utf8 (Join-Path $PackageDirectory 'README.txt')).Contains($meta.release_date)) 'Package readme uses release date'
    Require ((Read-Utf8 (Join-Path $PackageDirectory 'Source/README.txt')).Contains($meta.release_date)) 'Package source readme uses release date'
    Require ((Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'YRT-GPT_MusicBatch.exe') -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash) 'Package contains checked executable'
}
$observed = $null
if ($CheckGitHub) {
    Require ($meta.release_url -match '^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/releases/tag/([^/?#]+)$') 'GitHub release URL is supported'
    $owner = $Matches[1]; $repo = $Matches[2]
    $url = 'https://api.github.com/repos/' + $owner + '/' + $repo + '/releases/tags/' + [Uri]::EscapeDataString($meta.tag)
    $observed = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'YRT-GPT-release-date-check'; 'Accept' = 'application/vnd.github+json' }
    Require (-not $observed.draft -and -not $observed.prerelease) 'Release is formally published'
    Require ($observed.id -eq $meta.release_id -and $observed.tag_name -eq $meta.tag) 'Original release identity is retained'
    $livePublished = As-Offset $observed.published_at
    Require ($livePublished.UtcDateTime.Ticks -eq $published.UtcDateTime.Ticks) 'Original GitHub published_at is unchanged'
    Require ($livePublished.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -eq $meta.release_date) 'Date matches live GitHub release'
}
$result = [ordered]@{
    passed = $true
    releaseDate = $meta.release_date
    dateTimezone = $meta.date_timezone
    publishedAt = $meta.published_at
    githubChecked = [bool]$CheckGitHub
    checks = $checks
    executableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
$json = $result | ConvertTo-Json -Depth 8
if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false)) }
Write-Output $json

