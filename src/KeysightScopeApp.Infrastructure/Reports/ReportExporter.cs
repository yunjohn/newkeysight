using System.Globalization;
using System.Net;
using System.Text;
using KeysightScopeApp.Core.Validation;

namespace KeysightScopeApp.Infrastructure.Reports;

public sealed class ReportExporter
{
    public async Task<string> ExportHtmlAsync(TestRun run, string targetPath, CancellationToken token = default)
    {
        var rows = new StringBuilder();
        foreach (MetricResult metric in run.Metrics)
            rows.Append("<tr><td>").Append(E(metric.Name)).Append("</td><td>").Append(metric.Status)
                .Append("</td><td>").Append(metric.Value?.ToString("g", CultureInfo.InvariantCulture) ?? "")
                .Append("</td><td>").Append(E(metric.Unit)).Append("</td><td>").Append(E(metric.Reason)).AppendLine("</td></tr>");
        string screenshot = string.IsNullOrWhiteSpace(run.ScreenshotPath) ? ""
            : $"<p><img alt=\"关键波形\" src=\"{E(run.ScreenshotPath)}\"></p>";
        string html = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>测试报告 " +
            E(run.SampleId) + "</title><style>body{font-family:sans-serif;max-width:1000px;margin:2rem auto}" +
            "table{border-collapse:collapse;width:100%}td,th{border:1px solid #bbb;padding:.5rem}</style></head><body>" +
            "<h1>Keysight 示波器测试报告</h1><p>样品：" + E(run.SampleId) + "　结论：<strong>" +
            run.Status + "</strong></p><p>设备：" + E(run.InstrumentId) + "　方案：" + E(run.ProfileName) +
            " " + E(run.ProfileVersion) + "</p><p>运行 ID：" + E(run.EffectiveRunId) + "　时间：" +
            run.EffectiveGeneratedAt.ToString("O") + "　软件：" + E(run.AppVersion) + "</p><p>原始波形：" +
            E(run.WaveformPath ?? "未记录") + "</p><table><thead><tr><th>指标</th><th>判定</th>" +
            "<th>实测值</th><th>单位</th><th>原因</th></tr></thead><tbody>" + rows +
            "</tbody></table>" + screenshot + "</body></html>";
        await AtomicWriteAsync(targetPath, html, new UTF8Encoding(false), token);
        return targetPath;
    }

    public async Task<string> ExportCsvAsync(IEnumerable<TestRun> runs, string targetPath, CancellationToken token = default)
    {
        var content = new StringBuilder();
        content.AppendLine("run_id,sample_id,status,profile,profile_version,instrument_id,generated_at,metric,metric_status,value,unit,reason,waveform_path");
        foreach (TestRun run in runs)
            foreach (MetricResult? metric in run.Metrics.Cast<MetricResult?>().DefaultIfEmpty())
            {
                token.ThrowIfCancellationRequested();
                string[] fields = [run.EffectiveRunId, run.SampleId, run.Status.ToString(), run.ProfileName, run.ProfileVersion,
                run.InstrumentId, run.EffectiveGeneratedAt.ToString("O"), metric?.Name ?? "", metric?.Status.ToString() ?? "",
                metric?.Value?.ToString("g", CultureInfo.InvariantCulture) ?? "", metric?.Unit ?? "", metric?.Reason ?? "",
                run.WaveformPath ?? ""];
                content.AppendLine(string.Join(',', fields.Select(Csv)));
            }
        await AtomicWriteAsync(targetPath, content.ToString(), new UTF8Encoding(true), token);
        return targetPath;
    }

