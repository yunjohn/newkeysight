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
    private const string BundleHeader = "# KEYSIGHT_SCOPE_BUNDLE_V1";

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
        if (TrimCsvQuotes(first).Equals(BundleHeader, StringComparison.Ordinal))
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
                await writer.WriteLineAsync(BundleHeader);
                int channelIndex = 0;
                foreach (WaveformData waveform in bundle.Channels.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(
                        $"\"# channel={waveform.Channel},points_mode={waveform.PointsMode},unit={waveform.Unit}\"");
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
                    .ToDictionary(item => item[0].Trim(), item => item[1].Trim(), StringComparer.OrdinalIgnoreCase);
                if (!metadata.TryGetValue("channel", out channel))
                    throw new WaveformCsvException("通道区段缺少 channel 元数据。", line);
                mode = metadata.GetValueOrDefault("points_mode", "FILE");
                unit = metadata.GetValueOrDefault("unit", "V");
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
            output.Add(new WaveformData(channel, [.. x], [.. y], mode, unit));
            channel = null;
            x.Clear();
            y.Clear();
        }
    }

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
