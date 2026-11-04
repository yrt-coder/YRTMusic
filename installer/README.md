# Build the v1.1 installer

Use Inno Setup 6.7.4 on Windows. The installer contains the complete clean portable payload, including source, and uses a separate v1.1 application identity. It does not replace later local versions.

```powershell
.\installer\Build-Installer.ps1 -PayloadDirectory C:\work\YRTMusic-v1.1 -OutputDirectory C:\work\installer-output -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' -NoSign
```

The root `release.json` supplies the version. An unsigned rebuild is suitable for local development; it is not the signed download. Release signing requires the local YRT-GPT private code-signing certificate. Private keys are never included. The signed build uses a callback that verifies the PE digest and embedded CMS signature without changing certificate trust or requesting a timestamp.

The English and both Chinese installer message files are included with their upstream provenance in `Languages/Sources.json`. The application itself remains in its original Chinese interface.

Publishing and its release-date reconciliation are separate actions. This build script never publishes to GitHub.
