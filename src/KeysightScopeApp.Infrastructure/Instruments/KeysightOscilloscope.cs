using System.Diagnostics;
using System.Globalization;
using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Infrastructure.Instruments;

public sealed class KeysightOscilloscope(IScopeTransport transport)
{
    public const int ChunkedReadThreshold = 500_000;

    public async Task<InstrumentIdentity> IdentifyAsync(CancellationToken token = default)
    {
        InstrumentIdentity identity = InstrumentIdentity.Parse(await transport.QueryAsync("*IDN?", token));
        if (!identity.Manufacturer.Contains("KEYSIGHT", StringComparison.OrdinalIgnoreCase) &&
            !identity.Manufacturer.Contains("AGILENT", StringComparison.OrdinalIgnoreCase))
            throw new ScopeProtocolException($"检测到的设备不是 Keysight/Agilent 示波器：{identity.Manufacturer}");
        return identity;
    }

    public async Task SetChannelDisplayAsync(string channel, bool enabled, CancellationToken token = default)
    {
        ValidateChannel(channel);
        await transport.WriteAsync($":{channel}:DISPlay {(enabled ? "ON" : "OFF")}", token);
    }

    public async Task<bool> GetChannelDisplayAsync(
        string channel,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        string response = (await transport.QueryAsync($":{channel}:DISPlay?", token))
            .Trim().Trim('"');
        if (response.Equals("ON", StringComparison.OrdinalIgnoreCase)) return true;
        if (response.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        return ParseDouble(response) != 0;
    }

    public Task RunAsync(CancellationToken token = default) =>
        transport.WriteAsync(":RUN", token);

    public Task StopAsync(CancellationToken token = default) =>
        transport.WriteAsync(":STOP", token);

    public async Task SaveChannelToReferenceAsync(
        string channel,
        int referenceSlot,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        ValidateReferenceSlot(referenceSlot);
        await transport.WriteAsync($":WMEMory{referenceSlot}:SAVE {channel}", token);
        await transport.WriteAsync($":WMEMory{referenceSlot}:DISPlay ON", token);
        await EnsureNoSystemErrorAsync("保存参考波形", token);
    }

    public async Task UploadReferenceWaveformAsync(
        string path,
        int referenceSlot,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateReferenceSlot(referenceSlot);
        if (!Path.GetExtension(path).Equals(".h5", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("DSO-X 3000T 只能召回 Keysight 参考波形 .h5 文件。", nameof(path));
        byte[] data = await File.ReadAllBytesAsync(path, token);
        ReadOnlySpan<byte> hdf5Signature = [137, 72, 68, 70, 13, 10, 26, 10];
        if (data.Length < hdf5Signature.Length ||
            !data.AsSpan(0, hdf5Signature.Length).SequenceEqual(hdf5Signature))
            throw new WaveformIntegrityException("所选文件不是有效的 HDF5/Keysight 参考波形文件。");
        await transport.WriteBinaryBlockAsync($":RECall:WMEMory{referenceSlot}", data, token);
        await transport.WriteAsync($":WMEMory{referenceSlot}:DISPlay ON", token);
        await EnsureNoSystemErrorAsync("上传参考波形", token);
    }

    public async Task SaveReferenceFileToDeviceStorageAsync(
        string channel,
        string fileName,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        string normalized = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("请输入参考波形文件名。", nameof(fileName));
        if (!Path.GetExtension(normalized).Equals(".h5", StringComparison.OrdinalIgnoreCase))
            normalized += ".h5";
        if (normalized.Contains('"'))
            throw new ArgumentException("文件名不能包含双引号。", nameof(fileName));

        // Errors are queued by the instrument and may belong to an earlier command
        // (for example, a compatibility screenshot command). Start this transaction
        // from a known clean queue so a stale -113 is not reported as a save failure.
        _ = await DrainSystemErrorsAsync(token: token);

        await transport.WriteAsync($":SAVE:WMEMory:SOURce {channel}", token);
        await EnsureNoSystemErrorAsync("设置参考波形保存来源", token);
        await transport.WriteAsync($":SAVE:WMEMory \"{normalized}\"", token);
        _ = await transport.QueryAsync("*OPC?", 30_000, token);
        await EnsureNoSystemErrorAsync("保存参考波形文件", token);
    }

    private async Task EnsureNoSystemErrorAsync(string operation, CancellationToken token)
    {
        string error = (await GetSystemErrorAsync(token)).Trim();
        if (!error.StartsWith("+0", StringComparison.Ordinal) &&
            !error.StartsWith("0,", StringComparison.Ordinal))
            throw new ScopeProtocolException($"{operation}失败：{error}");
    }

    private static void ValidateReferenceSlot(int referenceSlot)
    {
        if (referenceSlot is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(referenceSlot), "参考波形位置只能是 REF1 或 REF2。");
    }

    public Task SingleAsync(CancellationToken token = default) =>
        transport.WriteAsync(":SINGle", token);

    public async Task<string> GetTriggerStatusAsync(CancellationToken token = default) =>
        (await transport.QueryAsync(":TRIGger:STATus?", token)).Trim().ToUpperInvariant();

    public async Task<string> GetTriggerStatusAsync(
        int timeoutMilliseconds,
        CancellationToken token = default) =>
        (await transport.QueryAsync(":TRIGger:STATus?", timeoutMilliseconds, token))
            .Trim().ToUpperInvariant();

    public async Task<bool> GetTriggerEventAsync(
        int timeoutMilliseconds,
        CancellationToken token = default)
    {
        string response = (await transport.QueryAsync(":TER?", timeoutMilliseconds, token)).Trim();
        if (int.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
            value is 0 or 1)
            return value == 1;

        throw new ScopeProtocolException($"触发事件寄存器返回无效数据：'{response}'");
    }

    public async Task<(ScopeOperatingSettings Operating, string TriggerStatus)>
        GetDeviceStatusWithRecoveryAsync(
            int timeoutMilliseconds = 1200,
            CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var operating = new ScopeOperatingSettings(
                    (await transport.QueryAsync(
                        ":TIMebase:MODE?", timeoutMilliseconds, token))
                    .Trim().ToUpperInvariant(),
                    (await transport.QueryAsync(
                        ":ACQuire:TYPE?", timeoutMilliseconds, token))
                    .Trim().ToUpperInvariant());

                // DSO-X 的 :TER? 是快速事件寄存器查询。读取状态不再使用可能
                // 在等待触发期间长时间阻塞的 :TRIGger:STATus?。
                bool triggered = await GetTriggerEventAsync(timeoutMilliseconds, token);
                return (operating, triggered ? "TRIGGERED" : "WAIT");
            }
            catch (ScopeConnectionException ex) when (IsVisaTimeout(ex))
            {
                // 超时响应可能滞留在 VISA 缓冲区，必须先 Clear，之后才能安全重试。
                await transport.ClearAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested();
                if (attempt == 0)
                    await Task.Delay(80, token);
                else
                    throw new TimeoutException(
                        "读取设备状态超时；已清理 VISA 通信缓冲区，请确认示波器未被其他程序占用。",
                        ex);
            }
        }

        throw new InvalidOperationException("无法读取设备状态。");
    }

    public async Task<string> SingleAndWaitAsync(
        EdgeTriggerSettings trigger,
        ScopeOperatingSettings operating,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        bool restoreRoll = operating.TimebaseMode.Equals("ROLL", StringComparison.OrdinalIgnoreCase);
        ScopeOperatingSettings singleOperating = restoreRoll
            ? operating with { TimebaseMode = "MAIN" }
            : operating;
        try
        {
            await SetOperatingSettingsAsync(singleOperating, token);
            await SetTriggerAsync(trigger, token);
            await SingleAsync(token);
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                bool triggered;
                try
                {
                    triggered = await GetTriggerEventAsync(250, token);
                }
                catch (ScopeConnectionException ex) when (IsVisaTimeout(ex))
                {
                    // 超时的查询响应可能稍后到达并污染下一条 SCPI 响应。
                    // Device Clear 同时终止待处理 I/O 并清空 VISA 收发缓冲区。
                    await transport.ClearAsync(CancellationToken.None);
                    token.ThrowIfCancellationRequested();
                    triggered = false;
                }
                if (triggered) return "TRIGGERED";
                await Task.Delay(50, token);
            }
            throw new TimeoutException($"等待单次触发超时（{timeout.TotalSeconds:g} 秒）。");
        }
        finally
        {
            if (restoreRoll)
            {
                try { await SetTimebaseModeAsync("ROLL", CancellationToken.None); }
                catch { /* 保留原始触发/取消异常；恢复失败可由系统错误队列诊断。 */ }
            }
        }
    }

