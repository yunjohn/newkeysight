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

public static class ChannelPalette
{
    public const string Channel1 = "#FFFF00";
    public const string Channel2 = "#00FF00";
    public const string Channel3 = "#2672FF";
    public const string Channel4 = "#FF00FF";

    public static string Hex(string channel)
    {
        string digits = new(channel.Where(char.IsDigit).ToArray());
        return digits switch
        {
            "1" => Channel1,
            "2" => Channel2,
            "3" => Channel3,
            "4" => Channel4,
            _ => "#D7DEE8"
        };
    }
}

public sealed class ChannelDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ChannelDisplayName.Format(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
