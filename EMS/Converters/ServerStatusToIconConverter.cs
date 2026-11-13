using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using EMS.Models;

namespace EMS.Converters
{
    /// <summary>
    /// 图标按 URI 全局缓存（Freeze 后可跨线程共享），避免树节点状态刷新时重复解码 PNG
    /// </summary>
    internal static class IconCache
    {
        private static readonly Dictionary<string, BitmapImage> _cache = new();

        public static BitmapImage? Get(string iconPath)
        {
            if (_cache.TryGetValue(iconPath, out var cached)) return cached;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                _cache[iconPath] = bitmap;
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"图标加载失败 {iconPath}: {ex.Message}");
                return null;
            }
        }
    }

    public class ServerStatusToIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Enums.ServerStatus status)
            {
                return null;
            }

            string iconPath = status switch
            {
                Enums.ServerStatus.未连接 => "pack://application:,,,/Resources/Icons/disconnected.png",
                Enums.ServerStatus.连接中 => "pack://application:,,,/Resources/Icons/connecting.png",
                Enums.ServerStatus.已连接 => "pack://application:,,,/Resources/Icons/connected.png",
                Enums.ServerStatus.异常 => "pack://application:,,,/Resources/Icons/error.png",
                _ => "pack://application:,,,/Resources/Icons/disconnected.png",
            };

            return IconCache.Get(iconPath);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class GroupExpanderIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isExpanded = value is bool b && b;
            string iconPath = isExpanded
                ? "pack://application:,,,/Resources/Icons/folder_open.png"
                : "pack://application:,,,/Resources/Icons/folder_closed.png";

            return IconCache.Get(iconPath);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
