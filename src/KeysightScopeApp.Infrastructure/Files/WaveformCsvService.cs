using System.Globalization;
using System.Text;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Infrastructure.Files;

public sealed class WaveformCsvException(string message, int? lineNumber = null, Exception? inner = null)
    : IOException(lineNumber is null ? message : $"{message}（第 {lineNumber} 行）", inner)
{
    public int? LineNumber { get; } = lineNumber;
}

public sealed class WaveformCsvService
{
    private const string BundleHeaderV1 = "# KEYSIGHT_SCOPE_BUNDLE_V1";
    private const string BundleHeaderV2 = "# KEYSIGHT_SCOPE_BUNDLE_V2";

    public async Task<WaveformBundle> LoadAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long length = new FileInfo(path).Length;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1 << 16, false);
        string? first = await reader.ReadLineAsync(cancellationToken);
        if (first is null) throw new WaveformCsvException("CSV 文件为空。");

        var waveforms = new List<WaveformData>();
        string header = TrimCsvQuotes(first);
        if (header.Equals(BundleHeaderV1, StringComparison.Ordinal) ||
            header.Equals(BundleHeaderV2, StringComparison.Ordinal))
            await ReadBundleAsync(reader, stream, length, waveforms, progress, cancellationToken);
        else
            waveforms.Add(await ReadSingleAsync(first, reader, stream, length, progress, cancellationToken));

        if (waveforms.Count == 0) throw new WaveformCsvException("波形包中没有通道数据。");
        progress?.Report(1);
        return new WaveformBundle(waveforms);
    }

    public async Task SaveBundleAsync(
        WaveformBundle bundle,
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync(BundleHeaderV2);
                int channelIndex = 0;
                foreach (WaveformData waveform in bundle.Channels.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync($"\"# {BuildMetadata(waveform)}\"");
                    await writer.WriteLineAsync("time_s,voltage_v");
                    for (int i = 0; i < waveform.Count; i++)
                    {
                        if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(FormattableString.Invariant($"{waveform.X[i]:e12},{waveform.Y[i]:e12}"));
                    }
                    await writer.WriteLineAsync();
                    progress?.Report((double)++channelIndex / bundle.Channels.Count);
                }
                await writer.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static async Task ReadBundleAsync(
        StreamReader reader, Stream stream, long length, List<WaveformData> output,
        IProgress<double>? progress, CancellationToken token)
    {
        string? channel = null, mode = "FILE", unit = "V";
        WaveformPreamble? preamble = null;
        ChannelAcquisitionMetadata? acquisition = null;
        var x = new List<double>();
        var y = new List<double>();
        int line = 1;
        while (await reader.ReadLineAsync(token) is { } raw)
        {
            line++;
            string value = TrimCsvQuotes(raw).Trim();
            if (value.Length == 0) { AddCurrent(); continue; }
            if (value.StartsWith("# channel=", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrent();
                Dictionary<string, string> metadata = value[2..].Split(',')
                    .Select(item => item.Split('=', 2))
                    .Where(item => item.Length == 2)
                    .ToDictionary(
                        item => item[0].Trim(),
                        item => Uri.UnescapeDataString(item[1].Trim()),
                        StringComparer.OrdinalIgnoreCase);
                if (!metadata.TryGetValue("channel", out channel))
                    throw new WaveformCsvException("通道区段缺少 channel 元数据。", line);
                mode = metadata.GetValueOrDefault("points_mode", "FILE");
                unit = metadata.GetValueOrDefault("unit", "V");
                preamble = ReadPreamble(metadata);
                acquisition = ReadAcquisition(metadata);
                continue;
            }
            if (value.Equals("time_s,voltage_v", StringComparison.OrdinalIgnoreCase)) continue;
            if (channel is null) throw new WaveformCsvException("数据行前缺少通道区段。", line);
            ParseData(value, line, x, y);
            if ((line & 4095) == 0) progress?.Report(length == 0 ? 0 : (double)stream.Position / length);
        }
        AddCurrent();

        void AddCurrent()
        {
            if (channel is null) return;
            if (x.Count == 0) throw new WaveformCsvException($"通道 {channel} 没有数据。", line);
            output.Add(new WaveformData(channel, [.. x], [.. y], mode, unit, preamble, acquisition));
            channel = null;
            preamble = null;
            acquisition = null;
            x.Clear();
            y.Clear();
        }
    }

    private static string BuildMetadata(WaveformData waveform)
    {
        var fields = new List<(string Key, string? Value)>
        {
            ("channel", waveform.Channel),
            ("points_mode", waveform.PointsMode),
            ("unit", waveform.Unit)
        };
        if (waveform.Preamble is { } p)
        {
            fields.AddRange([
                ("pre_format", Invariant(p.Format)), ("pre_type", Invariant(p.Type)),
                ("pre_points", Invariant(p.Points)), ("pre_count", Invariant(p.Count)),
                ("x_increment", Invariant(p.XIncrement)), ("x_origin", Invariant(p.XOrigin)),
                ("x_reference", Invariant(p.XReference)), ("y_increment", Invariant(p.YIncrement)),
                ("y_origin", Invariant(p.YOrigin)), ("y_reference", Invariant(p.YReference))
            ]);
        }
        if (waveform.Acquisition is { } a)
        {
            fields.AddRange([
                ("probe_attenuation", Invariant(a.ProbeAttenuation)),
                ("probe_id", a.ProbeId), ("probe_type", a.ProbeType),
                ("vertical_scale", Invariant(a.VerticalScale)),
                ("vertical_offset", Invariant(a.VerticalOffset)),
                ("coupling", a.Coupling), ("input_impedance", a.InputImpedance),
                ("bandwidth_limit", a.BandwidthLimit),
                ("inverted", Invariant(a.Inverted)), ("displayed", Invariant(a.Displayed)),
                ("label", a.Label)
            ]);
        }
        return string.Join(',', fields
            .Where(field => field.Value is not null)
            .Select(field => $"{field.Key}={Uri.EscapeDataString(field.Value!)}"));
    }

    private static WaveformPreamble? ReadPreamble(Dictionary<string, string> metadata)
    {
        if (!metadata.ContainsKey("pre_format")) return null;
        return new(
            Integer("pre_format"), Integer("pre_type"), Integer("pre_points"), Integer("pre_count", 1),
            Number("x_increment"), Number("x_origin"), Number("x_reference"),
            Number("y_increment", 1), Number("y_origin"), Number("y_reference"));

        int Integer(string key, int fallback = 0) =>
            int.TryParse(metadata.GetValueOrDefault(key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int value) ? value : fallback;
        double Number(string key, double fallback = 0) =>
            double.TryParse(metadata.GetValueOrDefault(key), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }

    private static ChannelAcquisitionMetadata? ReadAcquisition(
        Dictionary<string, string> metadata)
    {
        string[] keys = ["probe_attenuation", "probe_id", "probe_type", "vertical_scale",
            "vertical_offset", "coupling", "input_impedance", "bandwidth_limit", "inverted",
            "displayed", "label"];
        if (!keys.Any(metadata.ContainsKey)) return null;
        return new(
            Number("probe_attenuation"), Text("probe_id"), Text("probe_type"),
            Number("vertical_scale"), Number("vertical_offset"), Text("coupling"),
            Text("input_impedance"), Text("bandwidth_limit"), Boolean("inverted"),
            Boolean("displayed"), Text("label"));

        string? Text(string key) => metadata.GetValueOrDefault(key);
        double? Number(string key) =>
            double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value) && double.IsFinite(value) ? value : null;
        bool? Boolean(string key) => Text(key)?.ToLowerInvariant() switch
        {
            "true" or "1" or "on" => true,
            "false" or "0" or "off" => false,
            _ => null
        };
    }

    private static string Invariant<T>(T value) where T : IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);
    private static string? Invariant<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture);
    private static string? Invariant(bool? value) => value?.ToString().ToLowerInvariant();

    private static async Task<WaveformData> ReadSingleAsync(
        string first, StreamReader reader, Stream stream, long length,
        IProgress<double>? progress, CancellationToken token)
    {
        if (!TrimCsvQuotes(first).Trim().Equals("time_s,voltage_v", StringComparison.OrdinalIgnoreCase))
            throw new WaveformCsvException("CSV 缺少 time_s,voltage_v 表头。", 1);
        var x = new List<double>();
        var y = new List<double>();
        int line = 1;
        while (await reader.ReadLineAsync(token) is { } raw)
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            ParseData(raw, line, x, y);
            if ((line & 4095) == 0) progress?.Report(length == 0 ? 0 : (double)stream.Position / length);
        }
        if (x.Count == 0) throw new WaveformCsvException("CSV 没有波形数据。");
        return new WaveformData("CSV", [.. x], [.. y]);
    }

    private static void ParseData(string raw, int line, List<double> x, List<double> y)
    {
        string[] fields = raw.Split(',');
        if (fields.Length != 2 ||
            !double.TryParse(TrimCsvQuotes(fields[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out double time) ||
            !double.TryParse(TrimCsvQuotes(fields[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(time) || !double.IsFinite(value))
            throw new WaveformCsvException("无法解析波形数据。", line);
        if (x.Count > 0 && time <= x[^1])
            throw new WaveformCsvException("时间轴必须严格递增。", line);
        x.Add(time);
        y.Add(value);
    }

    private static string TrimCsvQuotes(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\"\"", "\"") : value;
}
