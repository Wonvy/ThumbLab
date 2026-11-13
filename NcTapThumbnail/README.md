# NC / TAP 缩略图处理器

适用于 x64 Windows 10/11 的独立 DLL。安装后，文件资源管理器可为 `.nc` 和 `.tap` 中的 CNC G-code 生成 XY 俯视刀路缩略图。青色表示进给轨迹，灰蓝色表示快速移动。这个项目可以单独构建和安装，无需安装 ThumbLab。

## 安装与卸载

1. 解压发布包，用平时使用资源管理器的 Windows 账号，以管理员身份运行 `install.cmd`。保持脚本和 DLL 在同一目录。也可以在管理员终端运行 `install.cmd "C:\path\NcTapThumbnail.dll"`。
2. 在资源管理器打开 `samples`，切换到「大图标」或「超大图标」，查看两个样本。若旧文件仍显示原图标，可用一个新文件名复制样本再试，并检查文件夹选项中的「始终显示图标，从不显示缩略图」是否已取消勾选。
3. 卸载时，以同一 Windows 账号的管理员权限运行 `uninstall.cmd`。脚本会恢复安装前的当前用户缩略图关联，保留之后由其他程序改写的关联。若系统仍占用 DLL，请按脚本提示处理。

`.nc` 也用于 NetCDF 等数据文件。处理器只绘制能识别为 CNC 刀路的内容。Windows 对没有可绘制 XY 轨迹的文件使用普通图标。安装脚本只设置缩略图处理器，保留文件的默认打开方式。

## 从源码构建

需要 Visual Studio 2022 的「使用 C++ 的桌面开发」工作负载（含 Windows SDK）及 CMake 3.20 或更新版本。在仓库根目录运行：

```powershell
cmake -S .\NcTapThumbnail -B .\work\NcTapThumbnail -G "Visual Studio 17 2022" -A x64
cmake --build .\work\NcTapThumbnail --config Release
ctest --test-dir .\work\NcTapThumbnail -C Release --output-on-failure
```

DLL 和安装、卸载脚本输出到 `work\NcTapThumbnail\Release\`。手动分发时，请将 `NcTapThumbnail.dll`、`install.cmd`、`uninstall.cmd` 和 `Registration.ps1` 放在同一目录。DLL 使用静态 C++ 运行库。

## G-code 支持范围

支持 G0/G1 直线移动、G2/G3 圆弧，以及常见的公英制、绝对和增量坐标模式。刀路以 XY 平面投影显示，无法表示纯 Z 轴的升降。文件最大为 16 MiB，特别复杂的刀路只绘制前 125,000 条线段（圆弧也会拆分成线段）。宏、子程序和固定循环等控制器专用逻辑不在解析范围内，缩略图仅用于浏览文件。

[微软的 Windows 缩略图处理器文档](https://learn.microsoft.com/windows/win32/shell/thumbnail-providers)
