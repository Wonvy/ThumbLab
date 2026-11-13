# ThumbLab

<img src="./ThumbLab.png" alt="ThumbLab 图标 / ThumbLab app icon" width="96" height="96">

Windows 缩略图管理与修复工具 · A Windows thumbnail manager and repair utility

[简体中文](#简体中文) · [English](#english)

## 简体中文

ThumbLab 帮你检查资源管理器为不同文件类型使用的缩略图处理器，用实际文件验证缩略图能否生成，并在需要时切换处理器。测试过的文件会在尝试修复后自动复测。

### 功能

- 按扩展名搜索，筛选有或没有缩略图处理器的文件类型，在列表和图标视图之间切换。
- 拖入或选择一个文件，测试 Windows 能否生成缩略图。文件扩展名不同时，可切换到对应类型继续测试。
- 查看当前处理器、DLL 路径和注册表位置。可以尝试修复、手动切换处理器或撤销本次运行中应用的关联更改。
- 勾选多个文件类型，导出为 `.thumblab` 配置。导入前可查看即将应用或跳过的配置。
- 支持浅色和深色主题，启动时跟随系统。

### 使用方法

1. 在 x64 Windows 上运行 `ThumbLab.exe`。
2. 从左栏选一个扩展名，再拖入或选择样本文件进行测试。
3. 如果测试失败，可尝试修复或在高级设置中选择其他已安装的处理器，然后用同一个文件验证。

普通配置只迁移关联设置，目标电脑需要先安装相应处理器。若供应商支持独立部署，可按[组件包制作说明](./WinUI/ProviderPackages.md)准备 ZIP 文件，再通过「导入组件包」安装。安装新组件需要管理员权限。

### 从源码发布

在 Windows 上使用 .NET 8 SDK 或兼容的较新版本，从仓库根目录运行下方的[发布命令](#发布命令--build-command)。生成的 `ThumbLab.exe` 为自包含 x64 单文件。

## English

ThumbLab shows which thumbnail handler File Explorer uses for each file type. It lets you test a real file and switch handlers when thumbnails fail to generate.

### Features

- Search by extension, filter file types by thumbnail-handler availability, and switch between list and icon views.
- Drop or choose a file to test thumbnail generation. If its extension differs from the current selection, ThumbLab can switch to that file type before testing.
- Inspect the active handler, DLL path, and registry locations. Try a repair, choose another installed handler, or undo association changes made during the current session.
- Select multiple file types to export as a `.thumblab` configuration file. Preview which settings will be applied or skipped when importing.
- Switch between light and dark themes. ThumbLab follows the system theme at startup.

### Usage

1. Run `ThumbLab.exe` on x64 Windows.
2. Select an extension in the sidebar, then drop or choose a sample file to test.
3. If the test fails, try a repair or select another installed handler in Advanced Settings. Test the same file again to verify the change.

Configuration files migrate associations. Install the corresponding handlers on the destination PC before importing. For components that the vendor supports deploying independently, prepare a ZIP using the [component package guide](./WinUI/ProviderPackages.md) in Chinese, then choose Import Component Package. Administrator rights are required to install new components.

### Build from source

On Windows, use the .NET 8 SDK or a compatible newer version. Run the command below from the repository root to publish a self-contained, single-file x64 `ThumbLab.exe`.

## 发布命令 / Build command

```powershell
dotnet publish .\WinUI\ThumbLab.WinUI.csproj -c Release -o .\work\publish
```

程序输出到 `work/publish/ThumbLab.exe`。 · The executable is written to `work/publish/ThumbLab.exe`.
