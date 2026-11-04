#requires -Version 5.1
# Editor: YRT-GPT. A candidate is never a verified formal release.
[CmdletBinding()]
param(
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [Parameter(Mandatory=$true)][string]$ExePath,
    [string]$PackageDirectory,
    [switch]$AllowUnpublishedCandidate,
    [switch]$CheckGitHub,
    [string]$ReportPath
)
$ErrorActionPreference='Stop'
$meta=Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$checks=[ordered]@{}
function Require([bool]$Condition,[string]$Name){
    if(-not $Condition){throw ('Release verification failed: '+$Name)}
    $checks[$Name]=$true
}
Require ($meta.date_timezone -eq 'Asia/Shanghai') 'Explicit release timezone'
Require ($meta.tag -eq 'v1.1' -and $meta.assembly_version -eq '1.1.0.0') 'Historical v1.1 identity'
$candidate=$meta.status -eq 'UnpublishedCandidate'
$published=$null
if($candidate){
    Require ([bool]$AllowUnpublishedCandidate -and -not $CheckGitHub) 'Candidate check explicitly selected'
    Require (-not $meta.release_date -and -not $meta.published_at -and -not $meta.release_id) 'Candidate claims no publication date'
    foreach($name in @('README.md','README.zh-CN.md','CHANGELOG.md','ReleaseNotes.md')){
        $text=[IO.File]::ReadAllText((Join-Path $Root $name),[Text.Encoding]::UTF8)
        Require ($text -match 'pending|unpublished|待正式发布|待发布|尚待|审核稿') ($name+' states pending publication')
    }
}else{
    Require ($meta.status -eq 'Published') 'Formal publication state'
    Require ([bool]$meta.release_id -and [bool]$meta.published_at -and $meta.release_date -match '^\d{4}-\d{2}-\d{2}$') 'Actual publication evidence'
    $published=[DateTimeOffset]::Parse([string]$meta.published_at,[Globalization.CultureInfo]::InvariantCulture)
    Require ($published.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd') -eq $meta.release_date) 'Date derived from GitHub published_at'
    foreach($name in @('README.md','README.zh-CN.md','CHANGELOG.md','ReleaseNotes.md')){
        $text=[IO.File]::ReadAllText((Join-Path $Root $name),[Text.Encoding]::UTF8)
        Require ($text.Contains([string]$meta.release_date)) ($name+' uses formal release date')
    }
}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExePath))
Require ($asm.GetName().Version.ToString() -eq $meta.assembly_version) 'Actual executable version'
$attributes=@{}
foreach($a in $asm.GetCustomAttributesData()){
    if($a.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){
        $attributes[[string]$a.ConstructorArguments[0].Value]=[string]$a.ConstructorArguments[1].Value
    }
}
Require ($attributes['PublicationStatus'] -eq $meta.status) 'Executable publication state'
Require ($attributes['ReleaseDate'] -eq [string]$meta.release_date) 'Executable release date'
Require ($attributes['ReleaseDateTimeZone'] -eq $meta.date_timezone) 'Executable release timezone'
if($PackageDirectory){
    $packageMeta=Get-Content -LiteralPath (Join-Path $PackageDirectory 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $sourceMeta=Get-Content -LiteralPath (Join-Path $PackageDirectory 'Source/release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($item in @($packageMeta,$sourceMeta)){
        Require ($item.status -eq $meta.status -and $item.release_date -eq $meta.release_date -and $item.published_at -eq $meta.published_at) 'Package and source release metadata'
    }
    $packageExe=Join-Path $PackageDirectory 'YRT-GPT_MusicDownloader.exe'
    Require ((Get-FileHash -LiteralPath $packageExe -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash) 'Exact checked application in package'
    $readme=[IO.File]::ReadAllText((Join-Path $PackageDirectory 'README.txt'),[Text.Encoding]::UTF8)
    if($candidate){Require ($readme.Contains('待正式发布')) 'Package declares candidate'}
    else{Require ($readme.Contains([string]$meta.release_date)) 'Package declares formal date'}
}
if($CheckGitHub){
    Require (-not $candidate) 'Only published metadata can be checked against live GitHub'
    $url='https://api.github.com/repos/yrt-coder/YRTMusic/releases/tags/'+[Uri]::EscapeDataString($meta.tag)
    $live=Invoke-RestMethod -Uri $url -Headers @{'User-Agent'='YRTMusic-release-date-verifier';'Accept'='application/vnd.github+json'}
    Require (-not $live.draft -and -not $live.prerelease) 'Formal live GitHub release'
    Require ($live.id -eq $meta.release_id -and $live.tag_name -eq $meta.tag) 'Original live release identity'
    $liveTime=[DateTimeOffset]::Parse([string]$live.published_at,[Globalization.CultureInfo]::InvariantCulture)
    Require ($liveTime.UtcDateTime.Ticks -eq $published.UtcDateTime.Ticks) 'Original live publication time preserved'
}
$result=[ordered]@{passed=$true;scope=if($candidate){'UnpublishedCandidateOnly'}else{'FormalRelease'};formalReleaseVerified=([bool]$CheckGitHub -and -not $candidate);releaseDate=$meta.release_date;publishedAt=$meta.published_at;githubChecked=[bool]$CheckGitHub;checks=$checks;executableSha256=(Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash;checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$json=$result|ConvertTo-Json -Depth 8
if($ReportPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),$json+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))}
Write-Output $json
