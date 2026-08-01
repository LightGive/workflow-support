using System.Text.Json;

namespace FlowChartImporter.Core.Settings;

public static class ImportSettingsLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ImportSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new ImportSettings();

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<ImportSettings>(json, Options) ?? new ImportSettings();
    }
}
