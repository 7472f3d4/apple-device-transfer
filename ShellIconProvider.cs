using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppleDeviceTransfer;

internal static class ShellIconProvider
{
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiUseFileAttributes = 0x00000010;

    private static readonly ConcurrentDictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource GetIcon(bool isDirectory, string name)
    {
        var extension = isDirectory ? "<folder>" : Path.GetExtension(name);
        var key = isDirectory ? "<folder>" : string.IsNullOrEmpty(extension) ? "<file>" : extension;
        return Icons.GetOrAdd(key, _ => ReadShellIcon(isDirectory, extension));
    }

    private static ImageSource ReadShellIcon(bool isDirectory, string extension)
    {
        var path = isDirectory ? "transfer-folder" : $"transfer-file{extension}";
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var info = new ShellFileInfo { DisplayName = string.Empty, TypeName = string.Empty };
        var result = SHGetFileInfo(path, attributes, ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);

        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            return CreateFallbackIcon(isDirectory);

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static BitmapSource CreateFallbackIcon(bool isDirectory)
    {
        var geometry = Geometry.Parse(isDirectory
            ? "M1.5,4 L6.2,4 L7.6,5.5 L14.5,5.5 L14.5,13.5 L1.5,13.5 Z"
            : "M4,1.5 L10,1.5 L13.5,5 L13.5,14.5 L4,14.5 Z M10,1.5 L10,5 L13.5,5");
        var brush = new SolidColorBrush(isDirectory
            ? Color.FromRgb(232, 179, 49)
            : Color.FromRgb(130, 146, 165));
        brush.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(87, 101, 119)), 0.7);
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawGeometry(brush, pen, geometry);

        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
