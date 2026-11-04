# YRT Music v1.1

**Official release date: 2026-11-04 (Asia/Shanghai).**

**正式发布日期：2026-11-04（Asia/Shanghai）。**

Tag / 标签：`v1.1`  
Application file version / 程序文件版本：`1.1.0.0`

v1.1 changes the workflow to search first and let you choose which results to download. Enter an artist or part of a song title, review the returned songs, then download one result or a selected group.

v1.1 改为先搜索，再由你选择下载。输入歌手或歌名片段，查看返回的歌曲后，可逐首下载，也可勾选多首下载。

## Changes / 更新内容

- Search results and downloads are separate; searching does not download automatically. / 搜索与下载分开，搜索不会自动下载。
- Select individual songs or multiple results, and browse the site's actual result pages. / 支持逐首、勾选多首下载及网站实际分页。
- Choose and remember the download folder. / 自选下载目录并保留选择。
- Run normal searches and downloads through independent background Chrome; open its window manually when the page needs attention. / 独立 Chrome 默认后台运行，需要处理网页时主动打开。
- Use 12, 14, or 16 pt text, with 14 pt as the default. / 默认 14 磅，可切换为 12 或 16 磅。
- Provide an installer and portable ZIP, plus application source in the repository. / 提供安装包、便携 ZIP 和仓库中可直接浏览的源码。

## Files / 下载文件

| File / 文件 | Description / 说明 |
| --- | --- |
| `YRTMusic-Setup-v1.1-x64.exe` | Windows x64 installer for the current user; selectable folder; Simplified Chinese, Traditional Chinese, and English installation wizard. / 当前用户 x64 安装包，可自选目录，安装向导支持简体、繁体、English。 |
| `YRTMusic-v1.1.zip` | Portable folder with application source; also available in the repository's root file list. / 附应用源码的便携包，同时放入仓库首页文件列表。 |

Windows 10 / 11, 64-bit; .NET Framework 4.8; installed Google Chrome. The application interface is Chinese. Run `YRT-GPT_MusicDownloader.exe` after installation or complete extraction.

需要 Windows 10 / 11 64 位、.NET Framework 4.8 和已安装的 Google Chrome。程序界面为中文。安装完成或完整解压后，运行 `YRT-GPT_MusicDownloader.exe`。

## Verification / 核验状态

The application was rebuilt and signed. The packaged source rebuilt successfully, and release-date consistency checks passed. Actual app UI and About checks passed at 12, 14 and 16 pt; one live Beyond search returned 15 results without downloading. This run did not verify audio downloads. Installer validation is recorded separately in the local review report. Historical v1.1 test records remain historical evidence, not a current download test.

本轮已完成程序重新构建与签署、随包源码重建、发布日期一致性检查；12、14、16磅真实界面及关于页通过。一轮 Beyond 实际搜索返回15首，未自动下载；本轮没有实测音频下载。安装器验收另记本地审核报告。原版测试记录保留为历史证据，不代替本次下载测试。

The earlier v1.0.0 release remains available under its original `v1.0` tag, with the official release date **2026-10-31**. The v1.1 official date 2026-11-04 is based on GitHub's actual `published_at`: `2026-11-04T09:15:12Z`, converted to Asia/Shanghai.

此前 v1.0.0 保留原标签 `v1.0` 及正式发布日期 **2026-10-31**。v1.1 正式日期 2026-11-04 依据 GitHub 实际 `published_at`：`2026-11-04T09:15:12Z`，按上海时间换算。
