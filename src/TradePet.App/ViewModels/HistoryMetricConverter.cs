using System.Globalization;
using System.Windows.Data;

namespace TradePet.App.ViewModels;

public sealed class HistoryMetricConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is true && values[1] is IFormattable value
            ? value.ToString(parameter as string, culture)
            : "—";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
