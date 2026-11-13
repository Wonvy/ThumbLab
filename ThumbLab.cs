using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

internal sealed partial class ThumbLab : Form
{
    private const string Slot = @"\ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string Classes = @"Software\Classes\";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private readonly TextBox extension = new TextBox { Width = 90, Text = ".pdf" };
    private readonly ComboBox providers = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox diagnostics = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly PictureBox preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label status = new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0), Text = "就绪" };
    private readonly Stack<List<Change>> history = new Stack<List<Change>>();
    private readonly Button undo;
    private readonly Button enable;

    private sealed class Provider
    {
        public string Id;
        public string Name;
        public string Server;
        public bool Exists;
        public override string ToString() { return Name + "    " + Id + (Exists ? "" : "    [组件不可用]"); }
    }

    private sealed class Change
    {
        public string Key;
        public string Name;
        public object Before;
        public object After;
        public RegistryValueKind BeforeKind;
        public RegistryValueKind AfterKind;
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ThumbLab());
    }

    public ThumbLab()
    {
        BuildInterface(out undo, out enable);
        extension.KeyDown += (sender, args) => { if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; Run(RefreshInfo); } };
        extension.TextChanged += (sender, args) =>
        {
            providers.Items.Clear();
            registryLocations.Items.Clear();
        };
        FormClosed += (sender, args) => { if (preview.Image != null) preview.Image.Dispose(); };
        Run(RefreshInfo);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ThumbLab", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string Normalize(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (!value.StartsWith(".")) value = "." + value;
        if (!Regex.IsMatch(value, @"^\.[a-z0-9][a-z0-9_+.-]{0,31}\z"))
            throw new InvalidOperationException("请输入有效的扩展名，例如 .pdf。");
        return value;
    }

    private static string Read(RegistryKey root, string key, string name = "")
    {
        using (var entry = root.OpenSubKey(key))
            return entry == null ? null : entry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static string CanonicalGuid(string value)
    {
        Guid guid;
        return Guid.TryParse(value, out guid) ? guid.ToString("B").ToUpperInvariant() : null;
    }

    private static Provider Describe(string id)
    {
        id = CanonicalGuid(id);
        if (id == null) return null;
        string key = @"CLSID\" + id;
        var merged = Registry.ClassesRoot;
        string raw = Read(merged, key + @"\InprocServer32");
        string server = raw == null ? "" : Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        bool exists = Path.IsPathRooted(server) && File.Exists(server);
        if (!exists && !String.IsNullOrWhiteSpace(server) && Path.GetFileName(server) == server)
            { server = Path.Combine(Environment.SystemDirectory, server); exists = File.Exists(server); }
        return new Provider
        {
            Id = id,
            Name = Read(merged, key, "DisplayName") ?? Read(merged, key) ?? "缩略图处理器",
            Server = server,
            Exists = exists
        };
    }

    private static Dictionary<string, string> Registrations(string ext)
    {
        var result = new Dictionary<string, string>
        {
            { "Windows 当前解析结果", ThumbnailShell.Effective(ext) },
            { "当前用户覆盖", Read(Registry.CurrentUser, Classes + ext + Slot) },
            { "系统扩展名配置", Read(Registry.LocalMachine, Classes + ext + Slot) },
            { "扩展名备用配置", Read(Registry.ClassesRoot, @"SystemFileAssociations\" + ext + Slot) }
        };
        var progId = Read(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId")
            ?? Read(Registry.ClassesRoot, ext);
        if (!String.IsNullOrWhiteSpace(progId))
            result["当前文件类型的配置"] = Read(Registry.ClassesRoot, progId + Slot);
        var perceivedType = Read(Registry.ClassesRoot, ext, "PerceivedType");
        if (!String.IsNullOrWhiteSpace(perceivedType))
            result["通用文件类型配置"] = Read(Registry.ClassesRoot, @"SystemFileAssociations\" + perceivedType + Slot);
        return result;
    }

    private void RefreshInfo()
    {
        string ext = Normalize(extension.Text);
        extension.Text = ext;
        var entries = Registrations(ext);
        providers.Items.Clear();
        foreach (var id in entries.Values.Select(CanonicalGuid).Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase))
            providers.Items.Add(Describe(id));
        if (providers.Items.Count > 0)
        {
            var alternative = providers.Items.Cast<Provider>().FirstOrDefault(p => p.Exists && p.Id != CanonicalGuid(entries["Windows 当前解析结果"]));
            providers.SelectedItem = alternative ?? providers.Items[0];
        }
        bool iconsOnly = Equals(ReadValue(Advanced, "IconsOnly"), 1);
        enable.Enabled = iconsOnly;
        UpdateRegistryLocations(ext);
        var effective = Describe(entries["Windows 当前解析结果"]);
        currentType.Text = ext + "   ·   " + (effective == null ? "未找到当前处理器" : effective.Name + (effective.Exists ? "" : "（组件不可用）"));
        diagnostics.Lines = new[] { "扩展名  " + ext, "资源管理器  " + (iconsOnly ? "设置为始终显示图标" : "允许显示缩略图"), "" }
            .Concat(entries.SelectMany(entry =>
            {
                var provider = Describe(entry.Value);
                return provider == null
                    ? new[] { entry.Key + "  " + (entry.Value ?? "未指定"), "" }
                    : new[] { entry.Key + "  " + provider.Name, "  " + provider.Id,
                        "  " + (provider.Exists ? provider.Server : "找不到组件文件"), "" };
            })).ToArray();
    }

    private static object ReadValue(string key, string name)
    {
        using (var entry = Registry.CurrentUser.OpenSubKey(key))
            return entry == null ? null : entry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static Change Plan(string key, string name, object value, RegistryValueKind kind)
    {
        using (var entry = Registry.CurrentUser.OpenSubKey(key))
        {
            object before = entry == null ? null : entry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new Change { Key = key, Name = name, Before = before, After = value,
                BeforeKind = before == null ? RegistryValueKind.None : entry.GetValueKind(name), AfterKind = kind };
        }
    }

    private static bool Same(object first, object second)
    {
        return System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(first, second);
    }

    private static void Write(Change change, bool forward)
    {
        object value = forward ? change.After : change.Before;
        object expected = forward ? change.Before : change.After;
        if (!Same(ReadValue(change.Key, change.Name), expected))
            throw new InvalidOperationException("配置已被其他程序修改，请重新检查后再试。");
        if (value == null)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(change.Key, true))
                if (key != null) key.DeleteValue(change.Name, false);
            return;
        }
        using (var key = Registry.CurrentUser.CreateSubKey(change.Key))
            key.SetValue(change.Name, value, forward ? change.AfterKind : change.BeforeKind);
    }

    private void Commit(IEnumerable<Change> planned, string message)
    {
        var changes = planned.Where(change => !Same(change.Before, change.After)).ToList();
        if (changes.Count == 0)
        {
            status.Text = "配置无需更新。";
            return;
        }
        WriteBatch(changes, true);
        history.Push(changes);
        undo.Enabled = true;
        RefreshInfo();
        status.Text = message;
    }

    private static void WriteBatch(List<Change> changes, bool forward)
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
            var rollbackErrors = new List<string>();
            foreach (var change in completed)
            {
                try { Write(change, !forward); }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            }
            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(error.Message + "\r\n部分配置未能恢复，请重新检查。", error);
            throw;
        }
        finally { ThumbnailShell.Notify(); }
    }

    private void ApplySelected()
    {
        string ext = Normalize(extension.Text);
        var selected = providers.SelectedItem as Provider;
        if (selected == null || !Describe(selected.Id).Exists)
            throw new InvalidOperationException("请选择本机可用的缩略图处理器。");
        Commit(new[] { Plan(Classes + ext + Slot, "", selected.Id, RegistryValueKind.String) },
            "已应用。可选择一个文件测试，或在资源管理器中按 F5 刷新。");
    }

    private void ResetOverride()
    {
        Commit(new[] { Plan(Classes + Normalize(extension.Text) + Slot, "", null, RegistryValueKind.String) },
            "已移除当前扩展名的用户覆盖。");
    }

    private void EnableThumbnails()
    {
        Commit(new[] { Plan(Advanced, "IconsOnly", 0, RegistryValueKind.DWord) }, "已启用缩略图显示。");
    }

    private void Undo()
    {
        if (history.Count == 0) return;
        WriteBatch(history.Peek(), false);
        history.Pop();
        undo.Enabled = history.Count > 0;
        RefreshInfo();
        status.Text = "已撤销上一次修改。";
    }

    private async void TestFile()
    {
        using (var picker = new OpenFileDialog { Title = "选择用于测试的文件", Filter = "所有文件 (*.*)|*.*", CheckFileExists = true })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            string path = picker.FileName;
            try
            {
                extension.Text = Normalize(Path.GetExtension(path));
                RefreshInfo();
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
                previewEmpty.Visible = true;
                status.Text = "正在请求 Windows 重新生成缩略图…";
                Enabled = false;
                Bitmap image = await Task.Run(() => ThumbnailShell.Extract(path, true));
                if (IsDisposed) { image.Dispose(); return; }
                preview.Image = image;
                previewEmpty.Visible = false;
                status.Text = String.Format("生成成功 · {0} × {1} 像素", image.Width, image.Height);
            }
            catch (Exception error)
            {
                if (!IsDisposed) status.Text = error is COMException
                    ? String.Format("Windows 未能生成缩略图（0x{0:X8}）。", error.HResult)
                    : "生成失败 · " + error.Message;
            }
            finally { if (!IsDisposed) Enabled = true; }
        }
    }

    private static Dictionary<string, string> CurrentOverrides()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var classes = Registry.CurrentUser.OpenSubKey(Classes.TrimEnd((char)92)))
        {
            if (classes == null) return values;
            foreach (var key in classes.GetSubKeyNames().Where(key => key.StartsWith(".")))
            {
                if (!Regex.IsMatch(key, @"^\.[a-zA-Z0-9][a-zA-Z0-9_+.-]{0,31}\z")) continue;
                var id = CanonicalGuid(Read(classes, key + Slot));
                if (id != null) values.Add(Normalize(key), id);
            }
        }
        return values;
    }

    private void Export()
    {
        var overrides = CurrentOverrides();
        using (var picker = new SaveFileDialog { Filter = "ThumbLab 配置 (*.thumblab)|*.thumblab", DefaultExt = "thumblab", FileName = "ThumbLab" })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var root = new XElement("ThumbLab", new XAttribute("version", 1),
                overrides.OrderBy(item => item.Key).Select(item => new XElement("Thumbnail",
                    new XAttribute("extension", item.Key), new XAttribute("provider", item.Value),
                    new XAttribute("name", Describe(item.Value).Name))));
            var iconsOnly = ReadValue(Advanced, "IconsOnly");
            if (Equals(iconsOnly, 0) || Equals(iconsOnly, 1))
                root.Add(new XElement("IconsOnly", iconsOnly));
            new XDocument(root).Save(picker.FileName);
            status.Text = "已导出 " + overrides.Count + " 项用户覆盖配置。";
        }
    }

    private static List<Change> ReadImport(string path, out List<string> skipped)
    {
        var parser = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2000000, XmlResolver = null };
        XElement root;
        using (var reader = XmlReader.Create(path, parser)) root = XDocument.Load(reader).Root;
        if (root == null || root.Name != "ThumbLab" || (string)root.Attribute("version") != "1")
            throw new InvalidOperationException("无法识别这个 ThumbLab 配置文件。");
        var changes = new List<Change>();
        skipped = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasDisplay = false;
        foreach (var node in root.Elements())
        {
            if (node.Name == "IconsOnly" && !hasDisplay && (node.Value == "0" || node.Value == "1"))
            {
                changes.Add(Plan(Advanced, "IconsOnly", Int32.Parse(node.Value), RegistryValueKind.DWord));
                hasDisplay = true;
                continue;
            }
            if (node.Name != "Thumbnail") throw new InvalidOperationException("配置含有未知或重复的项目。");
            var ext = Normalize((string)node.Attribute("extension") ?? "");
            var provider = Describe((string)node.Attribute("provider"));
            if (provider == null || !extensions.Add(ext)) throw new InvalidOperationException("配置含有无效处理器或重复扩展名。");
            if (!provider.Exists)
            {
                var label = (string)node.Attribute("name") ?? provider.Name;
                skipped.Add(ext + "  " + label.Substring(0, Math.Min(label.Length, 80)) + "  " + provider.Id);
                continue;
            }
            changes.Add(Plan(Classes + ext + Slot, "", provider.Id, RegistryValueKind.String));
        }
        return changes;
    }

    private void Import()
    {
        using (var picker = new OpenFileDialog { Filter = "ThumbLab 配置 (*.thumblab)|*.thumblab", CheckFileExists = true })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            List<string> skipped;
            var changes = ReadImport(picker.FileName, out skipped);
            var applicable = changes.Where(item => !Same(item.Before, item.After)).ToList();
            string skippedText = skipped.Count == 0 ? "" : "\r\n\r\n以下组件在本机不可用，已跳过：\r\n"
                + String.Join("\r\n", skipped.Take(12)) + (skipped.Count > 12 ? "\r\n另有 " + (skipped.Count - 12) + " 项" : "");
            string changedText = String.Join("\r\n", applicable.Take(12).Select(change => change.Name == "IconsOnly"
                ? "资源管理器：" + (Equals(change.After, 0) ? "允许显示缩略图" : "始终显示图标")
                : change.Key.Split((char)92)[2] + "  →  " + Describe((string)change.After).Name));
            if (applicable.Count == 0)
            {
                MessageBox.Show(this, "没有需要应用的配置。" + skippedText, "导入配置");
                return;
            }
            string summary = "将应用 " + applicable.Count + " 项当前用户设置：\r\n" + changedText
                + (applicable.Count > 12 ? "\r\n另有 " + (applicable.Count - 12) + " 项" : "") + skippedText;
            if (MessageBox.Show(this, summary, "导入配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            Commit(applicable, "已导入 " + applicable.Count + " 项，跳过 " + skipped.Count + " 项缺少组件的配置。");
        }
    }
}
