using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KeysightScopeApp.Core.AI;

namespace KeysightScopeApp.Infrastructure.AI;

public sealed class AiAssistantException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class OpenAiCompatibleAssistantService(HttpClient httpClient) : IAiAssistantService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AiConfigurationRecommendation> RecommendAsync(
        AiAssistantRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        Uri endpoint = BuildEndpoint(request.Endpoint);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        message.Content = JsonContent.Create(new
        {
            model = request.Model.Trim(),
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(request.Context, JsonOptions) }
            }
        }, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAssistantException($"AI 请求超时（{request.Timeout.TotalSeconds:g0} 秒）。");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAssistantException($"无法连接 AI 服务：{ex.Message}", ex);
        }
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "AI 服务请求过于频繁，请稍后重试。"
                    : $"AI 服务返回 {(int)response.StatusCode} {response.ReasonPhrase}。";
                throw new AiAssistantException(reason);
            }
            try
            {
                using JsonDocument envelope = JsonDocument.Parse(body);
                string content = ExtractAssistantContent(envelope.RootElement);
                string recommendationJson = ExtractJsonObject(content);
                AiConfigurationRecommendation recommendation = ParseRecommendation(recommendationJson);
                return ValidateRecommendation(recommendation);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AiAssistantException("AI 返回内容不是有效的结构化配置建议。", ex);
            }
        }
    }

    private static AiConfigurationRecommendation ParseRecommendation(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string summary = RequiredText(root, "summary");
        JsonElement changesElement = Required(root, "changes");
        if (changesElement.ValueKind != JsonValueKind.Array) throw new JsonException("changes 必须是数组");
        var changes = new List<AiConfigurationChange>();
        foreach (JsonElement item in changesElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string recommendation = item.GetString() ?? "未知";
                changes.Add(new("配置建议", "未知", recommendation, "AI 返回的通用建议",
                    "请按建议内容人工验证", "应用前确认设备量程与实验安全"));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException("changes 项类型无效");
            changes.Add(new(
                Text(item, "setting", "配置建议"), Text(item, "currentValue", "未知"),
                RequiredText(item, "recommendedValue"), RequiredText(item, "reason"),
                Text(item, "expectedEffect", "未知"), Text(item, "risk", "应用前人工确认")));
        }
        string originalVerdict = Text(root, "assistantVerdict", "INCONCLUSIVE").Trim();
        string verdict = originalVerdict.ToUpperInvariant() switch
        {
            "REASONABLE" or "合理" => "REASONABLE",
            "SUSPICIOUS" or "可疑" => "SUSPICIOUS",
            "UNREASONABLE" or "不合理" => "UNREASONABLE",
            "INCONCLUSIVE" or "无法判定" => "INCONCLUSIVE",
            _ => "INCONCLUSIVE"
        };
        string confidence = ReadConfidence(root);
        if (!originalVerdict.Equals(verdict, StringComparison.OrdinalIgnoreCase))
            confidence = $"{confidence}；模型原始意见：{originalVerdict}";
        return new(summary, changes, StringArray(root, "manualSteps"),
            StringArray(root, "verificationSteps"), verdict, confidence,
            Text(root, "missingInformation", "未知"),
            Text(root, "waveformAssessment", "缺少波形合理性结论"),
            ReadFindings(root), ReadCauses(root));
    }

    private static List<AiWaveformFinding> ReadFindings(JsonElement root)
    {
        if (!TryProperty(root, "findings", out JsonElement value) || value.ValueKind != JsonValueKind.Array) return [];
        var result = new List<AiWaveformFinding>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                result.Add(new("未知", "未知", item.GetString() ?? "未知", "AI 未提供量化证据", "未知"));
            else if (item.ValueKind == JsonValueKind.Object)
                result.Add(new(Text(item, "channel", "未知"), Text(item, "timeRange", "未知"),
                    Text(item, "phenomenon", "未知"), Text(item, "evidence", "未知"), Text(item, "severity", "未知")));
        }
        return result;
    }

    private static List<AiPossibleCause> ReadCauses(JsonElement root)
    {
        if (!TryProperty(root, "possibleCauses", out JsonElement value) || value.ValueKind != JsonValueKind.Array) return [];
        var result = new List<AiPossibleCause>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                result.Add(new(item.GetString() ?? "未知", "AI 未提供证据", "未知", "未知", "需要实验验证"));
            else if (item.ValueKind == JsonValueKind.Object)
                result.Add(new(Text(item, "cause", "未知"), Text(item, "supportingEvidence", "未知"),
                    Text(item, "contradictingEvidence", "未知"), Text(item, "likelihood", "未知"),
                    Text(item, "verificationMethod", "未知"), Text(item, "category", "未知")));
        }
        return result;
    }

    private static string ReadConfidence(JsonElement root)
    {
        if (!TryProperty(root, "confidence", out JsonElement value)) return "未提供";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "未提供";
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            double percent = number is >= 0 and <= 1 ? number * 100 : number;
            return FormattableString.Invariant($"{percent:G3}%");
        }
        return "未提供";
    }

    private static string[] StringArray(JsonElement root, string name)
    {
        JsonElement value = Required(root, name);
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException($"{name} 必须是数组");
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString() : item.GetRawText()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray();
    }

    private static JsonElement Required(JsonElement root, string name) =>
        TryProperty(root, name, out JsonElement value) ? value : throw new JsonException($"缺少 {name}");
    private static string RequiredText(JsonElement root, string name)
    {
        string value = Text(root, name, "");
        return string.IsNullOrWhiteSpace(value) ? throw new JsonException($"缺少 {name}") : value;
    }
    private static string Text(JsonElement root, string name, string fallback)
    {
        if (!TryProperty(root, name, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.GetRawText();
    }
    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string ExtractAssistantContent(JsonElement root)
    {
        if (root.TryGetProperty("summary", out _)) return root.GetRawText();
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
            throw new JsonException("缺少 choices[0].message.content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? throw new JsonException("响应内容为空");
        if (content.ValueKind == JsonValueKind.Object)
            return content.GetRawText();
        if (content.ValueKind == JsonValueKind.Array)
        {
            string combined = string.Join("\n", content.EnumerateArray().Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out JsonElement text))
                    return text.GetString();
                return null;
            }).Where(item => !string.IsNullOrWhiteSpace(item)));
            return string.IsNullOrWhiteSpace(combined)
                ? throw new JsonException("响应内容数组为空") : combined;
        }
        throw new JsonException("响应内容类型不受支持");
    }

    private static string ExtractJsonObject(string content)
    {
        string value = content.Trim();
        int fenced = value.IndexOf("```", StringComparison.Ordinal);
        if (fenced >= 0)
        {
            int lineEnd = value.IndexOf('\n', fenced + 3);
            int fenceEnd = value.LastIndexOf("```", StringComparison.Ordinal);
            if (lineEnd >= 0 && fenceEnd > lineEnd)
                value = value[(lineEnd + 1)..fenceEnd].Trim();
        }
        int start = value.IndexOf('{');
        if (start < 0) throw new JsonException("未找到 JSON 对象");
        bool inString = false;
        bool escaped = false;
        int depth = 0;
        for (int index = start; index < value.Length; index++)
        {
            char character = value[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return value[start..(index + 1)];
        }
        throw new JsonException("JSON 对象不完整");
    }

    private static readonly string SystemPrompt = """
        你是示波器实验配置顾问。只能提出人工操作建议，绝不能声称已经修改设备。
        所有当前值必须来自输入；缺失值写“未知”，不得推测设备能力。
        你的首要任务是分析波形是否符合用户描述的工况，并解释形成该波形的可能原因；配置建议是次要任务。
        输入中的 measurementScene 描述被测对象、总体测量位置和当前工况，channelSignals 定义每个示波器通道实际接入的信号；分析必须使用这些映射，不能把通道物理含义互换。
        briefTestDescription 是可选的简要测试描述；waveformScope 和 selectedTimeRange 说明发送的是完整波形还是用户当前视窗。只能分析输入中实际包含的通道和时间范围。
        必须区分“波形中直接观察到的事实”和“需要实验验证的成因推断”。没有预期行为或工况时不得武断判定合理。
        只返回 JSON，字段必须为 summary、waveformAssessment、findings、possibleCauses、changes、manualSteps、verificationSteps、assistantVerdict、confidence、missingInformation、schemaVersion。
        findings 必须是对象数组，每项含 channel、timeRange、phenomenon、evidence、severity；证据必须引用输入中的时间、幅值、统计量或通道信息。
        possibleCauses 必须是对象数组，每项含 cause、category、supportingEvidence、contradictingEvidence、likelihood、verificationMethod；category 只能是“电路现象”“测量问题”“采集配置”或“未知”，禁止把推测写成事实。
        changes 必须是对象数组，每项字段必须为 setting、currentValue、recommendedValue、reason、expectedEffect、risk，禁止返回字符串数组。
        assistantVerdict 只能返回 REASONABLE、SUSPICIOUS、UNREASONABLE 或 INCONCLUSIVE，分别表示合理、可疑、不合理、无法判定；禁止返回 PASS/FAIL 或解释句。confidence 必须返回中文字符串，禁止返回数字。
        建议应优先保证触发可靠、采样充分、避免削顶并明确探头倍率影响。
        """;

    private static Uri BuildEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
            throw new AiAssistantException("AI 接口地址无效，只支持 http 或 https。");
        string text = baseUri.ToString().TrimEnd('/');
        if (!text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            text += text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? "/chat/completions" : "/v1/chat/completions";
        return new(text);
    }

    private static void ValidateRequest(AiAssistantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Endpoint)) throw new AiAssistantException("请填写 AI 接口地址。");
        if (string.IsNullOrWhiteSpace(request.Model)) throw new AiAssistantException("请填写模型名称。");
        if (string.IsNullOrWhiteSpace(request.Context.Goal)) throw new AiAssistantException("请填写实验目标。");
        if (request.Timeout < TimeSpan.FromSeconds(5) || request.Timeout > TimeSpan.FromMinutes(10))
            throw new AiAssistantException("AI 超时时间必须在 5–600 秒之间。");
    }

    private static AiConfigurationRecommendation ValidateRecommendation(AiConfigurationRecommendation value)
    {
        if (value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.Summary) ||
            value.Changes is null || value.ManualSteps is null || value.VerificationSteps is null ||
            string.IsNullOrWhiteSpace(value.AssistantVerdict) || string.IsNullOrWhiteSpace(value.Confidence))
            throw new JsonException("缺少必需字段");
        string verdict = value.AssistantVerdict.Trim().ToUpperInvariant();
        if (verdict is not ("REASONABLE" or "SUSPICIOUS" or "UNREASONABLE" or "INCONCLUSIVE"))
            throw new JsonException("assistantVerdict 无效");
        if (value.Changes.Any(item => string.IsNullOrWhiteSpace(item.Setting) ||
                                      string.IsNullOrWhiteSpace(item.RecommendedValue) ||
                                      string.IsNullOrWhiteSpace(item.Reason)))
            throw new JsonException("配置建议字段不完整");
        return value with { AssistantVerdict = verdict };
    }
}
