using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlowChartImporter.Core.Settings;

public static class ImportSettingsLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // 日本語をそのまま出力する(デフォルトだと \uXXXX にエスケープされ設定ファイルが読みづらくなるため)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 設定ファイルを読み込む。ファイルが存在しない場合はデフォルト値の設定ファイルを新規作成したうえで、
    /// そのデフォルト値を返す。
    /// </summary>
    public static ImportSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var defaults = new ImportSettings();

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, JsonSerializer.Serialize(defaults, WriteOptions));
            return defaults;
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<ImportSettings>(json, ReadOptions) ?? new ImportSettings();
    }
}
