# 构建 Desktop Dock

使用 Windows 11 x64、.NET 10 SDK、PowerShell 7 和 Inno Setup 7.1.1。若本机同时安装多个 SDK，用 `dotnet --list-sdks` 检查选择。可向安装器构建脚本传入 .NET 10 CLI 的绝对路径。 [Microsoft .NET 下载](https://dotnet.microsoft.com/download/dotnet/10.0) · [Inno Setup 下载](https://jrsoftware.org/inno-setup/downloads)

Inno Setup 上游要求商业用户购买许可。使用时请参照随编译器提供的许可和上游条款。源码归档包含安装器脚本，不附带 Inno Setup 编译器。 [上游许可说明](https://jrsoftware.org/inno-setup/documentation/topic_purchase.htm)

在仓库根目录执行。`$version` 应与应用项目文件中的 `Version` 一致。

```powershell
$version = '1.1.0'
$output = ".\artifacts\releases\$version"
dotnet restore .\src\DesktopDock\DesktopDock.csproj -r win-x64
dotnet build .\DesktopDock.slnx -c Release
dotnet publish .\src\DesktopDock\DesktopDock.csproj -c Release -r win-x64 --self-contained true --no-restore -o "$output\DesktopDock-win-x64"
pwsh -File .\installer\Build-Installer.ps1 -AppDirectory "$output\DesktopDock-win-x64" -OutputDirectory $output -IsccPath 'C:\Program Files\Inno Setup 7\ISCC.exe' -Version $version
```

`ISCC.exe` 路径以本机安装为准。若受限环境无法读取 Microsoft SDK 的平台显示名称，构建和发布可额外传入 `'-p:TargetPlatformMoniker=Windows%2CVersion=7.0'`。此参数只用于 MSBuild 的平台元数据。

创建对应版本的 Git 标签后，可从该标签和发布目录打包源码及便携版。

```powershell
git archive --format=zip --prefix="DesktopDock-$version/" --output="$output\DesktopDock-$version-source.zip" "v$version"
tar.exe -a -cf "$output\DesktopDock-$version-win-x64-portable.zip" -C $output DesktopDock-win-x64
Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.exe','.zip' | Get-FileHash -Algorithm SHA256
```

在 `Diona86/DesktopDock` 发布 GitHub Release 时，使用 `v<版本>` 标签，将 `DesktopDock-<版本>-win-x64-setup.exe` 上传为发行附件，并将它作为稳定版发布。设置页只检查 GitHub 标为最新的公开发行。 [GitHub 发行 API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)

复测命令、覆盖和现场验证项见 [发布验收](PHASE_6_ACCEPTANCE.md)。发布目录的 `SHA256SUMS.txt` 用于核对交付文件，`THIRD-PARTY-NOTICES.txt` 汇集实际使用的 .NET 10.0.15 运行时包及 Inno Setup 7.1.1 的许可与声明。
