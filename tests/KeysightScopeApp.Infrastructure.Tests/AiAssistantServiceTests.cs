using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class AiAssistantServiceTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] LooseChanges = ["设置时基为100ms/div", "使用上升沿触发"];
    private static readonly string[] LooseManualSteps = ["人工调整旋钮"];
    private static readonly string[] LooseVerificationSteps = ["重新采集"];
    [Fact]
    public async Task ParsesStructuredRecommendationAndUsesBearerAuthentication()
    {
        string? authorization = null;
        string? requestBody = null;
        var handler = new StubHandler(async (request, token) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestBody = await request.Content!.ReadAsStringAsync(token);
            return JsonResponse(HttpStatusCode.OK, RecommendationEnvelope());
        });
        var service = new OpenAiCompatibleAssistantService(new HttpClient(handler));

        AiConfigurationRecommendation result = await service.RecommendAsync(Request("secret-value"));

        Assert.Equal("Bearer secret-value", authorization);
        Assert.DoesNotContain("secret-value", requestBody, StringComparison.Ordinal);
        Assert.Equal("INCONCLUSIVE", result.AssistantVerdict);
        Assert.Equal("时基", Assert.Single(result.Changes).Setting);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("content-array")]
    [InlineData("direct")]
    public async Task AcceptsCommonOpenAiCompatibleResponseVariants(string variant)
    {
        string recommendation = RecommendationJson();
        string response = variant switch
        {
            "markdown" => Envelope($"下面是建议：\n```json\n{recommendation}\n```"),
            "content-array" => JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = new[] { new { type = "text", text = recommendation } } } } }
            }),
            "direct" => recommendation,
            _ => throw new InvalidOperationException()
        };
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, response)))));

        AiConfigurationRecommendation result = await service.RecommendAsync(Request(""));

        Assert.Equal("建议缩短时基", result.Summary);
    }

    [Fact]
    public async Task NormalizesDeepSeekStyleLooseStructuredResponseSafely()
    {
        string loose = JsonSerializer.Serialize(new
        {
            summary = "配置建议",
            changes = LooseChanges,
            manualSteps = LooseManualSteps,
            verificationSteps = LooseVerificationSteps,
            assistantVerdict = "建议可行，但需要根据现场调整",
            confidence = 0.85,
            missingInformation = "缺少额定电流",
            schemaVersion = "1.0"
        });
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, Envelope(loose))))));

        AiConfigurationRecommendation result = await service.RecommendAsync(Request(""));

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("设置时基为100ms/div", result.Changes[0].RecommendedValue);
        Assert.Equal("INCONCLUSIVE", result.AssistantVerdict);
        Assert.Contains("85%", result.Confidence, StringComparison.Ordinal);
        Assert.Contains("模型原始意见", result.Confidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REASONABLE")]
    [InlineData("SUSPICIOUS")]
    [InlineData("UNREASONABLE")]
    [InlineData("INCONCLUSIVE")]
    public async Task PreservesFourEngineeringJudgmentGrades(string verdict)
    {
        var responseObject = new Dictionary<string, object?>
        {
            ["summary"] = "诊断",
            ["waveformAssessment"] = "工程判断",
            ["findings"] = Array.Empty<object>(),
            ["possibleCauses"] = Array.Empty<object>(),
            ["changes"] = LooseChanges,
            ["manualSteps"] = LooseManualSteps,
            ["verificationSteps"] = LooseVerificationSteps,
            ["assistantVerdict"] = verdict,
            ["confidence"] = "中等",
            ["missingInformation"] = "无",
            ["schemaVersion"] = 1
        };
        string content = JsonSerializer.Serialize(responseObject, WebJson);
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, Envelope(content))))));

        AiConfigurationRecommendation result = await service.RecommendAsync(Request(""));

        Assert.Equal(verdict, result.AssistantVerdict);
    }

    [Fact]
    public async Task RateLimitReturnsSafeMessageWithoutResponseBodyOrSecret()
    {
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.TooManyRequests, "secret-value internal")))));

        AiAssistantException error = await Assert.ThrowsAsync<AiAssistantException>(
            () => service.RecommendAsync(Request("secret-value")));

        Assert.Contains("频繁", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJsonIsRejected()
    {
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{broken")))));

        AiAssistantException error = await Assert.ThrowsAsync<AiAssistantException>(
            () => service.RecommendAsync(Request("")));

        Assert.Contains("结构化", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutIsReportedSeparately()
    {
        var service = new OpenAiCompatibleAssistantService(new HttpClient(
            new StubHandler(async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return JsonResponse(HttpStatusCode.OK, RecommendationEnvelope());
            })));
        AiAssistantRequest request = Request("") with { Timeout = TimeSpan.FromSeconds(5) };

        AiAssistantException error = await Assert.ThrowsAsync<AiAssistantException>(
            () => service.RecommendAsync(request));

        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryPersistsRecommendationButNeverNeedsApiKey()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ai-history-{Guid.NewGuid():N}");
        try
        {
            var store = new AiAssistantHistoryStore(new AppPaths(root));
            var recommendation = new AiConfigurationRecommendation("摘要", [], [], [], "NOT_APPLICABLE", "低", "无");
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "model", "目标", null, "0 点", recommendation, "未判定"));

            AiAssistantRecord loaded = Assert.Single(await store.LoadAsync());
            Assert.Equal("摘要", loaded.Recommendation.Summary);
            string file = await File.ReadAllTextAsync(Path.Combine(root, "settings", "ai-assistant-history.json"));
            Assert.DoesNotContain("apiKey", file, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CredentialStoreEncryptsSecretForCurrentWindowsUser()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ai-secret-{Guid.NewGuid():N}");
        try
        {
            var store = new AiCredentialStore(new AppPaths(root));
            await store.SaveAsync("plain-test-secret");

            Assert.Equal("plain-test-secret", await store.LoadAsync());
            byte[] persisted = await File.ReadAllBytesAsync(Path.Combine(root, "settings", "ai-key.dat"));
            Assert.DoesNotContain("plain-test-secret", Encoding.UTF8.GetString(persisted), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AiAssistantRequest Request(string key) => new(
        "https://example.test/v1", "test-model", key,
        new("检查启动配置", "设备：未知", "未知", "MAIN", "NORMal", "RAW", 20000,
            "CHANnel1", "POSitive", 1, "AUTO", [], null, null, "未判定"),
        TimeSpan.FromSeconds(30));

    private static string RecommendationEnvelope()
        => Envelope(RecommendationJson());

    private static string RecommendationJson()
    {
        var recommendation = new AiConfigurationRecommendation(
            "建议缩短时基", [new("时基", "MAIN", "1 ms/div", "观察启动沿", "提高时间分辨率", "可能缩短记录")],
            ["手动设置时基"], ["重新单次采集"], "INCONCLUSIVE", "中等，尚无波形", "需要探头型号");
        return JsonSerializer.Serialize(recommendation, WebJson);
    }

    private static string Envelope(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
