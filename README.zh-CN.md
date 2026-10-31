<div align="center">
  <img src="docs/images/logo.png" width="88" alt="YRT Music 图标">
  <h1>YRT Music</h1>
  <p>按行搜索歌名，保存匹配的试听 MP3。</p>
  <p><a href="README.md">English</a> · <strong>简体中文</strong></p>
  <p>
    <a href="https://github.com/yrt-coder/YRTMusic/releases/tag/v1.0"><img src="https://img.shields.io/badge/version-1.0.0-087f5b" alt="版本 1.0.0"></a>
    <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-1677c8" alt="Windows 10 和 11，64 位">
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6366a3" alt="MIT 许可证"></a>
  </p>
  <p><a href="#下载">下载</a> · <a href="#功能">功能</a> · <a href="#使用方法">使用方法</a> · <a href="CHANGELOG.md">更新记录</a> · <a href="#从源码构建">从源码构建</a></p>
</div>

![YRT Music 项目封面](docs/images/hero.png)

*原创品牌插画，真实 Windows 程序截图见下方。*

YRT Music 是一个 Windows 音乐批量下载工具。每行输入一首歌名，程序通过 Chrome 在歌曲海搜索，自动选择匹配结果，将试听 MP3 保存到桌面的“音乐”文件夹。

## 下载

**[下载 YRTMusic-v1.0.zip](https://github.com/yrt-coder/YRTMusic/releases/download/v1.0/YRTMusic-v1.0.zip)**

| 系统 | 下载包 | 运行环境 |
| --- | --- | --- |
| Windows 10 / 11，64 位 | 便携 ZIP，附完整源码 | .NET Framework 4.8 和已安装的 Chrome |

解压整个 ZIP，再运行 `YRT-GPT_MusicBatch.exe`。

本页将历史版本显示为 **v1.0.0**，已有 GitHub 标签仍是 **v1.0**，两者指向同一个下载包。[查看全部版本](https://github.com/yrt-coder/YRTMusic/releases)。

## 功能

- 粘贴歌单或导入 TXT，每行一首。
- 通过 Chrome 逐项搜索并自动匹配歌曲。
- 将试听 MP3 保存到桌面的“音乐”文件夹，并显示每项处理结果。
- 支持停止；遇到网站验证时，可处理后继续。
- 默认 14 磅字号，可切换为 12 或 16 磅。

<p align="center">
  <img src="docs/images/app-v1.0.0.png" width="960" alt="YRT Music v1.0.0 的真实 Windows 界面">
</p>

*真实 Windows 程序截图，本版本使用中文按钮。*

## 使用方法

1. 在解压后的文件夹中打开 `YRT-GPT_MusicBatch.exe`。
2. 粘贴歌单，每行一首，也可以导入 TXT。写上歌手名有助于匹配。
3. 点击“开始搜索并下载”。
4. 查看任务列表，再点击“打开音乐文件夹”查看已保存的文件。

搜索时会打开 Chrome 窗口。下载时保持程序和 Chrome 开启。遇到网站验证，先在 Chrome 中处理，再点“继续任务”；需要结束时点“停止”。

## 从源码构建

克隆仓库后，在仓库根目录使用 PowerShell 构建：

```powershell
git clone https://github.com/yrt-coder/YRTMusic.git
cd YRTMusic
.\build.ps1 -OutputDirectory .\Build
```

应用源码和 Windows 清单位于 `src/`。脚本使用 Windows 的 .NET Framework C# 编译器，在 `Build/YRT-GPT_MusicBatch.exe` 生成未签署程序，无需 NuGet 包或额外 SDK。

`tools/signing/` 保留可选的发布辅助工具，不参与应用编译。下载 ZIP 中也保留了完整的 `Source` 目录。

## 支持

通过 [GitHub Issues](https://github.com/yrt-coder/YRTMusic/issues)反馈问题或提出功能建议。

## 许可证

软件采用 [MIT 许可证](LICENSE)，版权归 2026 **YRT-GPT**。

请仅下载你有权保存的音乐。音乐内容的权利不随软件许可证授予。
