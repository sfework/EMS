using System.Globalization;
using System.Windows.Data;

namespace EMS.Converters
{
    /// <summary>
    /// bool → 菜单文本："连接"/"断开"
    /// </summary>
    public class ConnectActionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? "断开" : "连接";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
