#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ThumbLabWinUI;

public sealed class ExplorerFileDrop : IDisposable
{
    private readonly IntPtr window;
    private readonly DropTarget target;

    internal ExplorerFileDrop(IntPtr window, Action<string?> dropped)
    {
        this.window = window;
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            target = new DropTarget(dropped);
            Marshal.ThrowExceptionForHR(RegisterDragDrop(window, target));
        }
        catch
        {
            OleUninitialize();
            throw;
        }
    }

    public void Dispose()
    {
        RevokeDragDrop(window);
        OleUninitialize();
        GC.KeepAlive(target);
    }

    [ComVisible(true), Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject data, uint keys, Point point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, Point point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject data, uint keys, Point point, ref uint effect);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget(Action<string?> dropped) : IDropTarget
    {
        private const uint Copy = 1;
        private const uint Count = uint.MaxValue;
        private bool acceptsFiles;

        private static FORMATETC FileFormat => new()
        {
            cfFormat = 15, // CF_HDROP
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        public int DragEnter(IDataObject data, uint keys, Point point, ref uint effect)
        {
            var format = FileFormat;
            acceptsFiles = data.QueryGetData(ref format) == 0;
            return DragOver(keys, point, ref effect);
        }

        public int DragOver(uint keys, Point point, ref uint effect)
        {
            effect = acceptsFiles ? effect & Copy : 0;
            return 0;
        }

        public int DragLeave()
        {
            acceptsFiles = false;
            return 0;
        }

        public int Drop(IDataObject data, uint keys, Point point, ref uint effect)
        {
            effect = acceptsFiles ? effect & Copy : 0;
            acceptsFiles = false;
            if (effect == 0) return 0;

            string? path = null;
            try
            {
                var format = FileFormat;
                data.GetData(ref format, out var medium);
                try
                {
                    if (medium.tymed == TYMED.TYMED_HGLOBAL &&
                        DragQueryFile(medium.unionmember, Count, null, 0) == 1)
                    {
                        uint length = DragQueryFile(medium.unionmember, 0, null, 0);
                        if (length > 0 && length <= 32767)
                        {
                            var buffer = new StringBuilder((int)length + 1);
                            if (DragQueryFile(medium.unionmember, 0, buffer, (uint)buffer.Capacity) == length)
                                path = buffer.ToString();
                        }
                    }
                }
                finally { ReleaseStgMedium(ref medium); }
            }
            catch (COMException) { }

            effect = path == null ? 0 : Copy;
            dropped(path);
            return 0;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr window, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr window);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);
}

