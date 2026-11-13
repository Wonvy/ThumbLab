using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class ThumbnailShell
{
    private const string ThumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}";
    private const uint AssocStringShellExtension = 16;
    private const uint AssocChanged = 0x08000000;
    private const uint FlushNoWait = 0x2000;
    private const uint ForceExtraction = 0x4;
    private const uint ArgbThumbnail = 2;
    private static readonly Guid ShellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid LocalThumbnailCacheId = new Guid("50EF4544-AC9F-4A8E-B21B-8A26180DB13F");

    public static string Effective(string extension)
    {
        var result = new StringBuilder(64);
        uint size = (uint)result.Capacity;
        int hr = AssocQueryString(0, AssocStringShellExtension, extension, ThumbnailHandler, result, ref size);
        Guid provider;
        return hr == 0 && Guid.TryParse(result.ToString(), out provider)
            ? provider.ToString("B").ToUpperInvariant()
            : null;
    }

    public static Bitmap Extract(string path, bool force)
    {
        IntPtr item = IntPtr.Zero;
        object cacheObject = null;
        ISharedBitmap thumbnail = null;
        try
        {
            Guid iid = ShellItemId;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out item));
            cacheObject = Activator.CreateInstance(Type.GetTypeFromCLSID(LocalThumbnailCacheId, true));
            var cache = (IThumbnailCache)cacheObject;
            Marshal.ThrowExceptionForHR(cache.GetThumbnail(item, 256, force ? ForceExtraction : 0,
                out thumbnail, IntPtr.Zero, IntPtr.Zero));
            if (thumbnail == null)
                throw new InvalidOperationException("Windows did not return a thumbnail.");

            IntPtr bitmap;
            Marshal.ThrowExceptionForHR(thumbnail.GetSharedBitmap(out bitmap));
            if (bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Windows returned an empty thumbnail.");

            uint format;
            // GDI+ copies the pixels but FromHbitmap drops the alpha channel.
            // The shared bitmap still owns the original HBITMAP and pixel memory.
            if (thumbnail.GetFormat(out format) != 0 || format != ArgbThumbnail)
                return Image.FromHbitmap(bitmap);

            Size size;
            if (thumbnail.GetSize(out size) != 0 || size.Width <= 0 || size.Height <= 0)
                return Image.FromHbitmap(bitmap);

            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                Width = size.Width,
                Height = -size.Height,
                Planes = 1,
                BitsPerPixel = 32
            };
            int rowBytes = size.Width * 4;
            var pixels = new byte[rowBytes * size.Height];
            IntPtr device = GetDC(IntPtr.Zero);
            if (device == IntPtr.Zero)
                return Image.FromHbitmap(bitmap);
            try
            {
                if (GetDIBits(device, bitmap, 0, (uint)size.Height, pixels, ref header, 0) != size.Height)
                    return Image.FromHbitmap(bitmap);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, device);
            }

            var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                var area = new Rectangle(Point.Empty, result.Size);
                BitmapData data = result.LockBits(area, ImageLockMode.WriteOnly, result.PixelFormat);
                try
                {
                    for (int y = 0; y < area.Height; y++)
                        Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                }
                finally
                {
                    result.UnlockBits(data);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        finally
        {
            if (thumbnail != null) Marshal.ReleaseComObject(thumbnail);
            if (cacheObject != null) Marshal.ReleaseComObject(cacheObject);
            if (item != IntPtr.Zero) Marshal.Release(item);
        }
    }

    public static void Notify()
    {
        SHChangeNotify(AssocChanged, FlushNoWait, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, uint str, string association, string extra,
        StringBuilder result, ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext,
        ref Guid iid, out IntPtr shellItem);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr device);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(IntPtr device, IntPtr bitmap, uint start, uint count,
        [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitsPerPixel;
        public uint Compression, SizeImage;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [ComImport, Guid("F676C15D-596A-4CE2-8234-33996F445DB1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailCache
    {
        [PreserveSig]
        int GetThumbnail(IntPtr shellItem, uint size, uint flags,
            [MarshalAs(UnmanagedType.Interface)] out ISharedBitmap bitmap, IntPtr cacheFlags, IntPtr thumbnailId);
    }

    [ComImport, Guid("091162A4-BC96-411F-AAE8-C5122CD03363"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISharedBitmap
    {
        [PreserveSig]
        int GetSharedBitmap(out IntPtr bitmap);

        [PreserveSig]
        int GetSize(out Size dimensions);

        [PreserveSig]
        int GetFormat(out uint format);
    }
}
