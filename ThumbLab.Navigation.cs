using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class ThumbLab
{
    private sealed class RegistryLocation
    {
        public string Label, Path;
        public override string ToString() { return Label; }
    }

    private void UpdateRegistryLocations(string ext)
    {
        registryLocations.Items.Clear();
        AddRegistryLocation("当前用户 · " + ext, "HKEY_CURRENT_USER", Registry.CurrentUser, Classes + ext + Slot);
        AddRegistryLocation("系统配置 · " + ext, "HKEY_LOCAL_MACHINE", Registry.LocalMachine, Classes + ext + Slot);
        AddRegistryLocation("备用配置 · " + ext, "HKEY_CLASSES_ROOT", Registry.ClassesRoot, @"SystemFileAssociations\" + ext + Slot);
        var progId = Read(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId") ?? Read(Registry.ClassesRoot, ext);
        if (!String.IsNullOrWhiteSpace(progId))
            AddRegistryLocation("文件类型 · " + progId, "HKEY_CLASSES_ROOT", Registry.ClassesRoot, progId + Slot);
        var perceived = Read(Registry.ClassesRoot, ext, "PerceivedType");
        if (!String.IsNullOrWhiteSpace(perceived))
            AddRegistryLocation("通用类型 · " + perceived, "HKEY_CLASSES_ROOT", Registry.ClassesRoot, @"SystemFileAssociations\" + perceived + Slot);
        var provider = providers.SelectedItem as Provider;
        if (provider != null)
            AddRegistryLocation("所选处理器 · " + provider.Name, "HKEY_CLASSES_ROOT", Registry.ClassesRoot, @"CLSID\" + provider.Id + @"\InprocServer32");
        if (registryLocations.Items.Count > 0) registryLocations.SelectedIndex = 0;
    }

    private void AddRegistryLocation(string label, string hive, RegistryKey root, string path)
    {
        using (var key = root.OpenSubKey(path))
            if (key != null) registryLocations.Items.Add(new RegistryLocation { Label = label, Path = hive + "\\" + path });
    }

    private RegistryLocation SelectedLocation()
    {
        var location = registryLocations.SelectedItem as RegistryLocation;
        if (location == null) throw new InvalidOperationException("请选择一个已存在的注册表位置。");
        return location;
    }

    private void CopyRegistryPath()
    {
        Clipboard.SetText(SelectedLocation().Path);
        status.Text = "已复制注册表路径。";
    }

    private void OpenSelectedRegistry()
    {
        string path = SelectedLocation().Path;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            key.SetValue("LastKey", "Computer\\" + path, RegistryValueKind.String);
        using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"), "/m") { UseShellExecute = true }))
        {
            status.Text = "已请求打开注册表编辑器。如未定位，可复制路径并粘贴到其地址栏。";
        }
    }

    private void SelectProviderFile()
    {
        var provider = providers.SelectedItem as Provider;
        if (provider == null || !provider.Exists || !File.Exists(provider.Server))
            throw new InvalidOperationException("所选处理器的文件在本机不可用。");
        IntPtr item;
        Marshal.ThrowExceptionForHR(SHParseDisplayName(provider.Server, IntPtr.Zero, out item, 0, IntPtr.Zero));
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0)); }
        finally { Marshal.FreeCoTaskMem(item); }
        status.Text = "已在资源管理器中定位 " + Path.GetFileName(provider.Server) + "。";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint flags, IntPtr attributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
