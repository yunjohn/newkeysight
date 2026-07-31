using System.Text.Json;
using System.Text.RegularExpressions;
using KeysightScopeApp.Core.Validation;

namespace KeysightScopeApp.Infrastructure.Validation;

public sealed partial class TestProfileRepository(string directory)
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<string> SaveAsync(TestProfile profile, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, SafeName(profile.Name) + ".json");
        string temporary = target + ".tmp";
        TestProfile normalized = profile with { SchemaVersion = 2, CreatedAt = profile.CreatedAt ?? DateTimeOffset.UtcNow };
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, normalized, options, token);
        File.Move(temporary, target, true);
        return target;
    }

    public async Task<TestProfile> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        TestProfile profile = await JsonSerializer.DeserializeAsync<TestProfile>(stream, options, token)
            ?? throw new InvalidDataException("测试方案内容为空。");
        if (profile.SchemaVersion is < 1 or > 2)
            throw new InvalidDataException($"不支持的测试方案版本：{profile.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.ProfileVersion))
            throw new InvalidDataException("测试方案缺少名称或版本。");
        return profile.SchemaVersion == 1 ? profile with { SchemaVersion = 2 } : profile;
    }

    public IReadOnlyList<string> List() => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];

    public static string SafeName(string value)
    {
        string result = UnsafeFileName().Replace(value.Trim(), "_").Trim('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? throw new ArgumentException("名称不能为空。") : result;
    }

    [GeneratedRegex("""[<>:"/\\|?*\p{C}]""")]
    private static partial Regex UnsafeFileName();
}
