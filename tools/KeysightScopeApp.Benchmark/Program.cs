using System.Diagnostics;
using System.Globalization;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("用法：KeysightScopeApp.Benchmark <waveform.csv>");
    return 2;
}

string path = Path.GetFullPath(args[0]);
long memoryBefore = GC.GetTotalMemory(true);
var loadTimer = Stopwatch.StartNew();
WaveformBundle bundle = await new WaveformCsvService().LoadAsync(path);
loadTimer.Stop();

var processingTimer = Stopwatch.StartNew();
PreparedWaveformDisplay[] displays = await Task.Run(() =>
    bundle.Channels.Values
        .Select(waveform => EnvelopeDecimator.Prepare(waveform, waveform.Range, 1920))
        .ToArray());
WaveformStats[] stats = await Task.Run(() =>
    bundle.Channels.Values.Select(waveform => WaveformAnalysis.Analyze(waveform)).ToArray());
processingTimer.Stop();
long memoryAfter = GC.GetTotalMemory(false);

Console.WriteLine($"文件={path}");
Console.WriteLine($"通道={string.Join(',', bundle.Channels.Keys)}");
Console.WriteLine($"原始点数={bundle.Channels.Values.Sum(item => item.Count):N0}");
Console.WriteLine($"显示点数={displays.Sum(item => item.X.Length):N0}");
Console.WriteLine($"加载毫秒={loadTimer.Elapsed.TotalMilliseconds:F2}");
Console.WriteLine($"抽稀与统计毫秒={processingTimer.Elapsed.TotalMilliseconds:F2}");
Console.WriteLine($"托管内存增量MB={(memoryAfter - memoryBefore) / 1024d / 1024d:F2}");
Console.WriteLine($"频率={string.Join(',', stats.Select(item =>
    item.FrequencyHz?.ToString("G8", CultureInfo.InvariantCulture) ?? "--"))}");
return 0;
