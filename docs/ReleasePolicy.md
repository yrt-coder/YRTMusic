# 发布日期规范

GitHub 正式版本使用实际对外发布日期。编写日、文档编辑日、构建日和签署日不作为版本发布日期。

## 日期来源

仓库根目录的 `release.json` 是程序和发布文档的唯一发布日期来源。正式日期以对应 GitHub Release 的真实 `published_at` 为准，按 `date_timezone` 指定的 `Asia/Shanghai` 时区换算后，将 `release_date` 记为 `YYYY-MM-DD`。`display_version`、`assembly_version`、`tag`、`release_url` 和 `release_id` 对应同一个正式版本。

v1.0.0 对应的现有标签是 `v1.0`。该 Release 的 `published_at` 为 `2026-10-31T05:09:48Z`，换算为上海时间是 `2026-10-31 13:09:48 +08:00`，因此正式发布日期为 **2026-10-31**。

程序“关于”、使用说明、中英文 README、更新日志、Release 说明和下载 ZIP 中的版本文案均须采用这一正式日期。`build.ps1` 从 `release.json` 生成程序使用的发布日期，不能另写一份日期，也不能用执行构建当天的日期替代。

## 正式发布核验

1. 准备发布时，确认版本号、标签和包内文案对应同一版本；正式发布时间尚未确定时，将发布日期保留为待核状态，不能填入编写日。
2. 发布后读取该 GitHub Release 的 `published_at`，换算为上海日期，确认 `release.json` 记录一致。
3. 使用 `build.ps1` 构建，在仓库根目录运行：

   ```powershell
   .\tools\release\Verify-ReleaseDate.ps1 -Root . -ExePath .\Build\YRT-GPT_MusicDownloader.exe -CheckGitHub
   ```

4. 核验必须确认 GitHub 发布记录、`release.json`、构建程序的日期以及发布文档一致。打开实际程序“关于”，并检查 ZIP 内说明和最终下载附件；全部一致后完成发布验收。
5. 若正式发布跨过上海时间的日期边界，或修改了待发布文案，重新生成相关内容并运行核验。

## 已发布版本的日期修正

修正已发布版本时，继续使用该 Release 原有 `published_at`。本次 v1.0.0 日期修正属于现有版本的文案校正，不创建新版本、不重新计算原发布日期，也不重置现有 Release 的 `published_at`。修正操作当天的日期不能冒充原正式发布日期。

编译记录、数字签名时间戳、文件创建与修改时间、提交时间和核验记录保留各自真实时间，不改成版本发布日期。版权年份按实际版权信息保留。

## v1.1 审核候选

v1.1 的 `status` 在正式发布前必须为 `UnpublishedCandidate`，`release_date`、`published_at`、`release_id` 保持为空。关于页、版本历史及文档显示“待正式发布”。不得把历史开发验收的 2026-10-25 当作正式发布日期。

仅检查本地候选可加 `-AllowUnpublishedCandidate`。该检查通过只代表候选信息一致，不代表已经在 GitHub 正式发布。正式发布验收必须使用 `-CheckGitHub`，读取真实的 Release 身份和发布时间，并复核重新构建、重新签署后的程序、安装器、ZIP 和公开下载。
