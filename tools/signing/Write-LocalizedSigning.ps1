#requires -Version 5.1
<#
.SYNOPSIS
Writes English, Simplified Chinese and Traditional Chinese signing records.
.DESCRIPTION
Author: YRT-GPT. Reads an existing successful offline Signing.json only.
It does not sign a file, use a private key, contact a network service, or
independently verify the current executable. All displayed facts come from
the supplied report. The input JSON is never modified.
.EXAMPLE
.\Write-LocalizedSigning.ps1 -SigningJson 'D:\BuildOutput\Info\Signing.json' -OutputDirectory 'D:\BuildOutput\Info'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SigningJson,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Required-Value {
    param([object]$Object, [string]$Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { throw ('Missing signing-report field: ' + $Name) }
    return ,($property.Value)
}

function Required-Text {
    param([object]$Object, [string]$Name)
    $value = Required-Value $Object $Name
    if ($value -isnot [string] -or [String]::IsNullOrWhiteSpace($value)) { throw ('Invalid text field: ' + $Name) }
    return $value
}

function Assert-Boolean {
    param([object]$Object, [string]$Name, [bool]$Expected)
    $value = Required-Value $Object $Name
    if ($value -isnot [bool] -or $value -ne $Expected) { throw ('Unexpected boolean value for offline signing report: ' + $Name) }
}

function Parse-Time {
    param([string]$Value, [string]$Name, [int]$OffsetHours)
    $parsed = [DateTimeOffset]::MinValue
    $valid = $Value -match '(Z|[+-]\d{2}:\d{2})$' -and [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)
    if (-not $valid -or $parsed.Offset -ne [TimeSpan]::FromHours($OffsetHours)) { throw ('Invalid time or UTC offset: ' + $Name) }
    return $parsed
}

$source = [IO.Path]::GetFullPath($SigningJson)
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (-not [IO.File]::Exists($source) -or [IO.Path]::GetExtension($source) -ine '.json') { throw 'SigningJson must identify an existing JSON report.' }
$json = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $record = $json | ConvertFrom-Json -DateKind String }
else { $record = $json | ConvertFrom-Json }
Assert-Boolean $record 'passed' $true
Assert-Boolean $record 'trustedTimestampObtained' $false
Assert-Boolean $record 'timestampRequested' $false
Assert-Boolean $record 'privateKeyExported' $false
Assert-Boolean $record 'installedIntoRootOrTrustedPublisher' $false

$version = Required-Text $record 'applicationVersion'
$editor = Required-Text $record 'editor'
$signer = Required-Text $record 'signer'
if ($editor -cne 'YRT-GPT' -or $signer -cne 'YRT-GPT') { throw 'The signing report does not identify the required YRT-GPT editor and signer.' }
$subject = Required-Text $record 'certificateSubject'
$fingerprint = Required-Text $record 'thumbprint'
$executableHash = Required-Text $record 'executableSha256'
if ($fingerprint -notmatch '^[A-Fa-f0-9]{40}$' -or $executableHash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'The certificate fingerprint or executable SHA-256 is malformed.' }
$keyAlgorithm = Required-Text $record 'privateKeyAlgorithm'
$windowsStatus = Required-Text $record 'windowsSignatureStatus'
$windowsMessage = Required-Text $record 'windowsSignatureStatusMessage'
$selfIssued = Required-Value $record 'certificateIsSelfIssued'
if ($selfIssued -isnot [bool]) { throw 'certificateIsSelfIssued must be a JSON boolean.' }

$startedUtc = Required-Text $record 'localSigningStartedAtUtc'
$completedUtc = Required-Text $record 'localSigningCompletedAtUtc'
$startedShanghai = Required-Text $record 'localSigningStartedAtShanghai'
$completedShanghai = Required-Text $record 'localSigningCompletedAtShanghai'
$notBeforeUtc = Required-Text $record 'certificateNotBeforeUtc'
$notAfterUtc = Required-Text $record 'certificateNotAfterUtc'
$notBeforeShanghai = Required-Text $record 'certificateNotBeforeShanghai'
$notAfterShanghai = Required-Text $record 'certificateNotAfterShanghai'
$startTime = Parse-Time $startedUtc 'localSigningStartedAtUtc' 0
$finishTime = Parse-Time $completedUtc 'localSigningCompletedAtUtc' 0
if ($startTime.UtcDateTime.Ticks -ne (Parse-Time $startedShanghai 'localSigningStartedAtShanghai' 8).UtcDateTime.Ticks -or
    $finishTime.UtcDateTime.Ticks -ne (Parse-Time $completedShanghai 'localSigningCompletedAtShanghai' 8).UtcDateTime.Ticks -or
    $finishTime -lt $startTime) { throw 'The local signing start/completion times do not agree between UTC and UTC+08:00.' }
