# Shapr3D 模型缩略图

独立的 Windows x64 文件资源管理器缩略图处理器。它显示与项目配对的模型截图。没有配对图片的普通 `.shapr` 项目显示文件图标。

## 安装与配对

1. 解压发布包，以管理员身份运行 `install.cmd`。处理器为当前 Windows 账号启用，并保留文件默认打开方式。
2. 在 Shapr3D 打开项目，使用「导出 → 图像 → 截图」保存模型视图为 PNG 或 JPEG。
3. 在发布包目录中打开 PowerShell，以平时使用资源管理器的账号运行以下命令。

   ```powershell
   .\set-preview.cmd -Project 'D:\Models\example.shapr' -Image 'D:\Models\example.png'
   ```

4. 在资源管理器切换到「大图标」。如果仍显示旧缓存，可以把项目复制为一个新文件名。相同内容的项目副本会复用已配对的图片。

配对图片存放在本地用户目录。项目文件以只读方式访问，改名或移动后仍可使用同一张图片。项目重新导出后，若内容有变化，请重新配对。配对支持最大 256 MiB 的项目文件和最大 16 MiB 的 PNG 或 JPEG。

删除配对可运行以下命令。

```powershell
.\set-preview.cmd -Project 'D:\Models\example.shapr' -Remove
```

卸载时以同一 Windows 账号的管理员权限运行 `uninstall.cmd`。脚本会恢复安装前的缩略图关联。已配对的图片保留在本地，可供重新安装后使用。

## 从源码构建

需要 Visual Studio 2022 的「使用 C++ 的桌面开发」工作负载（含 Windows SDK）和 CMake 3.20 或更新版本。

```powershell
cmake -S .\ShaprThumbnail -B .\work\ShaprThumbnail -G "Visual Studio 17 2022" -A x64
cmake --build .\work\ShaprThumbnail --config Release
ctest --test-dir .\work\ShaprThumbnail -C Release --output-on-failure
```

构建输出位于 `work\ShaprThumbnail\Release\`。有 Python 3 时，测试也会生成带预览图的合成 ZIP 样本。除本地配对外，处理器支持自行封装的预览条目 `model-preview.png` 和 `model-preview.jpg`。常规 Shapr3D 导出可能只包含工作区数据库，本 DLL 尚不能自动渲染其中的 Parasolid 实体。ZIP 解压使用 Mark Adler 的 Puff 2.3，许可证随源码和发布包提供。

[Shapr3D 截图工具说明](https://support.shapr3d.com/hc/en-us/articles/7874523532956-Screenshot-tool)
