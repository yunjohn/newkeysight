using System.Diagnostics;
using System.Text.Json;
using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Instruments;

var factory = new KeysightVisaSessionFactory();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
CancellationToken token = timeout.Token;

try
{
    VisaRuntimeStatus runtime = await factory.CheckRuntimeAsync(token);
    if (!runtime.IsAvailable)
    {
        Console.Error.WriteLine(runtime.Message);
        return 2;
    }

    IReadOnlyList<string> resources = await factory.FindResourcesAsync(token);
    string? requested = GetOption(args, "--resource");
    if (requested is null && resources.Count > 1)
    {
        Console.Error.WriteLine("扫描到多个 VISA 资源，请使用 --resource 明确指定：");
        foreach (string item in resources) Console.Error.WriteLine($"  {item}");
        return 7;
    }
    string? resource = requested ?? (resources.Count == 1 ? resources[0] : null);
    if (resource is null)
    {
        Console.Error.WriteLine("未扫描到 VISA 仪器资源。");
        return 3;
    }
    if (!resources.Contains(resource, StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"指定资源不在扫描结果中：{resource}");
        Console.Error.WriteLine($"当前扫描结果：{string.Join(", ", resources)}");
        return 4;
    }

    Stopwatch timer = Stopwatch.StartNew();
    Console.Error.WriteLine("步骤 1/7：打开 VISA 会话");
    await using IVisaSession session = await factory.OpenAsync(resource, 10_000, token);
    await using var transport = new VisaScopeTransport(session, resource);
    var scope = new KeysightOscilloscope(transport);
    Console.Error.WriteLine("步骤 2/7：读取设备身份");
    InstrumentIdentity identity = await scope.IdentifyAsync(token);
    Console.Error.WriteLine("步骤 3/7：读取系统错误队列");
    string systemError = await scope.GetSystemErrorAsync(token);
    Console.Error.WriteLine("步骤 4/7：读取触发状态");
    string triggerStatus = await scope.GetTriggerStatusAsync(token);
    Console.Error.WriteLine("步骤 5/7：读取时基与采集模式");
    ScopeOperatingSettings operating = await scope.GetOperatingSettingsAsync(token);
    Console.Error.WriteLine("步骤 6/7：读取四通道垂直设置");
    var channels = new Dictionary<string, ChannelVerticalSettings>();
    foreach (string channel in ScopeChannels.All)
        channels[channel] = await scope.GetChannelVerticalAsync(channel, token);

    bool functional = args.Contains("--functional", StringComparer.OrdinalIgnoreCase);
    string? outputDirectory = null;
    string? csvPath = null;
    string? screenshotPath = null;
    string? systemErrorAfter = null;
    double? captureMilliseconds = null;
    Dictionary<string, object>? waveformResults = null;
    if (functional)
    {
        Console.Error.WriteLine("步骤 7/7：执行抓波、CSV 往返与截图");
        outputDirectory = Path.GetFullPath(
            GetOption(args, "--output") ??
            Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "hardware-smoke",
                DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outputDirectory);
        csvPath = Path.Combine(outputDirectory, "captured-waveforms.csv");
        screenshotPath = Path.Combine(outputDirectory, "device-screen.png");
        string[] captureChannels = channels
            .Where(item => item.Value.IsDisplayed)
            .Select(item => item.Key)
            .Take(3)
            .ToArray();
        if (captureChannels.Length == 0) captureChannels = ["CHANnel1"];

        try
        {
            await scope.StopAsync(token);
            CaptureResult capture = await scope.CaptureAsync(
                new CaptureRequest(captureChannels, "NORMal", 6_400, "NORMal"),
                token: token);
            captureMilliseconds = capture.Elapsed.TotalMilliseconds;
            var csv = new WaveformCsvService();
            await csv.SaveBundleAsync(capture.Bundle, csvPath, cancellationToken: token);
            WaveformBundle roundTrip = await csv.LoadAsync(csvPath, cancellationToken: token);
            waveformResults = roundTrip.Channels.Values.ToDictionary(
                waveform => waveform.Channel,
                waveform =>
                {
                    WaveformStats stats = WaveformAnalysis.Analyze(waveform);
                    return (object)new
                    {
                        points = waveform.Count,
                        startSeconds = waveform.Range.Minimum,
                        endSeconds = waveform.Range.Maximum,
                        minimum = stats.Minimum,
                        maximum = stats.Maximum,
                        peakToPeak = stats.PeakToPeak,
                        rms = stats.Rms,
                        frequencyHz = stats.FrequencyHz
                    };
                },
                StringComparer.OrdinalIgnoreCase);
            await scope.CaptureScreenshotAsync(screenshotPath, token);
            systemErrorAfter = await scope.GetSystemErrorAsync(token);
        }
        finally
        {
            await scope.SetOperatingSettingsAsync(operating, CancellationToken.None);
            if (triggerStatus is not "STOP")
                await scope.RunAsync(CancellationToken.None);
        }
    }
    timer.Stop();

    var result = new
    {
        checkedAt = DateTimeOffset.Now,
        runtime = runtime.Message,
        scannedResourceCount = resources.Count,
        resource,
        identity,
        systemError,
        triggerStatus,
        operating,
        channels,
        functional,
        outputDirectory,
        csvPath,
        screenshotPath,
        captureMilliseconds,
        waveformResults,
        systemErrorAfter,
        elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
        verdict = (systemError.TrimStart().StartsWith("+0", StringComparison.Ordinal) ||
                   systemError.TrimStart().StartsWith('0')) &&
                  (systemErrorAfter is null ||
                   systemErrorAfter.TrimStart().StartsWith("+0", StringComparison.Ordinal) ||
                   systemErrorAfter.TrimStart().StartsWith('0'))
            ? "通过"
            : "设备报告错误"
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return result.verdict == "通过" ? 0 : 5;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("硬件只读验收超时。");
    return 6;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"硬件只读验收失败：{ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

static string? GetOption(string[] arguments, string name)
{
    int index = Array.FindIndex(arguments, value =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
