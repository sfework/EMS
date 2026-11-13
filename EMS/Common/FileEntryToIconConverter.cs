using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using EMS.Services.Sftp;

namespace EMS.Common
{
    /// <summary>
    /// 远程文件条目 → Windows 系统图标：与资源管理器一致，按目录/扩展名
    /// 走 shell 图标接口（不依赖本机存在该文件），进程内按类型缓存。
    /// </summary>
    public class FileEntryToIconConverter : System.Windows.Data.IValueConverter
    {
        private static readonly ConcurrentDictionary<string, System.Windows.Media.ImageSource?> Cache = new();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not RemoteFileEntry entry) return null;

            var key = entry.IsDirectory ? "*" : Path.GetExtension(entry.Name).ToLowerInvariant();
            return Cache.GetOrAdd(key, _ => GetIcon(entry.IsDirectory, key));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static System.Windows.Media.ImageSource? GetIcon(bool isDirectory, string extension)
        {
            // USEFILEATTRIBUTES：按传入的名称/扩展名取图标，无需真实路径
            var fileName = isDirectory ? "directory" : "file" + extension;
            var attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            var shfi = new SHFILEINFO();
            if (SHGetFileInfo(fileName, attrs, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero ||
                shfi.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
