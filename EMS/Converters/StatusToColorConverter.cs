using EMS.Models;
using System.Globalization;
using System.Windows.Data;

namespace EMS.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Enums.ServerStatus status)
                return Brushes.Gray;

            return status switch
            {
                Enums.ServerStatus.未连接 => Brushes.Gray,
                Enums.ServerStatus.连接中 => Brushes.Orange,
                Enums.ServerStatus.已连接 => Brushes.Green,
                Enums.ServerStatus.异常 => Brushes.Red,
                _ => Brushes.Gray
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
