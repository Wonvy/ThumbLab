#nullable enable
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThumbLabWinUI;

internal sealed class ProviderPackage : IDisposable
{
    private const string Classes = @"Software\Classes\CLSID\";
    private const long MaxFile = 64L * 1024 * 1024;
    private const long MaxTotal = 256L * 1024 * 1024;
    internal const int MaxBackupConfiguration = 2_000_000;
    private const int MaxManifest = 128 * 1024;
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private readonly ZipArchive archive;
    private readonly Provider[] providers;
    private readonly FileItem[] files;
    private bool installed;
    public string Summary { get; }

    private sealed record Provider(string Name, string Clsid, string Threading, string Dll, string[] Extensions);
    private sealed record FileItem(string Path, ZipArchiveEntry Entry, string Hash);

    private ProviderPackage(ZipArchive archive, Provider[] providers, FileItem[] files)
    {
        this.archive = archive;
        this.providers = providers;
        this.files = files;
        Summary = string.Join("\n", providers.Select(p =>
            $"{p.Name} · {string.Join(", ", p.Extensions)}" +
            (FindExisting(p.Clsid) == null ? "（安装新组件）" : "（使用本机组件）")));
    }

    public static ProviderPackage Prepare(string path)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive? zip = null;
        try
        {
            if (input.Length > MaxTotal + MaxManifest + 4 * MaxBackupConfiguration) throw BadPackage("文件过大。");
            zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 8192) throw BadPackage("文件数量超过上限。");
            var archiveFiles = new Dictionary<string, ZipArchiveEntry>(IgnoreCase);
            long expandedSize = 0;
            foreach (var entry in zip.Entries)
            {
                ValidatePath(entry.FullName);
                if (entry.Length > MaxFile || entry.Length > MaxTotal + MaxManifest + 4 * MaxBackupConfiguration - expandedSize)
                    throw BadPackage("解压大小超过上限。");
                expandedSize += entry.Length;
                if (!archiveFiles.TryAdd(entry.FullName, entry) || (entry.ExternalAttributes & 0x400) != 0 ||
                    ((uint)entry.ExternalAttributes >> 28) == 0xA)
                    throw BadPackage("存在重复文件或链接。");
            }
            if (archiveFiles.Remove("config.thumblab", out var configuration) && configuration.Length > MaxBackupConfiguration)
                throw BadPackage("备份配置文件过大。");
            // Backup-only files are never eligible for extraction or registration.
            foreach (var name in archiveFiles.Keys.Where(name => name.StartsWith("backup-only/", StringComparison.OrdinalIgnoreCase)).ToArray())
                archiveFiles.Remove(name);
            if (!archiveFiles.Remove("manifest.json", out var manifest) || manifest.Length > MaxManifest)
                throw BadPackage("缺少有效的 manifest.json。");
            using var reader = manifest.Open();
            byte[] manifestBytes = new byte[(int)manifest.Length];
            reader.ReadExactly(manifestBytes);
            if (reader.ReadByte() != -1) throw BadPackage("清单解压大小与声明不一致。");
            using var json = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = json.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw BadPackage("版本不受支持。");