$validFrom = Parse-Time $notBeforeUtc 'certificateNotBeforeUtc' 0
$validUntil = Parse-Time $notAfterUtc 'certificateNotAfterUtc' 0
if ($validFrom.UtcDateTime.Ticks -ne (Parse-Time $notBeforeShanghai 'certificateNotBeforeShanghai' 8).UtcDateTime.Ticks -or
    $validUntil.UtcDateTime.Ticks -ne (Parse-Time $notAfterShanghai 'certificateNotAfterShanghai' 8).UtcDateTime.Ticks -or
    $validUntil -le $validFrom) { throw 'Certificate validity values do not agree between UTC and UTC+08:00.' }

$inspection = Required-Value $record 'embeddedTimeInspection'
if ((Required-Text $inspection 'TimestampKind') -cne 'None') { throw 'This writer accepts only offline reports with no embedded TSA timestamp.' }
Assert-Boolean $inspection 'TimestampCryptographicallyVerified' $false
$timestamps = Required-Value $inspection 'Timestamps'
if (@($timestamps).Count -ne 0) { throw 'The report contains timestamp tokens; an absent-timestamp statement would be inaccurate.' }
$verification = Required-Value $record 'independentVerification'
Assert-Boolean $verification 'CmsSignatureValid' $true
Assert-Boolean $verification 'AuthenticodeFileDigestMatches' $true
$digestAlgorithm = Required-Text $verification 'FileDigestAlgorithm'
if ($digestAlgorithm -eq 'SHA256') { $digestAlgorithm = 'SHA-256' }
$reportedTime = $inspection.PSObject.Properties['SignerReportedTimeUtc']
$signingTimeEn = 'Not present'
$signingTimeCn = '未包含'
$signingTimeTw = '未包含'
if ($null -ne $reportedTime -and -not [String]::IsNullOrWhiteSpace([string]$reportedTime.Value)) {
    $signingTimeEn = [string]$reportedTime.Value + ' (signer-reported; not a TSA time)'
    $signingTimeCn = [string]$reportedTime.Value + '（签名者自报；不是TSA可信时间）'
    $signingTimeTw = [string]$reportedTime.Value + '（簽署者自行宣告；不是TSA可信時間）'
}
$certificateEn = 'The certificate is self-signed by YRT-GPT; it is not public-CA certification.'
$certificateCn = '证书为YRT-GPT自签名代码签名证书，不是公共CA认证。'
$certificateTw = '憑證為YRT-GPT自我簽署程式碼簽署憑證，不是公用CA認證。'
if (-not $selfIssued) {
    $certificateEn = 'The report identifies a certificate that is not self-issued. Issuer trust must be evaluated separately.'
    $certificateCn = '报告标识的证书不是自颁发证书；签发者信任状态需要另行判断。'
    $certificateTw = '報告標示的憑證不是自行發行憑證；簽發者信任狀態需要另行判斷。'
}

$english = @"
YRT Music v$version — Signing Information

Editor / executable signer: $editor
Certificate subject: $subject
Local signing operation completed (UTC+08:00): $completedShanghai
Local signing operation completed (UTC): $completedUtc
Local signing operation started (UTC+08:00): $startedShanghai
Local signing operation started (UTC): $startedUtc
Time source: the local system clock, captured immediately before and after the actual signing command.
This is an unsigned local operation record, not an embedded signingTime or third-party time proof.

Trusted timestamp: NOT OBTAINED
No signing digest was sent to a third-party timestamp service in this signing operation.
The Windows file-properties timestamp column may remain unavailable.
Primary-signature signingTime (UTC): $signingTimeEn
Traditional counter-signature / RFC3161 timestamp: neither is embedded.
Certificate validity and file modification time are not used as signing time.

Certificate valid from (UTC+08:00): $notBeforeShanghai
Certificate valid until (UTC+08:00): $notAfterShanghai
Certificate valid from (UTC): $notBeforeUtc
Certificate valid until (UTC): $notAfterUtc
Certificate fingerprint (SHA-1 identifier): $fingerprint
Signature digest: $digestAlgorithm. Key: $keyAlgorithm
$certificateEn
Trust may differ between computers. This signing operation did not install a certificate into Root or TrustedPublisher and did not export a private key.
Windows result recorded on the signing machine: $windowsStatus
Windows result message (verbatim): $windowsMessage

Independent CMS signature valid: True
Authenticode file digest matches: True
EXE SHA-256: $executableHash
Source: Signing.json. These are reported verification results; this text-generation step does not re-sign or independently verify the current executable.
The About window displays this local record. It does not automatically hash-check the current executable.
"@

