using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

internal class ActorDetector
{
    // セル参照(例: "AB12")の列文字は A〜Z の26文字を基数とした26進数として解釈する
    private const int AlphabetLetterCount = 26;

    public record ActorRange(string Name, int StartRow, int EndRow); // 0始まり、両端含む

    public List<ActorRange> Detect(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var worksheet = worksheetPart.Worksheet!;
        var sheetData = worksheet.GetFirstChild<SheetData>();
        if (sheetData == null)
        {
            return [];
        }

        // A列の結合セル開始行 → 終了行のマップを構築
        var mergeEndByStartRow = BuildMergeMap(worksheet);
        var result = new List<ActorRange>();

        foreach (var row in sheetData.Elements<Row>().OrderBy(r => r.RowIndex?.Value ?? 0))
        {
            int rowIndex = (int)(row.RowIndex?.Value ?? 0) - 1; // 0始まり

            // 既に追加済みの範囲に含まれる行はスキップ
            if (result.Any(r => r.StartRow <= rowIndex && rowIndex <= r.EndRow))
            {
                continue;
            }

            var cellA = row.Elements<Cell>().FirstOrDefault(c => IsColumnA(c.CellReference?.Value));
            if (cellA == null)
            {
                continue;
            }

            var value = GetCellText(cellA, sharedStrings);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            int endRow = mergeEndByStartRow.TryGetValue(rowIndex, out var mergeEnd)
                ? mergeEnd
                : rowIndex;

            result.Add(new ActorRange(value, rowIndex, endRow));
        }

        return [.. result.OrderBy(r => r.StartRow)];
    }

    /// <summary>
    /// 図形の行範囲(fromRow〜toRow)と重なる実施主体(部署・システム・他社等)をすべて返す(開始行順)。
    /// 図形が複数の実施主体をまたいで配置されている場合は複数件返る。
    /// </summary>
    public List<string> GetActors(List<ActorRange> ranges, int fromRow, int toRow)
    {
        return ranges
            .Where(r => r.StartRow <= toRow && fromRow <= r.EndRow)
            .OrderBy(r => r.StartRow)
            .Select(r => r.Name)
            .ToList();
    }

    private static Dictionary<int, int> BuildMergeMap(Worksheet worksheet)
    {
        var map = new Dictionary<int, int>();
        foreach (var mc in worksheet.Descendants<MergeCell>())
        {
            var range = mc.Reference?.Value;
            if (range == null)
            {
                continue;
            }

            var parts = range.Split(':');
            if (parts.Length != 2)
            {
                continue;
            }

            var (col1, row1) = ParseCellRef(parts[0]);
            var (_, row2) = ParseCellRef(parts[1]);

            if (col1 != 0)
            {
                continue; // A列のみ
            }
            map[row1] = row2;
        }
        return map;
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null
            && int.TryParse(cell.CellValue?.Text, out var idx))
        {
            var item = sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(idx);
            return item != null ? GetSharedStringText(item) : string.Empty;
        }
        return cell.CellValue?.Text ?? string.Empty;
    }

    // SharedStringItem.InnerText はフリガナ(rPh)内のテキストも含めて連結してしまうため、
    // 表示テキスト(t / r/t)のみを対象に組み立てる。
    private static string GetSharedStringText(SharedStringItem item)
    {
        var text = string.Concat(item.Elements<Text>().Select(t => t.Text));
        text += string.Concat(item.Elements<Run>().Select(r => r.Text?.Text ?? string.Empty));
        return text;
    }

    private static bool IsColumnA(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef))
        {
            return false;
        }
        var (col, _) = ParseCellRef(cellRef);
        return col == 0;
    }

    internal static (int col, int row) ParseCellRef(string cellRef)
    {
        int col = 0, i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
            col = col * AlphabetLetterCount + (char.ToUpper(cellRef[i++]) - 'A' + 1);
        int row = int.TryParse(cellRef[i..], out var r) ? r - 1 : 0;
        return (col - 1, row); // どちらも0始まり
    }
}