    private static bool IsVisaTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("ERROR_TMO", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("VI_ERROR_TMO", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("timeout occurred", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public Task<string> GetSystemErrorAsync(CancellationToken token = default) =>
        transport.QueryAsync(":SYSTem:ERRor?", token);

    public async Task<IReadOnlyList<string>> DrainSystemErrorsAsync(
        int maximum = 20,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        var errors = new List<string>();
        for (int index = 0; index < maximum; index++)
        {
            string error = (await GetSystemErrorAsync(token)).Trim();
            errors.Add(error);
            if (error.StartsWith("+0", StringComparison.Ordinal) ||
                error.StartsWith("0,", StringComparison.Ordinal))
                break;
        }
        return errors;
    }

    public async Task SetTimebaseModeAsync(string mode, CancellationToken token = default)
    {
        string normalized = mode.Trim().ToUpperInvariant();
        if (normalized is not ("MAIN" or "ROLL"))
            throw new ArgumentException($"不支持的时基模式：{mode}", nameof(mode));
        await transport.WriteAsync($":TIMebase:MODE {normalized}", token);
    }

    public async Task<IReadOnlyList<MeasurementResult>> FetchMeasurementsAsync(
        string channel,
        IReadOnlyList<string> names,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        string channelUnit = (await transport.QueryAsync($":{channel}:UNITs?", token))
            .Trim().Trim('"').ToUpperInvariant();
        WaveformStats? stats = null;
        Exception? statsError = null;
        var results = new List<MeasurementResult>(names.Count);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            if (!ScopeMeasurements.Definitions.TryGetValue(name, out MeasurementDefinition? definition))
                throw new ArgumentException($"不支持的测量项：{name}", nameof(names));
            string unit = channelUnit == "A" && definition.Unit == "V" ? "A" : definition.Unit;
            try
            {
                double? value;
                if (definition.QueryFormat is not null)
                {
                    value = ParseDouble(await transport.QueryAsync(
                        string.Format(CultureInfo.InvariantCulture, definition.QueryFormat, channel), token));
                }
                else
                {
                    if (stats is null && statsError is null)
                    {
                        try
                        {
                            WaveformData waveform = await FetchWaveformAsync(
                                channel, "NORMal", ScopeMeasurements.SoftwareMeasurementPoints, token);
                            stats = WaveformAnalysis.Analyze(waveform);
                        }
                        catch (Exception ex) { statsError = ex; }
                    }
                    if (statsError is not null) throw statsError;
                    value = definition.StatsGetter!(stats!);
                }
                results.Add(new(name, value, unit, ScopeMeasurements.Format(value, unit), DateTimeOffset.Now));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new(name, null, unit, "超时/无效", DateTimeOffset.Now, ex.Message));
            }
        }
        return results;
    }

    public async Task<ChannelVerticalSettings> GetChannelVerticalAsync(
        string channel,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        double scale = ParseDouble(await transport.QueryAsync($":{channel}:SCALe?", token));
        double offset = ParseDouble(await transport.QueryAsync($":{channel}:OFFSet?", token));
        string display = (await transport.QueryAsync($":{channel}:DISPlay?", token)).Trim();
        bool shown = display.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
                     ParseDouble(display) != 0;
        return new(scale, offset, shown);
    }

    public async Task SetChannelVerticalAsync(
        string channel,
        double scale,
        double offset,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        await transport.WriteAsync(
            $":{channel}:SCALe {scale.ToString("R", CultureInfo.InvariantCulture)}", token);
        await transport.WriteAsync(
            $":{channel}:OFFSet {offset.ToString("R", CultureInfo.InvariantCulture)}", token);
    }

    public async Task<ScopeOperatingSettings> GetOperatingSettingsAsync(
        CancellationToken token = default) =>
        new(
            (await transport.QueryAsync(":TIMebase:MODE?", token)).Trim().ToUpperInvariant(),
            (await transport.QueryAsync(":ACQuire:TYPE?", token)).Trim().ToUpperInvariant());

    public async Task SetOperatingSettingsAsync(
        ScopeOperatingSettings settings,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string timebase = settings.TimebaseMode.Trim().ToUpperInvariant();
        string acquire = NormalizeAcquireType(settings.AcquireType);
        if (timebase is not ("MAIN" or "WINDow" or "XY" or "ROLL"))
            throw new ArgumentException($"不支持的时基模式：{settings.TimebaseMode}", nameof(settings));
        await transport.WriteAsync($":TIMebase:MODE {timebase}", token);
        await transport.WriteAsync($":ACQuire:TYPE {acquire}", token);
    }

    public async Task CaptureScreenshotAsync(string targetPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string fullPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await transport.WriteAsync(":HARDcopy:INKSaver OFF", token);
            byte[] png = await QueryScreenshotPngAsync(token);
            await File.WriteAllBytesAsync(temporary, png, token);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<byte[]> QueryScreenshotPngAsync(CancellationToken token)
    {
        string[] commands =
        [
            ":DISPlay:DATA? PNG, COLor",
            ":DISPlay:DATA? PNG",
            ":DISPlay:DATA? PNG, SCReen, COLor"
        ];
        var failures = new List<string>();
        foreach (string command in commands)
        {
            try
            {
                byte[] response = await transport.QueryBinaryAsync(command, 30_000, token);
                if (ExtractPng(response) is { } png) return png;
                string prefix = Convert.ToHexString(response.AsSpan(0, Math.Min(12, response.Length)));
                failures.Add($"{command} 返回 {response.Length} 字节，前缀 {prefix}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add($"{command}：{ex.Message}");
            }
        }
        throw new WaveformIntegrityException(
            "示波器返回的截图不是有效 PNG 数据。已尝试兼容命令；" + string.Join("；", failures));
    }

    private static byte[]? ExtractPng(byte[] response)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (response.Length < signature.Length) return null;
        int start = response.AsSpan().IndexOf(signature);
        return start < 0 ? null : response[start..];
    }

    public async Task<EdgeTriggerSettings> GetTriggerAsync(CancellationToken token = default) =>
        new(
            (await transport.QueryAsync(":TRIGger:EDGE:SOURce?", token)).Trim(),
            (await transport.QueryAsync(":TRIGger:EDGE:SLOPe?", token)).Trim(),
            ParseDouble(await transport.QueryAsync(":TRIGger:EDGE:LEVel?", token)),
            (await transport.QueryAsync(":TRIGger:SWEep?", token)).Trim());

    public async Task SetTriggerAsync(EdgeTriggerSettings settings, CancellationToken token = default)
    {
        ValidateChannel(settings.Source);
        await transport.WriteAsync(":TRIGger:MODE EDGE", token);
        await transport.WriteAsync($":TRIGger:EDGE:SOURce {settings.Source}", token);
        await transport.WriteAsync($":TRIGger:EDGE:SLOPe {settings.Slope}", token);
        await transport.WriteAsync($":TRIGger:EDGE:LEVel {settings.Level.ToString("R", CultureInfo.InvariantCulture)}", token);
        await transport.WriteAsync($":TRIGger:SWEep {settings.Sweep}", token);
    }

    public async Task<CaptureResult> CaptureAsync(
        CaptureRequest request,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (request.Channels.Count == 0) throw new ArgumentException("至少选择一个抓波通道。", nameof(request));
        if (!new[] { "NORMal", "MAXimum", "RAW" }.Contains(request.PointsMode, StringComparer.Ordinal))
            throw new ArgumentException($"不支持的波形点模式：{request.PointsMode}", nameof(request));
        string acquireType = NormalizeAcquireType(request.AcquireType);
        await transport.WriteAsync($":ACQuire:TYPE {acquireType}", token);
        Stopwatch timer = Stopwatch.StartNew();
        var waveforms = new List<WaveformData>();
        var warnings = new List<string>();
        for (int i = 0; i < request.Channels.Count; i++)
        {
            string channel = request.Channels[i];
            ValidateChannel(channel);
            bool autoDetectRecordLength =
                request.FullDeepMemory || request.PointsMode is "MAXimum" or "RAW";
            if (autoDetectRecordLength)
            {
                var channelProgress = new InlineProgress(value =>
                    progress?.Report((i + value) / request.Channels.Count));
                waveforms.Add(await FetchWaveformChunkedAsync(
                    channel,
                    request.PointsMode,
                    ChunkedReadThreshold,
                    null,
                    channelProgress,
                    warnings,
                    token));
            }
            else
            {
                waveforms.Add(await FetchWaveformAsync(
                    channel, request.PointsMode, request.Points, token));
            }
            progress?.Report((double)(i + 1) / request.Channels.Count);
        }
        return new(request, new WaveformBundle(waveforms), timer.Elapsed, warnings);
    }

    public async Task<WaveformData> FetchWaveformAsync(
        string channel, string pointsMode, int points, CancellationToken token = default)
    {
        ValidateChannel(channel);
        (string unit, ChannelAcquisitionMetadata metadata) =
            await ReadChannelMetadataAsync(channel, token);
        await transport.WriteAsync($":WAVeform:SOURce {channel}", token);
        await transport.WriteAsync($":WAVeform:POINts:MODE {pointsMode}", token);
        await transport.WriteAsync($":WAVeform:POINts {points}", token);
        await transport.WriteAsync(":WAVeform:FORMat BYTE", token);
        await transport.WriteAsync(":WAVeform:UNSigned ON", token);
        double[] preamble = ParseCsv(await transport.QueryAsync(":WAVeform:PREamble?", token));
        if (preamble.Length < 10) throw new WaveformIntegrityException($"{channel} 的波形前导字段不足。");
        byte[] payload = await QueryBinaryAndClearAsync(
            ":WAVeform:DATA?", 60_000, token);
        if (payload.Length == 0) throw new WaveformIntegrityException($"{channel} 返回空波形。");
        int expected = (int)preamble[2];
        if (expected > 0 && payload.Length < expected)
            throw new WaveformIntegrityException($"{channel} 波形被截断：期望 {expected} 点，实际 {payload.Length} 点。");
        int count = expected > 0 ? Math.Min(expected, payload.Length) : payload.Length;
        var x = new double[count];
        var y = new double[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = (i - preamble[6]) * preamble[4] + preamble[5];
            y[i] = (payload[i] - preamble[9]) * preamble[7] + preamble[8];
        }
        var model = new WaveformPreamble((int)preamble[0], (int)preamble[1], count, (int)preamble[3],
            preamble[4], preamble[5], preamble[6], preamble[7], preamble[8], preamble[9]);
        return new(channel, x, y, pointsMode, unit, model, metadata);
    }

    public async Task<WaveformData> FetchWaveformChunkedAsync(
        string channel,
        string pointsMode = "RAW",
        int chunkPoints = 500_000,
        int? totalPoints = null,
        IProgress<double>? progress = null,
        ICollection<string>? warnings = null,
        CancellationToken token = default)
    {
        ValidateChannel(channel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkPoints);
        if (!new[] { "NORMal", "MAXimum", "RAW" }.Contains(pointsMode, StringComparer.Ordinal))
            throw new ArgumentException($"不支持的波形点模式：{pointsMode}", nameof(pointsMode));

        (string unit, ChannelAcquisitionMetadata metadata) =
            await ReadChannelMetadataAsync(channel, token);

        await transport.WriteAsync($":WAVeform:SOURce {channel}", token);
        await transport.WriteAsync(":WAVeform:FORMat BYTE", token);
        await transport.WriteAsync(":WAVeform:UNSigned ON", token);
        await transport.WriteAsync($":WAVeform:POINts:MODE {pointsMode}", token);
        if (totalPoints is null)
        {
            await transport.WriteAsync(":WAVeform:POINts 100000000", token);
            totalPoints = (int)ParseDouble(await transport.QueryAsync(":WAVeform:POINts?", token));
        }
        if (totalPoints <= 0)
            throw new WaveformIntegrityException("示波器返回的深存储点数无效。");

        await transport.WriteAsync($":WAVeform:POINts {totalPoints}", token);
        await transport.WriteAsync(":WAVeform:STARt 1", token);
        await transport.WriteAsync($":WAVeform:STOP {totalPoints}", token);
        double[] preamble = ParseCsv(await transport.QueryAsync(":WAVeform:PREamble?", token));
        if (preamble.Length < 10)
            throw new WaveformIntegrityException($"{channel} 的波形前导字段不足。");

        int recordPoints = Math.Min(totalPoints.Value, (int)preamble[2]);
        var payload = new byte[recordPoints];
        int copied = 0;
        int effectiveChunkPoints = chunkPoints;
        int? firstRequestedChunk = null;
        int? firstReturnedChunk = null;
        for (int start = 1; start <= recordPoints;)
        {
            token.ThrowIfCancellationRequested();
            int stop = Math.Min(start + effectiveChunkPoints - 1, recordPoints);
            await transport.WriteAsync($":WAVeform:STARt {start}", token);
            await transport.WriteAsync($":WAVeform:STOP {stop}", token);
            byte[] chunk = await QueryBinaryAndClearAsync(
                ":WAVeform:DATA?", 60_000, token);
            int expected = stop - start + 1;
            if (chunk.Length == 0)
            {
                if (copied == 0)
                    throw new WaveformIntegrityException(
                        $"深存储未返回任何波形数据：{start}~{stop} 请求 {expected} 点。");
                warnings?.Add(
                    $"{channel}：设备报告 {recordPoints:N0} 点，实际返回 {copied:N0} 点；已保留实际数据。");
                break;
            }
            if (chunk.Length != expected && firstReturnedChunk is null)
            {
                firstRequestedChunk = expected;
                firstReturnedChunk = chunk.Length;
            }
            int accepted = Math.Min(chunk.Length, recordPoints - copied);
            Array.Copy(chunk, 0, payload, copied, accepted);
            copied += accepted;
            // InfiniiVision 系列可能将单次二进制传输限制为低于请求范围的点数。
            // 接受已经连续返回的数据，并以设备实际能力调整后续分块，避免跳过数据。
            if (chunk.Length < expected)
                effectiveChunkPoints = Math.Min(effectiveChunkPoints, chunk.Length);
            progress?.Report((double)copied / recordPoints);
            start += accepted;
            if (accepted < chunk.Length) break;
        }

        if (firstReturnedChunk is not null)
            warnings?.Add(
                $"{channel}：单次请求 {firstRequestedChunk!.Value:N0} 点，设备实际返回 " +
                $"{firstReturnedChunk.Value:N0} 点；已按实际返回连续读取，共保存 {copied:N0} 点。");
        if (copied < recordPoints && (warnings is null ||
            !warnings.Any(item => item.Contains($"实际返回 {copied:N0} 点", StringComparison.Ordinal))))
            warnings?.Add(
                $"{channel}：设备报告 {recordPoints:N0} 点，实际返回 {copied:N0} 点；已保留实际数据。");

        if (copied != payload.Length) Array.Resize(ref payload, copied);

        var x = new double[copied];
        var y = new double[copied];
        for (int i = 0; i < copied; i++)
        {
            x[i] = (i - preamble[6]) * preamble[4] + preamble[5];
            y[i] = (payload[i] - preamble[9]) * preamble[7] + preamble[8];
        }
        var model = new WaveformPreamble((int)preamble[0], (int)preamble[1], copied, (int)preamble[3],
            preamble[4], preamble[5], preamble[6], preamble[7], preamble[8], preamble[9]);
        return new(channel, x, y, pointsMode, unit, model, metadata);
    }

    private async Task<(string Unit, ChannelAcquisitionMetadata Metadata)> ReadChannelMetadataAsync(
        string channel,
        CancellationToken token)
    {
        async Task<string?> OptionalQuery(string command)
        {
            try
            {
                string value = (await transport.QueryAsync(command, token)).Trim().Trim('"');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        static double? Number(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) ? parsed : null;
        static bool? Boolean(string? value) => value?.Trim().ToUpperInvariant() switch
        {
            "1" or "ON" => true,
            "0" or "OFF" => false,
            _ => null
        };

        string? unit = await OptionalQuery($":{channel}:UNITs?");
        string? probe = await OptionalQuery($":{channel}:PROBe?");
        string? probeId = await OptionalQuery($":{channel}:PROBe:ID?");
        string? probeType = await OptionalQuery($":{channel}:PROBe:HEAD:TYPE?");
        string? scale = await OptionalQuery($":{channel}:SCALe?");
        string? offset = await OptionalQuery($":{channel}:OFFSet?");
        string? coupling = await OptionalQuery($":{channel}:COUPling?");
        string? impedance = await OptionalQuery($":{channel}:IMPedance?");
        string? bandwidth = await OptionalQuery($":{channel}:BWLimit?");
        string? inverted = await OptionalQuery($":{channel}:INVert?");
        string? displayed = await OptionalQuery($":{channel}:DISPlay?");
        string? label = await OptionalQuery($":{channel}:LABel?");
        return (
            unit?.ToUpperInvariant() ?? "V",
            new(
                Number(probe), probeId, probeType, Number(scale), Number(offset),
                coupling, impedance, bandwidth, Boolean(inverted), Boolean(displayed), label));
    }

    private async Task<byte[]> QueryBinaryAndClearAsync(
        string command,
        int timeoutMilliseconds,
        CancellationToken token)
    {
        try
        {
            return await transport.QueryBinaryAsync(command, timeoutMilliseconds, token);
        }
        finally
        {
            // Keysight FormattedIO 的 IEEE 块读取可能留下行结束符。
            // 在下一条 ASCII 查询之前清理收发缓冲区，保证连续抓波不会读到空响应。
            try { await transport.ClearAsync(CancellationToken.None); }
            catch when (token.IsCancellationRequested) { }
        }
    }

    private static double[] ParseCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ScopeProtocolException("示波器返回了空的波形参数；VISA 响应流可能需要清理。");
        return value.Split(',').Select(item => ParseDouble(item.Trim())).ToArray();
    }

    private static double ParseDouble(string value)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double result))
            throw new ScopeProtocolException($"示波器返回的数值格式无效：'{value.Trim()}'");
        return result;
    }
    private static string NormalizeAcquireType(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NORMAL" or "NORM" => "NORMal",
            "AVERAGE" or "AVER" => "AVERage",
            "HRESOLUTION" or "HRES" => "HRESolution",
            "PEAK" or "PEAKDETECT" => "PEAK",
            _ => throw new ArgumentException($"不支持的采集类型：{value}", nameof(value))
        };
    }
    private static void ValidateChannel(string channel)
    {
        if (!ScopeChannels.IsValid(channel)) throw new ArgumentException($"不支持的通道：{channel}", nameof(channel));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
