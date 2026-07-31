using System.Globalization;
using System.Windows.Data;

namespace KeysightScopeApp.App;

public static class ChannelDisplayName
{
    public static string Format(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        string upper = value.Trim().ToUpperInvariant();
        for (int channel = 1; channel <= 4; channel++)
        {
            if (upper == $"CHANNEL{channel}" ||
                upper == $"CHAN{channel}" ||
                upper == $"CH{channel}")
                return $"CH{channel}";
        }
        return value;
    }
}

public sealed class ChannelDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ChannelDisplayName.Format(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
