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
