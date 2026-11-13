#nullable enable
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ThumbLabWinUI;

internal sealed class ThumbnailSettings
{
    private const string Slot = @"\ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string Classes = @"Software\Classes\";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string IconsOnly = "IconsOnly";
    private const string Current = "Windows 当前解析结果";
    private static readonly Regex ExtensionPattern = new(@"^\.[a-z0-9][a-z0-9_+.-]{0,31}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Stack<List<Change>> history = new();

    internal sealed record FileType(string Extension, bool HasThumbnail);
    internal sealed record Provider(string Id, string Name, string Server, bool Exists)
    {
        public override string ToString() => Exists ? Name : Name + "（组件不可用）";
    }

    internal sealed record Location(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    internal sealed record Details(string Extension, Provider? Effective, Provider[] Providers,
        Location[] Locations, bool IconsOnly, bool HasOverride, string DiagnosticText);

    internal sealed record Entry(object? Value, RegistryValueKind Kind);
    internal sealed record Change(string Key, string Name, Entry Before, Entry After);

    internal sealed class ImportPlan
    {
        internal readonly List<Change> Changes;
        internal ImportPlan(List<Change> changes, int skippedCount, string summary)
        {
            Changes = changes;
            SkippedCount = skippedCount;
            Summary = summary;
        }
        public int ChangeCount => Changes.Count;
        public int SkippedCount { get; }
        public string Summary { get; }
    }

    public bool CanUndo => history.Count > 0;

    public static string Normalize(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (!value.StartsWith('.')) value = "." + value;
        if (!ExtensionPattern.IsMatch(value))
            throw new InvalidOperationException("请输入有效的扩展名，例如 .pdf。");
        return value;
    }

    public static FileType[] LoadFileTypes()
    {
        string? generic = ThumbnailShell.Effective(".thumblab-unregistered-" + Guid.NewGuid().ToString("N"));
        using var classes = Registry.ClassesRoot;
        return classes.GetSubKeyNames().Where(name => ExtensionPattern.IsMatch(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new FileType(name,
                (ThumbnailShell.Effective(name) ?? generic) != generic
                || Read(classes, name + Slot) is not null
                || Read(classes, @"SystemFileAssociations\" + name + Slot) is not null))
            .ToArray();
    }

    public Details Inspect(string extension)
    {
        string ext = Normalize(extension);
        string extensionSlot = ext + Slot;
        var entries = new List<(string Label, string? Value)> { (Current, ThumbnailShell.Effective(ext)) };
        var locations = new List<Location>();
        using var user = Registry.CurrentUser;
        using var machine = Registry.LocalMachine;
        using var merged = Registry.ClassesRoot;

        void AddRegistration(string label, RegistryKey hive, string key)
        {
            using var registration = hive.OpenSubKey(key);
            entries.Add((label, registration?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string));
            if (registration is not null) locations.Add(new Location(label, registration.Name));
        }

        AddRegistration("当前用户覆盖", user, Classes + extensionSlot);
        AddRegistration("系统扩展名配置", machine, Classes + extensionSlot);
        AddRegistration("扩展名备用配置", merged, @"SystemFileAssociations\" + extensionSlot);
        var progId = Read(user, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId")
            ?? Read(merged, ext);
        if (!string.IsNullOrWhiteSpace(progId))
            AddRegistration("当前文件类型的配置", merged, progId + Slot);
        var perceived = Read(merged, ext, "PerceivedType");
        if (!string.IsNullOrWhiteSpace(perceived))
            AddRegistration("通用文件类型配置", merged, @"SystemFileAssociations\" + perceived + Slot);

        var providers = entries.Select(entry => CanonicalGuid(entry.Value)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Describe(id)!).ToArray();
        bool iconsOnly = Equals(ReadEntry(Advanced, IconsOnly).Value, 1);
        var descriptions = entries.Select(entry =>
        {
            string? id = CanonicalGuid(entry.Value);
            var provider = providers.FirstOrDefault(item => item.Id == id);
            return provider is null ? $"{entry.Label}  {entry.Value ?? "未指定"}"
                : $"{entry.Label}  {provider.Name}\n{provider.Id}\n{(provider.Exists ? provider.Server : "找不到组件文件")}";
        });
        return new Details(ext, providers.FirstOrDefault(item => item.Id == CanonicalGuid(entries[0].Value)),
            providers, locations.ToArray(), iconsOnly, ReadEntry(Classes + extensionSlot, "").Value is not null,
            string.Join("\n\n", descriptions));
    }

    public static Location? ProviderLocation(Provider? provider)
    {
        if (provider is null || CanonicalGuid(provider.Id) is not string id) return null;
        using var root = Registry.ClassesRoot;
        using var key = root.OpenSubKey(@"CLSID\" + id + @"\InprocServer32");
        return key is null ? null : new Location("所选处理器 · " + provider.Name, key.Name);
    }

    public bool Apply(string extension, Provider provider)
    {
        if (provider is null || Describe(provider.Id) is not { Exists: true } installed)
            throw new InvalidOperationException("请选择本机可用的缩略图处理器。");
        return Commit([Plan(Classes + Normalize(extension) + Slot, "", installed.Id, RegistryValueKind.String)]);
    }

    public bool ApplyPackage(IReadOnlyDictionary<string, string> mappings)
    {
        return Commit(mappings.Select(mapping =>
        {
            if (Describe(mapping.Value) is not { Exists: true } installed)
                throw new InvalidOperationException("组件未能在本机注册：" + mapping.Value);
            return Plan(Classes + Normalize(mapping.Key) + Slot, "", installed.Id, RegistryValueKind.String);
        }));
    }

    public bool Reset(string extension) =>
        Commit([Plan(Classes + Normalize(extension) + Slot, "", null, RegistryValueKind.None)]);

    public bool EnableThumbnails() => Commit([Plan(Advanced, IconsOnly, 0, RegistryValueKind.DWord)]);

    public bool Undo()
    {
        if (!history.TryPeek(out var changes)) return false;
        WriteBatch(changes.AsEnumerable().Reverse(), false);
        history.Pop();
        return true;
    }

    public int Export(string path, IReadOnlyCollection<string>? selection = null)
    {
        using var user = Registry.CurrentUser;
        using var classes = user.OpenSubKey(Classes.TrimEnd('\\'));
        var overrides = (classes?.GetSubKeyNames() ?? []).Where(name => ExtensionPattern.IsMatch(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => (Extension: Normalize(name), Provider: Describe(Read(classes!, name + Slot))))
            .Where(entry => entry.Provider is not null).ToArray();
        if (selection != null)
            overrides = selection.Select(ext => (Extension: Normalize(ext), Provider: Inspect(ext).Effective))
                .Where(item => item.Provider is { Exists: true }).ToArray();
        var root = new XElement("ThumbLab", new XAttribute("version", 1),
            overrides.Select(item => new XElement("Thumbnail", new XAttribute("extension", item.Extension),
                new XAttribute("provider", item.Provider!.Id), new XAttribute("name", item.Provider.Name))));
        if (selection == null && ReadEntry(Advanced, IconsOnly).Value is int display && display is 0 or 1)
            root.Add(new XElement(IconsOnly, display));
        new XDocument(root).Save(path);
        return overrides.Length;
    }

    public ImportPlan PrepareImport(string path)
    {
        var parser = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 2_000_000,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, parser);
        var root = XDocument.Load(reader).Root;
        if (root is null || root.Name != "ThumbLab" || (string?)root.Attribute("version") != "1")
            throw new InvalidOperationException("无法识别这个 ThumbLab 配置文件。");

        var changes = new List<Change>();
        var applicable = new List<string>();
        var skipped = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasDisplay = false;
        foreach (var node in root.Elements())
        {
            if (node.Name == IconsOnly && !hasDisplay && node.Value is "0" or "1")
            {
                int value = node.Value == "1" ? 1 : 0;
                AddChange(Plan(Advanced, IconsOnly, value, RegistryValueKind.DWord),
                    "资源管理器：" + (value == 0 ? "允许显示缩略图" : "始终显示图标"));
                hasDisplay = true;
                continue;
            }
            if (node.Name != "Thumbnail")
                throw new InvalidOperationException("配置含有未知或重复的项目。");
            string extension = Normalize((string?)node.Attribute("extension") ?? "");
            var provider = Describe((string?)node.Attribute("provider"));
            if (provider is null || !extensions.Add(extension))
                throw new InvalidOperationException("配置含有无效处理器或重复扩展名。");
            if (!provider.Exists)
            {
                string label = (string?)node.Attribute("name") ?? provider.Name;
                skipped.Add(extension + "  " + label[..Math.Min(label.Length, 80)] + "  " + provider.Id);
                continue;
            }
            AddChange(Plan(Classes + extension + Slot, "", provider.Id, RegistryValueKind.String),
                extension + "  →  " + provider.Name);
        }

        string summary = applicable.Count > 0
            ? $"将应用 {applicable.Count} 项当前用户设置。\n" + ListPreview(applicable)
            : "没有需要应用的配置。";
        if (skipped.Count > 0)
            summary += $"\n\n本机缺少以下 {skipped.Count} 项组件，导入时会跳过。\n" + ListPreview(skipped);
        return new ImportPlan(changes, skipped.Count, summary);

        void AddChange(Change change, string description)
        {
            if (!NeedsWrite(change)) return;
            changes.Add(change);
            applicable.Add(description);
        }
    }

    public bool CommitImport(ImportPlan plan)
    {
        foreach (var change in plan.Changes)
            if (change.Name != IconsOnly && Describe(change.After.Value as string) is not { Exists: true })
                throw new InvalidOperationException("导入组件已发生变化，请重新打开配置文件。");
        return Commit(plan.Changes);
    }

    private static string ListPreview(List<string> list) =>
        string.Join("\n", list.Take(12)) + (list.Count > 12 ? $"\n另有 {list.Count - 12} 项" : "");

    private static string? Read(RegistryKey root, string key, string name = "")
    {
        using var entry = root.OpenSubKey(key);
        return entry?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static string? CanonicalGuid(string? value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("B").ToUpperInvariant() : null;

    private static Provider? Describe(string? id)
    {
        if (CanonicalGuid(id) is not string guid) return null;
        string key = @"CLSID\" + guid;
        using var merged = Registry.ClassesRoot;
        string? raw = Read(merged, key + @"\InprocServer32");
        string server = raw is null ? "" : Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (!string.IsNullOrWhiteSpace(server) && Path.GetFileName(server) == server)
            server = Path.Combine(Environment.SystemDirectory, server);
        string? name = Read(merged, key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) name = Read(merged, key);
        if (string.IsNullOrWhiteSpace(name)) name = Read(merged, key + @"\ProgID");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(server);
        if (string.IsNullOrWhiteSpace(name)) name = "缩略图处理器";
        return new Provider(guid, name, server, Path.IsPathFullyQualified(server) && File.Exists(server));
    }

    private static Entry ReadEntry(string key, string name)
    {
        using var user = Registry.CurrentUser;
        using var entry = user.OpenSubKey(key);
        object? value = entry?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new Entry(value, value is null ? RegistryValueKind.None : entry!.GetValueKind(name));
    }

    private static Change Plan(string key, string name, object? value, RegistryValueKind kind) =>
        new(key, name, ReadEntry(key, name), new Entry(value, kind));

    private static bool Same(Entry first, Entry second) => first.Kind == second.Kind
        && StructuralComparisons.StructuralEqualityComparer.Equals(first.Value, second.Value);

    private static bool NeedsWrite(Change change)
    {
        if (Same(change.Before, change.After)) return false;
        if (change.Name.Length == 0 && change.After.Value is string next &&
            CanonicalGuid(change.Before.Value as string) is string previous && previous == next)
            return false;
        return true;
    }

    private bool Commit(IEnumerable<Change> planned)
    {
        var changes = planned.Where(NeedsWrite).ToList();
        if (changes.Count == 0) return false;
        WriteBatch(changes, true);
        history.Push(changes);
        return true;
    }

    private static void Write(Change change, bool forward)
    {
        var value = forward ? change.After : change.Before;
        var expected = forward ? change.Before : change.After;
        if (!Same(ReadEntry(change.Key, change.Name), expected))
            throw new InvalidOperationException("配置已被其他程序修改，请重新检查后再试。");
        using var user = Registry.CurrentUser;
        if (value.Value is null)
        {
            using var key = user.OpenSubKey(change.Key, true);
            key?.DeleteValue(change.Name, false);
            return;
        }
        using var target = user.CreateSubKey(change.Key);
        target.SetValue(change.Name, value.Value, value.Kind);
    }

    private static void WriteBatch(IEnumerable<Change> changes, bool forward)
    {
        var completed = new Stack<Change>();
        try
        {
            foreach (var change in changes)
            {
                Write(change, forward);
                completed.Push(change);
            }
        }
        catch (Exception error)
        {
            bool restored = true;
            foreach (var change in completed)
            {
                try { Write(change, !forward); }
                catch { restored = false; }
            }
            if (!restored)
                throw new InvalidOperationException(error.Message + "\n部分配置未能恢复，请重新检查。", error);
            throw;
        }
        finally { ThumbnailShell.Notify(); }
    }
}


