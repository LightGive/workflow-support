using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowChartImporter.Core.Exporting;
using FlowChartImporter.Core.Importing;
using FlowChartImporter.Core.Settings;

namespace FlowChartImporter.Cli;

internal enum ImportOutcomeStatus
{
    Success,
    NoShapes,
    Failed,
}

internal record ImportOutcome(
    string FilePath,
    string? SheetName,
    ImportOutcomeStatus Status,
    int NodeCount,
    int EdgeCount,
    int WarningCount,
    string? ErrorMessage);

/// <summary>1ファイル分のインポート実行と、JSON/CSV出力・一括処理サマリー(txt)の書き出しを行う。</summary>
internal static class ImportRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// 1ファイルをインポートし、必要に応じてJSON/CSVを出力する。
    /// シート名(sheetNameOverride)がnullの場合、非表示ではない先頭のシートを自動選択する。
    /// 例外は内部で捕捉し、失敗としてImportOutcomeに反映する(呼び出し側は一括処理でも1件ずつ継続できる)。
    /// </summary>
    public static ImportOutcome ImportFile(
        string filePath,
        string? sheetNameOverride,
        ImportSettings settings,
        int minRow,
        string? ignoreActor,
        string? jsonOutputPath,
        string? csvOutputPath)
    {
        string? sheetName = sheetNameOverride;
        try
        {
            if (sheetName == null)
            {
                sheetName = WorkbookSheetInspector.FindFirstVisibleSheetName(filePath);
                if (sheetName == null)
                {
                    return new ImportOutcome(filePath, null, ImportOutcomeStatus.Failed, 0, 0, 0,
                        "表示状態のシートが見つかりませんでした。");
                }
            }

            var service = new ExcelImportService(settings);
            var result = service.Import(filePath, sheetName, minRow, ignoreActor);

            foreach (var warning in result.Warnings)
            {
                Console.Error.WriteLine($"[警告] {Path.GetFileName(filePath)} ({sheetName}): {warning}");
            }

            if (result.FlowChart.Nodes.Count == 0)
            {
                return new ImportOutcome(filePath, sheetName, ImportOutcomeStatus.NoShapes, 0, 0, result.Warnings.Count, null);
            }

            if (jsonOutputPath != null)
            {
                EnsureDirectoryExists(jsonOutputPath);
                File.WriteAllText(jsonOutputPath, JsonSerializer.Serialize(result.FlowChart, JsonOptions));
            }

            if (csvOutputPath != null)
            {
                EnsureDirectoryExists(csvOutputPath);
                var csv = new FlowChartCsvExporter().Export(result.FlowChart, settings);
                File.WriteAllText(csvOutputPath, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }

            return new ImportOutcome(filePath, sheetName, ImportOutcomeStatus.Success,
                result.FlowChart.Nodes.Count, result.FlowChart.Edges.Count, result.Warnings.Count, null);
        }
        catch (Exception ex)
        {
            return new ImportOutcome(filePath, sheetName, ImportOutcomeStatus.Failed, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>フォルダ一括処理の結果一覧を、人が読みやすいテキスト形式のサマリーレポートとして書き出す。</summary>
    public static void WriteSummary(string summaryPath, string sourceFolder, IReadOnlyList<ImportOutcome> outcomes)
    {
        EnsureDirectoryExists(summaryPath);

        var successCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.Success);
        var noShapesCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.NoShapes);
        var failedCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.Failed);

        var sb = new StringBuilder();
        sb.AppendLine("FlowChartImporter 一括処理サマリー");
        sb.AppendLine($"対象フォルダ: {sourceFolder}");
        sb.AppendLine($"実行日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"対象ファイル数: {outcomes.Count}");
        sb.AppendLine();

        foreach (var outcome in outcomes)
        {
            var fileName = Path.GetFileName(outcome.FilePath);
            switch (outcome.Status)
            {
                case ImportOutcomeStatus.Success:
                    sb.AppendLine(
                        $"[成功] {fileName} (シート: {outcome.SheetName}) ノード数: {outcome.NodeCount} エッジ数: {outcome.EdgeCount} 警告: {outcome.WarningCount}件");
                    break;
                case ImportOutcomeStatus.NoShapes:
                    sb.AppendLine($"[対象図形なし] {fileName} (シート: {outcome.SheetName})");
                    break;
                case ImportOutcomeStatus.Failed:
                    sb.AppendLine($"[失敗] {fileName} {outcome.ErrorMessage}");
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 50));
        sb.AppendLine($"成功: {successCount}件 / 対象図形なし: {noShapesCount}件 / 失敗: {failedCount}件 (全{outcomes.Count}件)");

        File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