            var declarations = root.GetProperty("files");
            if (root.TryGetProperty("extensionFolderBackup", out var layout) && layout.ValueKind == JsonValueKind.True)
            {
                var declared = declarations.EnumerateArray().Select(item => Field(item, "path")).ToHashSet(IgnoreCase);
                foreach (var name in archiveFiles.Keys.Where(name => !declared.Contains(name) &&
                    (name.Equals("restore-all.reg", StringComparison.OrdinalIgnoreCase) ||
                     (name.StartsWith(".") && name.Count(character => character == '/') == 1))).ToArray())
                    archiveFiles.Remove(name);
            }
            if (declarations.GetArrayLength() != archiveFiles.Count || archiveFiles.Count == 0 || archiveFiles.Count > 256)
                throw BadPackage("文件列表与包内文件不匹配。");
            var knownFiles = new HashSet<string>(IgnoreCase);
            long total = 0;
            var files = new List<FileItem>();
            foreach (var file in declarations.EnumerateArray())
            {
                string relative = Field(file, "path");
                string hash = Field(file, "sha256");
                if (!knownFiles.Add(relative) || !archiveFiles.TryGetValue(relative, out var entry)
                    || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")
                    || (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    throw BadPackage("存在未声明、重复或不受支持的组件文件。");
                total += entry.Length;
                if (entry.Length <= 0 || entry.Length > MaxFile || total > MaxTotal)
                    throw BadPackage("解压大小超过上限。");
                files.Add(new FileItem(relative, entry, hash));
            }

            var components = root.GetProperty("providers");
            if (components.GetArrayLength() is < 1 or > 32) throw BadPackage("预览处理程序数量超出范围。");
            var classIds = new HashSet<string>(IgnoreCase);
            var extensions = new HashSet<string>(IgnoreCase);
            var providers = components.EnumerateArray().Select(item =>
            {
                string name = Field(item, "name");
                if (name.Length > 120 || name.Any(char.IsControl)) throw BadPackage("预览处理程序名称无效。");
                if (!Guid.TryParse(Field(item, "clsid"), out var id) || id == Guid.Empty || !classIds.Add(id.ToString("B")))
                    throw BadPackage("CLSID 无效或重复。");
                string model = Field(item, "threadingModel");
                if (model is not ("Apartment" or "Both" or "Free" or "Neutral"))
                    throw BadPackage("线程模型无效。");
                string dll = Field(item, "dll");
                if (!dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !knownFiles.Contains(dll))
                    throw BadPackage("找不到声明的预览处理程序 DLL。");
                var values = item.GetProperty("extensions");
                if (values.GetArrayLength() is < 1 or > 128) throw BadPackage("扩展名数量超出范围。");
                var normalized = values.EnumerateArray().Select(value =>
                {
                    string extension = ThumbnailSettings.Normalize(value.GetString() ?? "");
                    if (!extensions.Add(extension)) throw BadPackage("同一个扩展名不能关联多个预览处理程序。");
                    return extension;
                }).ToArray();
                return new Provider(name, id.ToString("B").ToUpperInvariant(), model, dll, normalized);
            }).ToArray();
            return new ProviderPackage(zip, providers, files.ToArray());
        }
        catch
        {
            if (zip == null) input.Dispose();
            else zip.Dispose();
            throw;
        }
    }

