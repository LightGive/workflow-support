using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowChartImporter.Cli;
using FlowChartImporter.Core.Settings;

const string UsageText = """
    使用方法:
      FlowChartImporter <パス> [オプション]

    引数:
      <パス>             解析する .xlsx ファイル、または .xlsx ファイルを含むフォルダのパス。
                         フォルダを指定した場合、直下の .xlsx ファイルを一括処理する
                         (サブフォルダは対象外。"~$" で始まるファイルはExcelのロックファイルとして除外)

    オプション:
      --sheet <シート名> 処理対象のシート名
                         (省略時: 非表示ではない先頭のシートを自動選択)
      --settings <パス>  設定ファイルのパス
                         (省略時: 実行ファイルと同じフォルダの settings.json)
      --output <パス>    JSON 出力先ファイルのパス(<パス>がファイル1つの場合のみ指定可)
                         (省略時: 入力ファイルと同じフォルダの export フォルダに自動出力)
      --csv <パス>       フロー内容を要約したCSVファイルの出力先パス(<パス>がファイル1つの場合のみ指定可)
                         (省略時: 入力ファイルと同じフォルダの export フォルダに自動出力)
      --no-json          JSON を出力しない
      --no-csv           CSV を出力しない
      --summary <パス>   フォルダを一括処理した際のサマリーレポート(テキスト形式)の出力先パス
                         (省略時: <フォルダ>/export/summary.txt)
      --min-row <行番号> この行番号(1始まり)より上にあるシェイプ・テキストを無視する
                         (省略時: シート先頭から対象)
      --ignore-actor <実施主体名>
                         一番左(A列)の実施主体名がこれと一致する行のシェイプ・テキストを無視する
                         (省略時: 無視しない)
      --list-sheets      指定ファイルのシート一覧を表示して終了(フォルダは指定不可)
      --help             このヘルプを表示して終了

    設定ファイル (settings.json) の形式:
      {
        "connectionTolerancePoints": 10.0,
        "nodeNumberingStrategy": "default",
        "nodeNumberFormat": "A-{no}",
        "branchLabelSearchRadiusPoints": 200.0,
        "routeCheckStartShapeType": null,
        "routeCheckEndShapeType": null,
        "categoryNameStart": "開始",
        "categoryNameEnd": "終了",
        "categoryNameBranch": "分岐",
        "categoryNameProcess": "処理",
        "categoryNameCall": "呼び出し"
      }

      connectionTolerancePoints      : コネクタの座標近接判定の許容誤差(ポイント単位)
      nodeNumberingStrategy          : ノード採番戦略名("default" = 左→右、同列は上→下)
      nodeNumberFormat               : 処理名の表示フォーマット。"{no}" が採番番号に置換される
      branchLabelSearchRadiusPoints  : 分岐(ひし形)の近くにあるYES/NOラベルを探す範囲(ポイント単位)。
                                        矢印自体にラベルが無い場合のみ使用。反映されない場合は広げて調整する
      routeCheckStartShapeType /
      routeCheckEndShapeType         : ルート完全性チェックの開始・終了シェイプタイプ(例: "ellipse")。
                                        両方指定した場合のみ、開始から終了へ到達できるか検証する
      categoryName*                  : CSV出力の「種類」列に使う表示名(開始/終了/分岐/処理/呼び出し)

      ファイルが存在しない場合はデフォルト値で新規作成される。詳細は docs/spec.md の「6. 設定ファイル」を参照。
    """;

// ── 引数パース ──────────────────────────────────────────────────
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(UsageText);
    return 0;
}

string? path = null;
string? sheetName = null;
string? settingsPath = null;
string? outputPath = null;
string? csvPath = null;
string? summaryPath = null;
int minRow = 1;
string? ignoreActor = null;
bool listSheets = false;
bool noJson = false;
bool noCsv = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sheet" when i + 1 < args.Length:
            sheetName = args[++i];
            break;
        case "--settings" when i + 1 < args.Length:
            settingsPath = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--csv" when i + 1 < args.Length:
            csvPath = args[++i];
            break;
        case "--summary" when i + 1 < args.Length:
            summaryPath = args[++i];
            break;
        case "--no-json":
            noJson = true;
            break;
        case "--no-csv":
            noCsv = true;
            break;
        case "--min-row" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out minRow) || minRow < 1)
            {
                Console.Error.WriteLine($"エラー: --min-row には1以上の整数を指定してください: {args[i]}");
                return 1;
            }
            break;
        case "--ignore-actor" when i + 1 < args.Length:
            ignoreActor = args[++i];
            break;
        case "--list-sheets":
            listSheets = true;
            break;
        default:
            if (path == null)
            {
                path = args[i];
            }
            else
            {
                Console.Error.WriteLine($"不明な引数: {args[i]}");
                Console.Error.WriteLine(UsageText);
                return 1;
            }
            break;
    }
}

if (path == null)
{
    Console.Error.WriteLine("エラー: Excelファイルまたはフォルダのパスを指定してください。");
    Console.Error.WriteLine(UsageText);
    return 1;
}

