using System.Text.Json;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.Infrastructure.Files;

public sealed class WaveformWorkspaceStore(AppPaths paths)
{
    private readonly JsonSerializerOptions options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<WaveformWorkspace?> LoadAsync(string? waveformPath, CancellationToken token = default)
    {
        string path = ResolvePath(waveformPath);
        if (!File.Exists(path)) return null;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            WaveformWorkspace? workspace =
                await JsonSerializer.DeserializeAsync<WaveformWorkspace>(stream, options, token);
            return workspace?.SchemaVersion switch
            {
                WaveformWorkspace.CurrentSchemaVersion => workspace,
                1 => workspace with { SchemaVersion = WaveformWorkspace.CurrentSchemaVersion },
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        string? waveformPath,
        WaveformWorkspace workspace,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string path = ResolvePath(waveformPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, workspace, options, token);
                await stream.FlushAsync(token);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string ResolvePath(string? waveformPath)
    {
        if (!string.IsNullOrWhiteSpace(waveformPath))
            return Path.GetFullPath(waveformPath) + ".workspace.json";
        return Path.Combine(paths.Settings, "live-capture.workspace.json");
    }
}
