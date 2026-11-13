using System;
using System.Collections.Generic;
using System.Drawing;
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
        ShellFileInfo info;
        IntPtr result = SHGetFileInfo(extension, FileAttributeNormal, out info,
            (uint)Marshal.SizeOf(typeof(ShellFileInfo)), GetIcon | UseFileAttributes | (large ? 0u : SmallIcon));
        if (info.Icon == IntPtr.Zero) return null;

        try
        {
            if (result == IntPtr.Zero) return null;
            using (var icon = Icon.FromHandle(info.Icon))
                return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
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

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

