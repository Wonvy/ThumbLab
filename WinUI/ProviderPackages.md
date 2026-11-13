# ThumbLab 组件包

组件包用于把支持独立部署的缩略图组件连同扩展名关联一起迁移。它采用 ZIP 格式，与只保存设置的 `.thumblab` 配置文件分别导入。

## 准备组件

1. 从发布方获取支持独立部署的 x64 缩略图组件，确认其许可允许你复制或分发。若发布方要求使用安装程序，就在目标电脑上安装原组件，然后导入普通 ThumbLab 配置。
2. 根据发布方的部署说明确认 COM 类的 CLSID、`ThreadingModel`、可处理的扩展名，以及 DLL 所需的辅助文件。原电脑可在 ThumbLab 的「当前配置」和「高级设置」查看 DLL 位置与注册表信息。当前注册表中的路径仅用于定位源文件。
3. 在工作目录内按照发布方要求放置 DLL 和随附文件，每个组件保留自己的子目录。确认要打包的 DLL 和辅助 EXE 都有目标电脑能验证的 Authenticode 签名。
4. 用 PowerShell 的 `Get-FileHash -Algorithm SHA256 -LiteralPath <文件路径>` 计算每个文件的 SHA-256，将结果填到清单中。
5. 将 UTF-8 编码的 `manifest.json` 与组件子目录一起压缩。打开 ZIP 时，应直接看到 `manifest.json`，无需进入外层文件夹。

在示例工作目录中，可用 PowerShell 打包这两项。

```powershell
Compress-Archive -LiteralPath '.\manifest.json', '.\components' -DestinationPath '.\ThumbLab-Providers.zip'
```

## 版本 1 清单

以下是示例目录与清单。请将示例名称、CLSID、扩展名、线程模型与哈希换成实际组件的数据。

```text
manifest.json
components/
  example/
    Provider.dll
```

```json
{
  "version": 1,
  "providers": [
    {
      "name": "Example Thumbnail Provider",
      "clsid": "{11111111-2222-3333-4444-555555555555}",
      "threadingModel": "Apartment",
      "dll": "components/example/Provider.dll",
      "extensions": [".abc"]
    }
  ],
  "files": [
    {
      "path": "components/example/Provider.dll",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
    }
  ]
}
```

- `providers` 为需要导入的处理器。每个处理器填入发布方指定的 COM 类和扩展名。主 DLL 应提供 COM 类工厂，可由标准 `InprocServer32` 注册加载。
- `files` 为包中全部组件文件。路径相对 ZIP 根目录，使用 `/` 分隔，目录与文件名使用英文字母、数字、空格、下划线、点或连字符。每个文件都需对应一条 SHA-256 记录。ZIP 中只保留文件条目和清单，不添加空目录。
- 每包最多包含 32 个处理器和 256 个组件文件。单个组件文件最多 64 MiB，组件文件总解压大小最多 256 MiB，清单最多 128 KiB。组件文件限 x64 DLL 或 EXE。同一扩展名在一个包内只能关联一个处理器。
- 需要专用安装程序、额外系统组件、许可激活或其他复杂注册步骤的处理器，应按发布方的安装说明部署。

## 在另一台电脑上导入

打开 ThumbLab 的「导入组件包」，选择 ZIP 并检查即将应用的扩展名和处理器。需要安装组件时，目标目录位于 Windows 的 `Program Files\ThumbLab\Providers` 下，安装需要管理员权限。若本机已有可用的同一 CLSID，ThumbLab 使用现有组件。若已有注册但 DLL 缺失，请先修复原组件。导入后选一个实际文件测试缩略图。

「撤销上次修改」用于恢复当前用户的扩展名关联。已安装的 DLL 和 COM 注册仍可供其他扩展名使用。

哈希用于校验组件文件与清单是否一致，Authenticode 用于核对数字签名。组件支持的格式、独立部署条件和分发许可由发布方提供。第三方组件更新后，应以新版发布文件重新制作组件包。

[微软缩略图处理器文档](https://learn.microsoft.com/windows/win32/shell/thumbnail-providers) · [COM 注册文档](https://learn.microsoft.com/windows/win32/com/inprocserver32) · [Authenticode 验证文档](https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
