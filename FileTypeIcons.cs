using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

internal sealed class FileTypeIcons : IDisposable
{
    private const uint FileAttributeNormal = 0x80;
    private const uint GetIcon = 0x100;
    private const uint SmallIcon = 0x1;
    private const uint UseFileAttributes = 0x10;

    private readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

    // The caller borrows the bitmap. This cache owns and disposes it.
    public Bitmap Get(string extension, bool large = false)
    {
        string key = large ? "large:" + extension : extension;
        Bitmap bitmap;
        if (!icons.TryGetValue(key, out bitmap))
        {
            bitmap = LoadIcon(extension, large);
            icons.Add(key, bitmap);
        }
        return bitmap;
    }

    private static Bitmap LoadIcon(string extension, bool large)
    {
        // Read the icon registration without invoking the file type's Shell extensions.
        var value = new StringBuilder(32768);
        uint length = (uint)value.Capacity;
        IntPtr handle = IntPtr.Zero;
        if (AssocQueryString(0, 15, extension, null, value, ref length) == 0)
        {
            string location = Environment.ExpandEnvironmentVariables(value.ToString().Trim());
            int separator = location.LastIndexOf(',');
            int index = 0;
            if (separator >= 0 && int.TryParse(location.Substring(separator + 1).Trim(), out index))
                location = location.Substring(0, separator);
            location = location.Trim().Trim('"');
            if (Path.IsPathRooted(location) && !location.StartsWith(@"\") && File.Exists(location))
            {
                var result = new IntPtr[1];
                ExtractIconEx(location, index, large ? result : null, large ? null : result, 1);
                handle = result[0];
            }
        }
        if (handle == IntPtr.Zero)
        {
            // A neutral name obtains the Windows stock file icon.
            ShellFileInfo info;
            SHGetFileInfo("ThumbLab-unassociated", FileAttributeNormal, out info,
                (uint)Marshal.SizeOf(typeof(ShellFileInfo)), GetIcon | UseFileAttributes | (large ? 0u : SmallIcon));
            handle = info.Icon;
        }
        if (handle == IntPtr.Zero) return null;
        try
        {
            using (var icon = Icon.FromHandle(handle))
                return icon.ToBitmap();
        }
        finally { DestroyIcon(handle); }
    }

    public void Clear()
    {
        foreach (var bitmap in icons.Values)
            if (bitmap != null) bitmap.Dispose();
        icons.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint kind, string association, string extra, StringBuilder output, ref uint length);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[] large, [Out] IntPtr[] small, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
