using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowChartImporter.Core.Importing;
using FlowChartImporter.Core.Settings;

const string UsageText = """
    使用方法:
      FlowChartImporter <Excelファイル> <シート名> [オプション]

    引数:
      <Excelファイル>   解析する .xlsx ファイルのパス
      <シート名>        処理対象のシート名

    オプション:
      --settings <パス>  設定ファイルのパス
                         (省略時: 実行ファイルと同じフォルダの settings.json)
      --output <パス>    JSON 出力先ファイルのパス
                         (省略時: 標準出力)
      --list-sheets      指定ファイルのシート一覧を表示して終了
      --help             このヘルプを表示して終了

    設定ファイル (settings.json) の形式:
      {
        "connectionTolerancePoints": 10.0,
        "nodeNumberingStrategy": "default"
      }
    """;

// ── 引数パース ──────────────────────────────────────────────────
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(UsageText);
    return 0;
}

string? filePath = null;
string? sheetName = null;
string? settingsPath = null;
string? outputPath = null;
bool listSheets = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--settings" when i + 1 < args.Length:
            settingsPath = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--list-sheets":
            listSheets = true;
            break;
        default:
            if (filePath == null) filePath = args[i];
            else if (sheetName == null) sheetName = args[i];
            else
            {
                Console.Error.WriteLine($"不明な引数: {args[i]}");
                Console.Error.WriteLine(UsageText);
                return 1;
            }
            break;
    }
}

if (filePath == null)
{
    Console.Error.WriteLine("エラー: Excelファイルのパスを指定してください。");
    Console.Error.WriteLine(UsageText);
    return 1;
}

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"エラー: ファイルが見つかりません: {filePath}");
    return 1;
}

// ── シート一覧表示 ────────────────────────────────────────────
if (listSheets)
{
    PrintSheetNames(filePath);
    return 0;
}

if (sheetName == null)
{
    Console.Error.WriteLine("エラー: シート名を指定してください。");
    Console.Error.WriteLine(UsageText);
    return 1;
}

// ── 設定ファイル読み込み ──────────────────────────────────────
settingsPath ??= Path.Combine(AppContext.BaseDirectory, "settings.json");
var settings = ImportSettingsLoader.Load(settingsPath);

// ── インポート実行 ────────────────────────────────────────────
try
{
    var service = new ExcelImportService(settings);
    var result = service.Import(filePath, sheetName);

    // 警告を標準エラーへ出力
    foreach (var warning in result.Warnings)
        Console.Error.WriteLine($"[警告] {warning}");

    // JSON シリアライズ
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    var json = JsonSerializer.Serialize(result.FlowChart, jsonOptions);

    if (outputPath != null)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        File.WriteAllText(outputPath, json);
        Console.Error.WriteLine($"出力完了: {outputPath}");
        Console.Error.WriteLine($"  ノード数: {result.FlowChart.Nodes.Count}");
        Console.Error.WriteLine($"  エッジ数: {result.FlowChart.Edges.Count}");
    }
    else
    {
        Console.WriteLine(json);
    }

    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");

    // シートが見つからない場合はシート一覧を表示
    if (ex.Message.Contains("シート"))
        PrintSheetNames(filePath);

    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"予期しないエラーが発生しました: {ex.Message}");
    return 1;
}

// ── ヘルパー ─────────────────────────────────────────────────
static void PrintSheetNames(string filePath)
{
    try
    {
        using var doc = SpreadsheetDocument.Open(filePath, isEditable: false);
        var sheets = doc.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>().ToList();
        if (sheets == null || sheets.Count == 0)
        {
            Console.Error.WriteLine("シートが見つかりませんでした。");
            return;
        }
        Console.Error.WriteLine("利用可能なシート:");
        foreach (var sheet in sheets)
            Console.Error.WriteLine($"  - {sheet.Name?.Value}");
    }
    catch
    {
        Console.Error.WriteLine("シート一覧の取得に失敗しました。");
    }
}