bool isDirectory = Directory.Exists(path);
if (!isDirectory && !File.Exists(path))
{
    Console.Error.WriteLine($"エラー: ファイルまたはフォルダが見つかりません: {path}");
    return 1;
}

// ── シート一覧表示 ────────────────────────────────────────────
if (listSheets)
{
    if (isDirectory)
    {
        Console.Error.WriteLine("エラー: --list-sheets はファイルを指定してください(フォルダは指定できません)。");
        return 1;
    }
    PrintSheetNames(path);
    return 0;
}

if (noJson && outputPath != null)
{
    Console.Error.WriteLine("[警告] --no-json が指定されているため --output は無視されます。");
}
if (noCsv && csvPath != null)
{
    Console.Error.WriteLine("[警告] --no-csv が指定されているため --csv は無視されます。");
}
bool writeJson = !noJson;
bool writeCsv = !noCsv;

// ── 設定ファイル読み込み ──────────────────────────────────────
settingsPath ??= Path.Combine(AppContext.BaseDirectory, "settings.json");
var settingsExisted = File.Exists(settingsPath);
var settings = ImportSettingsLoader.Load(settingsPath);
if (!settingsExisted)
{
    Console.Error.WriteLine($"設定ファイルが見つからなかったため、デフォルト値で新規作成しました: {settingsPath}");
}

// ── インポート実行 ────────────────────────────────────────────
if (isDirectory)
{
    return RunBatch(path);
}
return RunSingleFile(path);

// ── ヘルパー ─────────────────────────────────────────────────
int RunBatch(string folderPath)
{
    if (outputPath != null || csvPath != null)
    {
        Console.Error.WriteLine(
            "[警告] フォルダを指定した一括処理では --output/--csv は無視されます(各ファイルと同じフォルダの export に自動出力されます)。");
    }

    var files = Directory.GetFiles(folderPath, "*.xlsx", SearchOption.TopDirectoryOnly)
        .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
    {
        Console.Error.WriteLine($"エラー: 対象の .xlsx ファイルが見つかりませんでした: {folderPath}");
        return 1;
    }

    var outcomes = new List<ImportOutcome>();
    foreach (var file in files)
    {
        Console.Error.WriteLine($"処理中: {file}");

        var exportDir = Path.Combine(Path.GetDirectoryName(file)!, "export");
        var baseName = Path.GetFileNameWithoutExtension(file);
        var jsonOut = writeJson ? Path.Combine(exportDir, baseName + ".json") : null;
        var csvOut = writeCsv ? Path.Combine(exportDir, baseName + ".csv") : null;

        outcomes.Add(ImportRunner.ImportFile(file, sheetName, settings, minRow, ignoreActor, jsonOut, csvOut));
    }

    var resolvedSummaryPath = summaryPath ?? Path.Combine(folderPath, "export", "summary.txt");
    ImportRunner.WriteSummary(resolvedSummaryPath, folderPath, outcomes);
    Console.Error.WriteLine($"サマリー出力完了: {resolvedSummaryPath}");

    var successCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.Success);
    var noShapesCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.NoShapes);
    var failedCount = outcomes.Count(o => o.Status == ImportOutcomeStatus.Failed);
    Console.Error.WriteLine($"  成功: {successCount}件 / 対象図形なし: {noShapesCount}件 / 失敗: {failedCount}件 (全{outcomes.Count}件)");

    return failedCount > 0 ? 1 : 0;
}

int RunSingleFile(string filePath)
{
    var exportDir = Path.Combine(Path.GetDirectoryName(filePath) is { Length: > 0 } dir ? dir : ".", "export");
    var baseName = Path.GetFileNameWithoutExtension(filePath);
    var jsonOut = writeJson ? outputPath ?? Path.Combine(exportDir, baseName + ".json") : null;
    var csvOut = writeCsv ? csvPath ?? Path.Combine(exportDir, baseName + ".csv") : null;

    var outcome = ImportRunner.ImportFile(filePath, sheetName, settings, minRow, ignoreActor, jsonOut, csvOut);

    switch (outcome.Status)
    {
        case ImportOutcomeStatus.Success:
            Console.Error.WriteLine($"  シート: {outcome.SheetName}");
            Console.Error.WriteLine($"  ノード数: {outcome.NodeCount}");
            Console.Error.WriteLine($"  エッジ数: {outcome.EdgeCount}");
            if (jsonOut != null)
            {
                Console.Error.WriteLine($"出力完了: {jsonOut}");
            }
            if (csvOut != null)
            {
                Console.Error.WriteLine($"CSV出力完了: {csvOut}");
            }
            return 0;
        case ImportOutcomeStatus.NoShapes:
            Console.Error.WriteLine($"対象図形が見つかりませんでした。(シート: {outcome.SheetName})");
            return 0;
        default:
            Console.Error.WriteLine($"エラー: {outcome.ErrorMessage}");
            if (outcome.ErrorMessage?.Contains("シート") == true)
            {
                PrintSheetNames(filePath);
            }
            return 1;
    }
}

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
