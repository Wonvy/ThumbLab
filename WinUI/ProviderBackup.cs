#nullable enable
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ThumbLabWinUI;

internal sealed class ProviderBackup
{
    public const string ConfigurationEntry = "config.thumblab";
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const long MaxPayloadBytes = 256L * 1024 * 1024;
    private const int MaxManifestBytes = 128 * 1024;
    private const int MaxProviders = 32;
    private const int MaxExtensionsPerProvider = 128;

    private readonly XDocument configuration;
    private readonly Component[] components;

    private sealed record Component(string Clsid, string Name, string ThreadingModel,
        string Source, string Entry, string[] Extensions, long Length, string Hash, bool Installable);

    private ProviderBackup(ThumbnailSettings.ExportPlan plan, Component[] components, string[] configurationOnly)
    {
        configuration = new XDocument(plan.Document);
        this.components = components;
        IncludedComponents = components.Where(item => item.Installable).SelectMany(component =>
            component.Extensions.Select(extension => $"{extension} · {component.Name}")).ToArray();
        BackupOnly = components.Where(item => !item.Installable).SelectMany(item =>
            item.Extensions.Select(ext => $"{ext} · {item.Name}（签名未通过验证）")).ToArray();
        ConfigurationOnly = configurationOnly.Concat(plan.Skipped.Select(ext => $"{ext} · 没有可用的关联配置")).ToArray();
        ConfigCount = plan.Included.Length;
    }

    public string[] IncludedComponents { get; }
    public string[] BackupOnly { get; }
    public string[] ConfigurationOnly { get; }
    public int ConfigCount { get; }

