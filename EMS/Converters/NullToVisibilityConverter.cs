using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EMS.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter is string s && s == "Inverted";
            bool isNull = value == null;

            if (invert)
            {
                return isNull ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
