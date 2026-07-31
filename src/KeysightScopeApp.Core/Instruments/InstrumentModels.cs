using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Instruments;

public static class ScopeChannels
{
    public static readonly string[] All = ["CHANnel1", "CHANnel2", "CHANnel3", "CHANnel4"];
    public static bool IsValid(string channel) => All.Contains(channel, StringComparer.Ordinal);
}

public sealed record InstrumentIdentity(string Manufacturer, string Model, string SerialNumber, string Firmware)
{
    public static InstrumentIdentity Parse(string value)
    {
        string[] fields = value.Split(',').Select(item => item.Trim()).ToArray();
        if (fields.Length < 4) throw new FormatException($"无法解析设备标识：{value}");
        return new(fields[0], fields[1], fields[2], string.Join(",", fields[3..]));
    }
}

public sealed record InstrumentCapabilities(
    string[] WaveformPointsModes,
    int? MaximumPoints,
    bool SupportsEdgeTrigger,
    IReadOnlyList<string> Warnings);

public sealed record CaptureRequest(
    IReadOnlyList<string> Channels,
    string PointsMode = "NORMal",
    int Points = 20000,
    string AcquireType = "NORMal",
    bool FullDeepMemory = false);

public sealed record CaptureResult(
    CaptureRequest Request,
    WaveformBundle Bundle,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings);

public sealed record EdgeTriggerSettings(string Source, string Slope, double Level, string Sweep);
public sealed record ChannelVerticalSettings(double Scale, double Offset, bool IsDisplayed);
public sealed record ScopeOperatingSettings(string TimebaseMode, string AcquireType);

public class ScopeException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ScopeConnectionException(string message, Exception? inner = null) : ScopeException(message, inner);
public sealed class ScopeProtocolException(string message, Exception? inner = null) : ScopeException(message, inner);
public sealed class WaveformIntegrityException(string message) : ScopeException(message);
