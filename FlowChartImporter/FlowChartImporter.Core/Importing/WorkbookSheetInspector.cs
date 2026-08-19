using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

/// <summary>ワークブックのシート一覧を調べる。シート名指定を省略した場合のシート自動選択に使う。</summary>
public static class WorkbookSheetInspector
{
    /// <summary>
    /// シート定義の並び順で、非表示(hidden/veryHidden)ではない最初のシート名を返す。
    /// 全シートが非表示、またはシートが1つも無い場合はnull。
    /// </summary>
    public static string? FindFirstVisibleSheetName(string filePath)
    {
        using var doc = SpreadsheetDocument.Open(filePath, isEditable: false);
        var sheets = doc.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>();
        // OpenXML 3.x の SheetStateValues は定数として扱えないため InnerText で文字列比較する。
        // state属性が省略された場合の既定値は "visible"。
        return sheets
            ?.FirstOrDefault(s => s.State?.InnerText is null or "" or "visible")
            ?.Name?.Value;
    }
}
