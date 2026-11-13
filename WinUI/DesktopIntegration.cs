#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace ThumbLabWinUI;

internal static class DesktopIntegration
{
    public static async Task<BitmapImage> ToImageSourceAsync(Bitmap bitmap)
    {
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, ImageFormat.Png);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encoded.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    public static Task<string?> PickTestFileAsync(Window window)
    {
        return PickFileAsync(window, null);
    }

    public static Task<string?> PickImportFileAsync(Window window)
    {
        return PickFileAsync(window, ".thumblab");
    }

    public static Task<string?> PickPackageAsync(Window window) => PickFileAsync(window, ".zip");

    private static async Task<string?> PickFileAsync(Window window, string? filter)
    {
        var picker = new FileOpenPicker(window.AppWindow.Id);
        if (filter != null) picker.FileTypeFilter.Add(filter);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> PickExportFileAsync(Window window)
    {
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = "ThumbLab",
            DefaultFileExtension = ".thumblab",
            FileTypeChoices = { { "ThumbLab 配置", new List<string> { ".thumblab" } } }
        };
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static void OpenRegistry(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
        // Regedit stores its localized name for the root node in LastKey.
        var lastKey = key.GetValue("LastKey") as string;
        int hiveStart = lastKey?.IndexOf(@"\HKEY_", StringComparison.OrdinalIgnoreCase) ?? -1;
        string root = hiveStart > 0 ? lastKey![..hiveStart] : "Computer";
        key.SetValue("LastKey", root + @"\" + path, RegistryValueKind.String);
        var regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        using var process = Process.Start(new ProcessStartInfo(regedit, "/m") { UseShellExecute = true });
    }

    public static void SelectProviderFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("所选处理器的文件在本机不可用。", path);

        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out IntPtr item, 0, IntPtr.Zero));
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    public static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint flags, IntPtr attributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}

