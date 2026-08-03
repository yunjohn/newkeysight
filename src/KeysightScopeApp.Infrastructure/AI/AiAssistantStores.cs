using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.Infrastructure.AI;

[SupportedOSPlatform("windows")]
public sealed class AiCredentialStore(AppPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeysightScopeApp.AI.v1");
    private string PathName => Path.Combine(paths.Settings, "ai-key.dat");

    public async Task SaveAsync(string apiKey, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(PathName)) File.Delete(PathName);
            return;
        }
        byte[] clear = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            string temporary = PathName + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted, token);
            File.Move(temporary, PathName, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task<string> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(PathName)) return "";
        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(PathName, token);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (CryptographicException) { return ""; }
    }
}

public sealed record AiAssistantHistoryDocument(
    IReadOnlyList<AiAssistantRecord> Records,
    int SchemaVersion = 1);

public sealed class AiAssistantHistoryStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private string PathName => Path.Combine(paths.Settings, "ai-assistant-history.json");

    public async Task<IReadOnlyList<AiAssistantRecord>> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(PathName)) return [];
            await using FileStream stream = File.OpenRead(PathName);
            AiAssistantHistoryDocument? document =
                await JsonSerializer.DeserializeAsync<AiAssistantHistoryDocument>(stream, Options, token);
            return document is { SchemaVersion: 1 } ? document.Records : [];
        }
        catch (JsonException) { return []; }
    }

    public async Task AppendAsync(AiAssistantRecord record, CancellationToken token = default)
    {
        List<AiAssistantRecord> records = (await LoadAsync(token)).TakeLast(199).ToList();
        records.Add(record);
        string temporary = PathName + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, new AiAssistantHistoryDocument(records), Options, token);
        File.Move(temporary, PathName, true);
    }
}