$simplified = @"
YRT Music v$version — 签署信息

编辑者／文件数字签名者：$editor
证书主题：$subject
本地签署完成（北京时间 UTC+08:00）：$completedShanghai
本地签署完成（UTC）：$completedUtc
本地签署开始（北京时间 UTC+08:00）：$startedShanghai
本地签署开始（UTC）：$startedUtc
时间来源：实际签署命令前后读取本机系统时钟。
这是未独立签名的本地操作记录，不是EXE签名内嵌的signingTime，也不是第三方时间认证。

可信时间戳：未取得可信时间戳
本次签署操作没有向任何第三方时间戳服务发送签名摘要。
Windows文件属性中的“时间戳”可能仍显示“不可用”。
主签名signingTime（UTC）：$signingTimeCn
传统计数签名／RFC3161时间戳：均未包含。
证书有效期和文件修改时间均不作为签署时间。

证书有效期开始（北京时间 UTC+08:00）：$notBeforeShanghai
证书有效期结束（北京时间 UTC+08:00）：$notAfterShanghai
证书有效期开始（UTC）：$notBeforeUtc
证书有效期结束（UTC）：$notAfterUtc
证书指纹（SHA-1标识）：$fingerprint
签名摘要算法：$digestAlgorithm；密钥：$keyAlgorithm
$certificateCn
各电脑信任状态可能不同。本次签署流程未向Root或TrustedPublisher存储安装证书，也未导出私钥。
签署机器上记录的Windows结果：$windowsStatus
Windows结果原文：$windowsMessage

独立CMS密码学签名核验：通过
Authenticode文件摘要核验：通过
EXE SHA-256：$executableHash
数据来源：Signing.json。以上是报告记录的核验结果；本说明生成步骤不会重新签名，也不会独立核验当前EXE。
关于窗口展示本地记录，不自动核对当前EXE哈希。
"@

$traditional = @"
YRT Music v$version — 簽署資訊

編輯者／檔案數位簽署者：$editor
憑證主體：$subject
本機簽署完成（北京時間 UTC+08:00）：$completedShanghai
本機簽署完成（UTC）：$completedUtc
本機簽署開始（北京時間 UTC+08:00）：$startedShanghai
本機簽署開始（UTC）：$startedUtc
時間來源：在實際簽署命令前後讀取本機系統時鐘。
這是未獨立簽署的本機操作記錄，不是EXE簽署內嵌的signingTime，也不是第三方時間認證。

可信時間戳記：未取得可信時間戳記
本次簽署操作沒有向任何第三方時間戳記服務傳送簽署摘要。
Windows檔案內容中的「時間戳記」可能仍顯示「無法使用」。
主簽署signingTime（UTC）：$signingTimeTw
傳統副署／RFC3161時間戳記：均未包含。
憑證有效期間和檔案修改時間均不作為簽署時間。

憑證有效期間開始（北京時間 UTC+08:00）：$notBeforeShanghai
憑證有效期間結束（北京時間 UTC+08:00）：$notAfterShanghai
憑證有效期間開始（UTC）：$notBeforeUtc
憑證有效期間結束（UTC）：$notAfterUtc
憑證指紋（SHA-1識別碼）：$fingerprint
簽署摘要演算法：$digestAlgorithm；金鑰：$keyAlgorithm
$certificateTw
各電腦的信任狀態可能不同。本次簽署流程未將憑證安裝至Root或TrustedPublisher存放區，也未匯出私密金鑰。
簽署電腦記錄的Windows結果：$windowsStatus
Windows結果原文：$windowsMessage

獨立CMS密碼學簽署驗證：通過
Authenticode檔案摘要驗證：通過
EXE SHA-256：$executableHash
資料來源：Signing.json。以上是報告記錄的驗證結果；本說明產生步驟不會重新簽署，也不會獨立驗證目前的EXE。
關於視窗顯示本機記錄，不會自動核對目前EXE的雜湊值。
"@

[IO.Directory]::CreateDirectory($destination) | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
$outputs = [ordered]@{ 'Signing.txt' = $english; 'Signing.zh-CN.txt' = $simplified; 'Signing.zh-TW.txt' = $traditional }
foreach ($name in $outputs.Keys) {
    [IO.File]::WriteAllText((Join-Path $destination $name), $outputs[$name].TrimEnd() + [Environment]::NewLine, $encoding)
}
[pscustomobject]@{
    SourceJson = $source
    OutputDirectory = $destination
    Files = @($outputs.Keys)
    ExecutableSha256 = $executableHash
    LocalSigningCompletedAtShanghai = $completedShanghai
    TrustedTimestampObtained = $false
    SourceJsonModified = $false
    SigningOrNetworkOperationPerformed = $false
}