    public static ProviderBackup Prepare(ThumbnailSettings.ExportPlan plan, IProgress<string>? progress = null)
    {
        var included = new List<Component>();
        var configurationOnly = new List<string>();
        long remaining = MaxPayloadBytes;
        string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            + Path.DirectorySeparatorChar;
        using var classes = Registry.ClassesRoot;
        var groups = plan.Document.Root!.Elements("Thumbnail").GroupBy(node => (string)node.Attribute("provider")!);
        var batches = groups.ToArray();
        int checkedCount = 0;
        int remainingCopies = 220;
        foreach (var group in batches)
        {
            string name = (string?)group.First().Attribute("name") ?? "预览处理程序";
            var extensions = group.Select(node => (string)node.Attribute("extension")!).ToArray();
            progress?.Report($"正在检查组件 {++checkedCount}/{batches.Length} · {string.Join("、", extensions.Take(3))}");
            string? reason = null;
            try
            {
                if (included.Count >= MaxProviders)
                    throw new InvalidDataException("已达到备份包的组件数量上限。");

                Guid id = Guid.Parse(group.Key);
                using var registry = classes.OpenSubKey($@"CLSID\{id:B}\InprocServer32");
                string? registered = registry?.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(registered))
                    throw new InvalidDataException("找不到组件的 DLL 路径。");
                string source = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                if (Path.GetFileName(source) == source)
                    source = Path.Combine(Environment.SystemDirectory, source);
                if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidDataException("组件没有本机 DLL 路径。");
                source = Path.GetFullPath(source);
                if (source.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Windows 自带的组件由目标电脑提供。");
                if (!Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("组件未注册为 DLL。");

                string? threading = registry?.GetValue("ThreadingModel") as string;
                string model = threading?.Trim().ToUpperInvariant() switch
                {
                    "APARTMENT" => "Apartment",
                    "BOTH" => "Both",
                    "FREE" => "Free",
                    "NEUTRAL" => "Neutral",
                    _ => throw new InvalidDataException("组件缺少可用于恢复的线程模型。")
                };
                if (!ProviderPackage.IsX64(source))
                    throw new InvalidDataException("DLL 不是 x64 组件。");
                bool installable = ProviderPackage.HasTrustedSignature(source);
                var validExtensions = extensions.Where(ext => !ext.EndsWith('.')).ToArray();
                if (validExtensions.Length == 0) throw new InvalidDataException("扩展名不能作为 Windows 文件夹名。");
                string entry = $"{validExtensions[0]}/{Path.GetFileName(source)}";
                ProviderPackage.ValidatePath(entry);

                using var input = File.OpenRead(source);
                if (input.Length <= 0 || input.Length > Math.Min(MaxFileBytes, remaining))
                    throw new InvalidDataException("DLL 超过备份包的大小限制。");


                int copies = (int)Math.Min(remaining / input.Length, remainingCopies);
                if (copies == 0) throw new InvalidDataException("已达到 DLL 副本数量上限。");
                var bundled = validExtensions.Take(Math.Min(MaxExtensionsPerProvider, copies)).ToArray();
                string manifestName = new(name.Where(character => !char.IsControl(character)).Take(120).ToArray());
                if (string.IsNullOrWhiteSpace(manifestName)) manifestName = "预览处理程序";
                string hash = Convert.ToHexString(SHA256.HashData(input));
                var component = new Component(id.ToString("B"), manifestName, model, source, entry, bundled, input.Length, hash, installable);
                if (SerializeManifest(included.Append(component).Where(item => item.Installable)).Length > MaxManifestBytes)
                    throw new InvalidDataException("组件清单超过备份包的大小限制。");
                included.Add(component);
                remaining -= input.Length * bundled.Length;
                remainingCopies -= bundled.Length;
                configurationOnly.AddRange(extensions.Except(bundled, StringComparer.OrdinalIgnoreCase)
                    .Select(extension => $"{extension} · 超过备份包的大小或数量上限。"));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
            {
                reason = error.Message;
            }

            if (reason != null)
                configurationOnly.AddRange(extensions.Select(extension => $"{extension} · {name}：{reason}"));
        }

        return new ProviderBackup(plan, included.ToArray(), configurationOnly.ToArray());
    }

    public void Save(string path, IProgress<string>? progress = null)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".thumblab-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                using (var entry = archive.CreateEntry(ConfigurationEntry).Open())
                    configuration.Save(entry);

                var associations = configuration.Root!.Elements("Thumbnail").ToArray();
                var regLines = new List<string> { "Windows Registry Editor Version 5.00", "" };
                foreach (var association in associations)
                {
                    string extension = ThumbnailSettings.Normalize((string)association.Attribute("extension")!);
                    string clsid = Guid.Parse((string)association.Attribute("provider")!).ToString("B").ToUpperInvariant();
                    string key = $@"[HKEY_CURRENT_USER\Software\Classes\{extension}\ShellEx\{{E357FCCD-A995-4576-B01F-234630154E96}}]";
                    string[] lines = { key, $"@=\"{clsid}\"", "" };
                    string directory = extension.EndsWith('.') ? extension + "_extension" : extension;
                    WriteRegistryFile(archive, directory + "/restore.reg",
                        new[] { "Windows Registry Editor Version 5.00", "", "; 请先安装此 CLSID 对应的组件。", "" }.Concat(lines));
                    WriteRegistryInfo(archive, directory, extension, clsid, key);
                    var component = components.FirstOrDefault(item => item.Installable &&
                        item.Clsid.Equals(clsid, StringComparison.OrdinalIgnoreCase) &&
                        item.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
                    if (component != null)
                    {
                        string classKey = $@"HKEY_CURRENT_USER\Software\Classes\CLSID\{clsid}";
                        WriteRegistryFile(archive, directory + "/register-component.reg", new[]
                        {
                            "Windows Registry Editor Version 5.00", "",
                            "; Verify the DLL path on this computer before importing.",
                            "; Import restore.reg afterwards to apply the file type association.", "",
                            $"[{classKey}]", "@=" + RegistryString(component.Name), "",
                            $@"[{classKey}\InprocServer32]", "@=" + RegistryString(component.Source),
                            "\"ThreadingModel\"=" + RegistryString(component.ThreadingModel), ""
                        });
                    }
                    regLines.AddRange(lines);
                }
                WriteRegistryFile(archive, "restore-all.reg", regLines);
                long total = components.Sum(component => component.Length * component.Extensions.Length);
                long written = 0;
                int previousPercent = -1;
                byte[] buffer = new byte[128 * 1024];
                foreach (var component in components)
                foreach (var extension in component.Extensions)
                {
                    using var source = File.OpenRead(component.Source);
                    if (source.Length != component.Length)
                        throw new IOException($"{component.Name} 的 DLL 已更改，请重新开始导出。");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var target = archive.CreateEntry(extension + "/" + Path.GetFileName(component.Source), CompressionLevel.Fastest).Open();
                    int read;
                    long length = 0;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        length += read;
                        if (length > component.Length) throw new IOException("DLL 大小已改变，请重新开始导出。");
                        hash.AppendData(buffer, 0, read);
                        target.Write(buffer, 0, read);
                        written += read;
                        int percent = (int)(written * 100 / total);
                        if (percent != previousPercent)
                        {
                            previousPercent = percent;
                            progress?.Report($"正在写入 ZIP · {percent}%");
                        }
                    }
                    if (length != component.Length || Convert.ToHexString(hash.GetHashAndReset()) != component.Hash)
                        throw new IOException($"{component.Name} 的 DLL 已更改，请重新开始导出。");
                }

                var installable = components.Where(item => item.Installable).ToArray();
                if (installable.Length > 0)
                {
                    using var manifest = archive.CreateEntry("manifest.json").Open();
                    manifest.Write(SerializeManifest(installable));
                }
            }
            progress?.Report("正在完成保存…");
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteRegistryInfo(ZipArchive archive, string directory, string extension, string clsid, string restoreKey)
    {
        string classPath = $@"CLSID\{clsid}\InprocServer32";
        using var server = Registry.ClassesRoot.OpenSubKey(classPath);
        using var writer = new StreamWriter(archive.CreateEntry(directory + "/registry-info.txt").Open());
        writer.WriteLine($"扩展名：{extension}");
        writer.WriteLine($"CLSID：{clsid}");
        writer.WriteLine($"恢复关联写入位置：{restoreKey}");
        writer.WriteLine($@"源电脑的组件注册位置：HKEY_CLASSES_ROOT\{classPath}");
        writer.WriteLine($"原 DLL 路径：{server?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "未登记"}");
        writer.WriteLine($"ThreadingModel：{server?.GetValue("ThreadingModel") ?? "未登记"}");
        writer.WriteLine();
        writer.WriteLine("源电脑的登记路径仅供参考。restore.reg 只恢复关联，目标电脑需先注册对应组件。");
        writer.WriteLine();
        writer.WriteLine("源电脑的缩略图关联详情");
        writer.WriteLine(new ThumbnailSettings().Inspect(extension).DiagnosticText);
    }

    private static string RegistryString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void WriteRegistryFile(ZipArchive archive, string path, IEnumerable<string> lines)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), System.Text.Encoding.Unicode);
        writer.NewLine = "\r\n";
        foreach (string line in lines) writer.WriteLine(line);
    }

    private static byte[] SerializeManifest(IEnumerable<Component> components) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            extensionFolderBackup = true,
            providers = components.Select(component => new
            {
                name = component.Name,
                clsid = component.Clsid,
                threadingModel = component.ThreadingModel,
                dll = component.Entry,
                extensions = component.Extensions
            }),
            files = components.Select(component => new { path = component.Entry, sha256 = component.Hash })
        });
}