    public async Task<string> ExportHistoryHtmlAsync(
        IEnumerable<TestRun> runs,
        string targetPath,
        CancellationToken token = default)
    {
        TestRun[] allRuns = runs.OrderBy(item => item.EffectiveGeneratedAt).ToArray();
        TestRun[] startupRuns = allRuns.Where(item => item.ProfileName.Equals("启动刹车", StringComparison.Ordinal)).ToArray();
        StartupBrakeRunMetadata? config = startupRuns.LastOrDefault(item => item.StartupBrake is not null)?.StartupBrake;
        static double? M(TestRun run, string name) =>
            run.Metrics.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal))?.Value;
        static string N(double? value, string format = "0.######") =>
            value?.ToString(format, CultureInfo.InvariantCulture) ?? "-";
        static string Range(IEnumerable<double?> values, string unit)
        {
            double[] valid = values.Where(item => item is not null && double.IsFinite(item.Value))
                .Select(item => item!.Value).ToArray();
            return valid.Length == 0 ? "-" :
                $"{valid.Min().ToString("0.###", CultureInfo.InvariantCulture)} ～ {valid.Max().ToString("0.###", CultureInfo.InvariantCulture)} {unit}";
        }
        static string TargetMode(string? value) => value switch
        {
            "Rpm" => "转速 (RPM)", "PeriodSeconds" => "周期 (s)", "FrequencyHz" => "频率 (Hz)", _ => value ?? "未记录"
        };
        static string TestMode(string? value) => value switch
        {
            "Full" => "完整流程", "StartupOnly" => "仅启动", "BrakeOnly" => "仅刹车", _ => value ?? "未记录"
        };
        static string BrakeMode(string? value) => value switch
        {
            "CurrentZero" => "电流归零", "SpeedZero" => "速度归零", "EncoderBacktrack" => "编码器回溯", _ => value ?? "未记录"
        };
        string C(string label, string value) => $"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>";

        var configurationItems = new List<(string Label, string Value)>();
        if (config is null)
            configurationItems.Add(("配置状态", "旧历史记录未保存启动/刹车配置"));
        else
        {
            configurationItems.AddRange([
                ("控制输入通道", config.ControlChannel), ("转速反馈通道", config.SpeedChannel),
                ("电流通道", config.CurrentChannel), ("编码器 A 相通道", config.EncoderAChannel ?? "-"),
                ("达速目标类型", TargetMode(config.TargetMode)), ("达速目标值", N(config.TargetValue)),
                ("达速下偏差 (%)", N(config.LowerTolerancePercent)), ("达速上偏差 (%)", N(config.UpperTolerancePercent)),
                ("连续周期数", config.ConsecutivePeriods.ToString(CultureInfo.InvariantCulture)),
                ("每转脉冲数", config.PulsesPerRevolution.ToString(CultureInfo.InvariantCulture)),
                ("测试模式", TestMode(config.TestMode)), ("刹车模式", BrakeMode(config.BrakeMode)),
                ("启动最小跳变 (V)", N(config.StartupMinimumVoltageStep)),
                ("高电平保持时间 (ms)", N(config.StartupHoldMilliseconds)),
                ("最小上升时间 (ms)", N(config.StartupMinimumRiseMilliseconds)),
                ("最大上升时间 (ms)", N(config.StartupMaximumRiseMilliseconds)),
                ("零电流阈值 (A)", N(config.ZeroCurrentThreshold)),
                ("零电流波动阈值 (A)", N(config.ZeroCurrentFlatThreshold)),
                ("零电流保持时间 (ms)", N(config.ZeroCurrentHoldMilliseconds)),
                ("低电平保持时间 (ms)", N(config.BrakeLowHoldMilliseconds)),
                ("最小下降时间 (ms)", N(config.BrakeMinimumFallMilliseconds)),
                ("最大下降时间 (ms)", N(config.BrakeMaximumFallMilliseconds)),
                ("回溯脉冲数", config.BrakeBacktrackPulses.ToString(CultureInfo.InvariantCulture))
            ]);
        }
        var configuration = new StringBuilder();
        for (int index = 0; index < configurationItems.Count; index += 2)
        {
            (string label, string value) = configurationItems[index];
            configuration.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td>");
            if (index + 1 < configurationItems.Count)
            {
                (string secondLabel, string secondValue) = configurationItems[index + 1];
                configuration.Append("<th>").Append(E(secondLabel)).Append("</th><td>").Append(E(secondValue)).Append("</td>");
            }
            else configuration.Append("<th class=\"empty\"></th><td class=\"empty\"></td>");
            configuration.AppendLine("</tr>");
        }

        var summary = new StringBuilder()
            .Append(C("样本数", startupRuns.Length.ToString(CultureInfo.InvariantCulture)))
            .Append(C("启动时长范围", Range(startupRuns.Select(item => item.StartupDelayMilliseconds), "ms")))
            .Append(C("刹车时长范围", Range(startupRuns.Select(item => item.BrakeDelayMilliseconds), "ms")))
            .Append(C("启动峰值电流范围", Range(startupRuns.Select(item => item.StartupPeak), "A")))
            .Append(C("刹车峰值电流范围", Range(startupRuns.Select(item => item.BrakePeak), "A")))
            .Append(C("稳定转速峰峰值范围", Range(startupRuns.Select(item => M(item, "稳定转速峰峰值")), "RPM")))
            .Append(C("稳定转速波动范围", Range(startupRuns.Select(item => item.StableFluctuationPercent), "%")));

        var records = new StringBuilder();
        for (int index = 0; index < startupRuns.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            TestRun run = startupRuns[index];
            StartupBrakeRunMetadata? metadata = run.StartupBrake;
            double? averageRpm = run.StableAverageRpm;
            double? hitFrequency = averageRpm is null || metadata is null ? null : averageRpm * Math.Max(1, metadata.PulsesPerRevolution) / 60;
            double? hitPeriodMs = hitFrequency is > 0 ? 1000 / hitFrequency : null;
            string note = run.Metrics.FirstOrDefault(item => item.Name.Equals("刹车完成点", StringComparison.Ordinal))?.Reason ?? "";
            string[] fields = [
                (index + 1).ToString(CultureInfo.InvariantCulture), run.EffectiveGeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                N(M(run, "启动点")), N(M(run, "达速点")), N(run.StartupDelayMilliseconds, "0.###"),
                N(M(run, "刹车点")), N(M(run, "零电流确认点")), N(M(run, "刹车完成点")), N(run.BrakeDelayMilliseconds, "0.###"),
                N(run.StartupPeak), N(run.BrakePeak), N(hitFrequency), N(hitPeriodMs), N(averageRpm),
                N(M(run, "稳定最小转速")), N(M(run, "稳定最大转速")), N(M(run, "稳定转速峰峰值")),
                N(run.StableFluctuationPercent), N(M(run, "稳定完整周期"), "0"), TargetMode(metadata?.TargetMode),
                metadata is null ? "未记录" : N(metadata.TargetValue), metadata?.PulsesPerRevolution.ToString(CultureInfo.InvariantCulture) ?? "未记录",
                BrakeMode(metadata?.BrakeMode), TestMode(metadata?.TestMode), note];
            records.Append("<tr>");
            foreach (string field in fields) records.Append("<td>").Append(E(field)).Append("</td>");
            records.AppendLine("</tr>");
        }
        string[] headers = ["序号", "时间", "启动起点 (s)", "达速时刻 (s)", "启动时长 (ms)", "刹车起点 (s)",
            "零电流确认 (s)", "刹车终点 (s)", "刹车时长 (ms)", "启动峰值电流 (A)", "刹车峰值电流 (A)",
            "命中频率 (Hz)", "命中周期 (ms)", "稳定转速平均值 (RPM)", "稳定最小转速 (RPM)", "稳定最大转速 (RPM)",
            "稳定转速峰峰值 (RPM)", "稳定转速波动 (%)", "稳定转速有效周期数", "达速目标类型", "达速目标值",
            "每转脉冲数", "刹车模式", "测试模式", "终点可信度说明"];
        string headerHtml = string.Concat(headers.Select(item => $"<th>{E(item)}</th>"));
        string html = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>启动刹车历史性能报告</title><style>" +
            "body{font-family:'Microsoft YaHei',sans-serif;margin:24px;color:#1f2937;background:#f8fafc}h1{margin-bottom:4px}" +
            ".meta{color:#64748b;margin-bottom:20px}.cards{display:grid;grid-template-columns:repeat(2,minmax(320px,1fr));gap:16px}" +
            ".card{background:white;border:1px solid #dbe2ea;border-radius:8px;padding:16px}.scroll{overflow-x:auto;background:white;border:1px solid #dbe2ea;border-radius:8px}" +
            "table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #e5e7eb;padding:8px 10px;text-align:left;white-space:nowrap}" +
            "th{background:#eef2f7;font-weight:600}.config th{width:18%}.config td{width:32%}.empty{background:white}" +
            ".kv th{width:220px}@media(max-width:800px){.cards{grid-template-columns:1fr}.config th,.config td{width:auto}}" +
            "</style></head><body><h1>启动/刹车历史性能报告</h1><div class=\"meta\">导出时间：" +
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "　记录数：" + startupRuns.Length.ToString(CultureInfo.InvariantCulture) +
            "</div><div class=\"cards\"><section class=\"card\"><h2>测试配置</h2><table class=\"config\">" + configuration +
            "</table></section><section class=\"card\"><h2>汇总统计</h2><table class=\"kv\">" + summary +
            "</table></section></div><h2>测试记录</h2><div class=\"scroll\"><table><thead><tr>" + headerHtml +
            "</tr></thead><tbody>" + records + "</tbody></table></div></body></html>";
        await AtomicWriteAsync(targetPath, html, new UTF8Encoding(false), token);
        return targetPath;
    }

    private static async Task AtomicWriteAsync(string target, string content, Encoding encoding, CancellationToken token)
    {
        string full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, content, encoding, token); File.Move(temporary, full, true); }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
