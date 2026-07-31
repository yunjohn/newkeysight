using System.Text.Json;
using KeysightScopeApp.Infrastructure.Configuration;

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：KeysightScopeApp.Migrate <Python项目根目录>");
    return 2;
}

try
{
    var paths = new AppPaths();
    var settings = new AppSettingsStore(paths);
    LegacyMigrationSummary summary =
        await new LegacyMigrationService(paths, settings).ImportAsync(args[0]);
    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
    {
        WriteIndented = true
    }));
    return summary.Warnings.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"导入失败：{ex.Message}");
    return 3;
}