    public bool Install(Func<IReadOnlyDictionary<string, string>, bool> apply)
    {
        if (installed) throw new InvalidOperationException("组件包已经应用。");
        var newProviders = providers.Where(p => FindExisting(p.Clsid) == null).ToArray();
        var registered = new List<(string Key, string Server)>();
        var createdFiles = new List<string>();
        var createdFolders = new List<string>();
        bool keep = false;
        try
        {
            if (newProviders.Length > 0)
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("安装新组件需要管理员权限，请关闭 ThumbLab 后以管理员身份运行。");
                if (RuntimeInformation.OSArchitecture != Architecture.X64)
                    throw new InvalidOperationException("组件包目前仅支持 x64 Windows。");

                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var destination = Path.Combine(programFiles, "ThumbLab", "Providers", Guid.NewGuid().ToString("N"));
                EnsureProtectedFolder(programFiles, destination, createdFolders);
                foreach (var file in files)
                {
                    var target = Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    EnsureProtectedFolder(programFiles, Path.GetDirectoryName(target)!, createdFolders);
                    using (var source = file.Entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        createdFiles.Add(target);
                        CopyVerified(source, output, file.Entry.Length, file.Hash);
                    }
                    if (!IsX64(target) || !HasTrustedSignature(target))
                        throw BadPackage($"{file.Path} 需要有效的 Authenticode 签名和 x64 架构。");
                }

                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                foreach (var provider in newProviders)
                {
                    if (FindExisting(provider.Clsid) != null) continue;
                    string server = Path.Combine(destination, provider.Dll.Replace('/', Path.DirectorySeparatorChar));
                    string keyPath = Classes + provider.Clsid + @"\InprocServer32";
                    using var key = machine.CreateSubKey(keyPath);
                    registered.Add((keyPath, server));
                    key.SetValue("", server, RegistryValueKind.String);
                    key.SetValue("ThreadingModel", provider.Threading, RegistryValueKind.String);
                }
            }

            var bindings = providers.SelectMany(provider => provider.Extensions.Select(extension => (extension, provider.Clsid)))
                .ToDictionary(binding => binding.extension, binding => binding.Clsid, IgnoreCase);
            bool changed = apply(bindings);
            keep = registered.Count > 0;
            installed = true;
            return changed || keep;
        }
        catch (Exception error)
        {
            bool restored = true;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var registration in registered.AsEnumerable().Reverse())
            {
                try
                {
                    using var current = machine.OpenSubKey(registration.Key);
                    if (current?.GetValue("") is string currentPath && !IgnoreCase.Equals(currentPath, registration.Server))
                        throw new InvalidOperationException("注册已被其他程序修改。");
                    machine.DeleteSubKey(registration.Key, false);
                }
                catch { restored = false; }
            }
            keep = !restored;
            if (!restored) throw new InvalidOperationException(error.Message + "\n部分组件注册无法撤回，请重新检查。", error);
            throw;
        }
        finally
        {
            if (!keep)
            {
                foreach (var file in createdFiles.AsEnumerable().Reverse())
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                foreach (var folder in createdFolders.AsEnumerable().Reverse())
                    try { Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string? FindExisting(string clsid)
    {
        using var merged = Registry.ClassesRoot;
        using var key = merged.OpenSubKey(@"CLSID\" + clsid);
        if (key == null) return null;
        using var inProc = key.OpenSubKey("InprocServer32");
        string? value = inProc?.GetValue("") as string;
        string? path = value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) throw BadPackage($"{clsid} 已注册，但没有可用的 DLL。");
        if (Path.GetFileName(path) == path) path = Path.Combine(Environment.SystemDirectory, path);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw BadPackage($"{clsid} 已注册，但 DLL 不存在。");
        return path;
    }

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static bool TrustedWriter(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(SystemAccount) || sid.Equals(TrustedInstaller);

    private static void EnsureProtectedFolder(string programFiles, string folder, List<string> created)
    {
        // Check ancestors for reparse points before trusting Program Files.
        for (string? part = programFiles; part != null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw BadPackage("组件安装目录不能是重解析点。");

        var chain = new Stack<string>();
        for (string? part = folder; part != null && !IgnoreCase.Equals(part, programFiles); part = Path.GetDirectoryName(part))
            chain.Push(part);
        chain.Push(programFiles);
        foreach (var part in chain)
        {
            var directory = new DirectoryInfo(part);
            if (!directory.Exists)
            {
                var security = new DirectorySecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(SystemAccount, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                directory.Create(security);
                created.Add(part);
            }
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw BadPackage("组件安装目录不能是重解析点。");

            var access = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var raw = new RawSecurityDescriptor(access.GetSecurityDescriptorBinaryForm(), 0);
            const int genericWriteOrAll = 0x50000000;
            const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete
                | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (raw.Owner == null || !TrustedWriter(raw.Owner) || raw.DiscretionaryAcl == null ||
                raw.DiscretionaryAcl.Cast<GenericAce>().OfType<QualifiedAce>().Any(rule =>
                    rule.AceQualifier == AceQualifier.AccessAllowed && (rule.AceFlags & AceFlags.InheritOnly) == 0
                    && (rule.AccessMask & (genericWriteOrAll | (int)write)) != 0 && !TrustedWriter(rule.SecurityIdentifier)))
                throw BadPackage("安装目录可被非管理员修改：" + part);
        }
    }

    private static void CopyVerified(Stream input, Stream output, long expected, string expectedHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long copied = 0;
        int length;
        while ((length = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += length;
            if (copied > expected) throw BadPackage("解压大小与声明不一致。");
            hash.AppendData(buffer, 0, length);
            output.Write(buffer, 0, length);
        }
        if (copied != expected || !IgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash))
            throw BadPackage("组件哈希校验失败。");
    }

    internal static bool IsX64(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input);
        if (input.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
        input.Position = 0x3C;
        int pe = reader.ReadInt32();
        if (pe < 0x40 || pe > input.Length - 6) return false;
        input.Position = pe;
        return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
    }

    internal static bool HasTrustedSignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        IntPtr info = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, info, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                File = info,
                StateAction = 1,
                Flags = 0x80
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(info);
            Marshal.FreeHGlobal(info);
        }
    }

    internal static void ValidatePath(string name)
    {
        if (name.Length is < 1 or > 190) throw BadPackage("组件路径无效。");
        var segments = name.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (index == 0 && segments.Length > 1 && Regex.IsMatch(segment, @"\A\.[a-zA-Z0-9][a-zA-Z0-9_.+-]{0,31}\z") && !segment.EndsWith('.'))
                continue;
            if (!Regex.IsMatch(segment, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]{0,95}\z") || segment.EndsWith('.') || segment.EndsWith(' ')
                || Regex.IsMatch(segment.Split('.')[0].TrimEnd(), @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase))
                throw BadPackage("组件路径不安全。");
        }
    }

    private static string Field(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw BadPackage($"缺少 {name}。");

    private static InvalidDataException BadPackage(string error) => new("组件包无效：" + error);

    public void Dispose() => archive.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint Flags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
