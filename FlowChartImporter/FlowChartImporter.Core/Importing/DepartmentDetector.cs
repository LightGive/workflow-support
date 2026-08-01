using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

internal class DepartmentDetector
{
    public record DepartmentRange(string Name, int StartRow, int EndRow); // 0始まり、両端含む

    public List<DepartmentRange> Detect(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var worksheet = worksheetPart.Worksheet!;
        var sheetData = worksheet.GetFirstChild<SheetData>();
        if (sheetData == null) return [];

        // A列の結合セル開始行 → 終了行のマップを構築
        var mergeEndByStartRow = BuildMergeMap(worksheet);
        var result = new List<DepartmentRange>();

        foreach (var row in sheetData.Elements<Row>().OrderBy(r => r.RowIndex?.Value ?? 0))
        {
            int rowIndex = (int)(row.RowIndex?.Value ?? 0) - 1; // 0始まり

            // 既に追加済みの範囲に含まれる行はスキップ
            if (result.Any(r => r.StartRow <= rowIndex && rowIndex <= r.EndRow))
                continue;

            var cellA = row.Elements<Cell>().FirstOrDefault(c => IsColumnA(c.CellReference?.Value));
            if (cellA == null) continue;

            var value = GetCellText(cellA, sharedStrings);
            if (string.IsNullOrWhiteSpace(value)) continue;

            int endRow = mergeEndByStartRow.TryGetValue(rowIndex, out var mergeEnd)
                ? mergeEnd
                : rowIndex;

            result.Add(new DepartmentRange(value, rowIndex, endRow));
        }

        return [.. result.OrderBy(r => r.StartRow)];
    }

    public string? GetDepartment(List<DepartmentRange> ranges, int centerRow)
    {
        // 境界値は後(下側)の部署に割り当てる
        var matching = ranges.Where(r => r.StartRow <= centerRow && centerRow <= r.EndRow).ToList();
        if (matching.Count == 0) return null;
        return matching.OrderByDescending(r => r.StartRow).First().Name;
    }

    private static Dictionary<int, int> BuildMergeMap(Worksheet worksheet)
    {
        var map = new Dictionary<int, int>();
        foreach (var mc in worksheet.Descendants<MergeCell>())
        {
            var range = mc.Reference?.Value;
            if (range == null) continue;

            var parts = range.Split(':');
            if (parts.Length != 2) continue;

            var (col1, row1) = ParseCellRef(parts[0]);
            var (_, row2) = ParseCellRef(parts[1]);

            if (col1 != 0) continue; // A列のみ
            map[row1] = row2;
        }
        return map;
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null
            && int.TryParse(cell.CellValue?.Text, out var idx))
        {
            return sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(idx)?.InnerText
                   ?? string.Empty;
        }
        return cell.CellValue?.Text ?? string.Empty;
    }

    private static bool IsColumnA(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return false;
        var (col, _) = ParseCellRef(cellRef);
        return col == 0;
    }

    internal static (int col, int row) ParseCellRef(string cellRef)
    {
        int col = 0, i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
            col = col * 26 + (char.ToUpper(cellRef[i++]) - 'A' + 1);
        int row = int.TryParse(cellRef[i..], out var r) ? r - 1 : 0;
        return (col - 1, row); // どちらも0始まり
    }
}
