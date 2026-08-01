using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// シートの列幅・行高さからセルインデックス→絶対座標(ポイント)の変換を行う。
/// </summary>
internal class SheetDimensionMap
{
    // Excelデフォルト: 列幅 8.43文字幅≒64pt、行高さ 15pt
    private const double DefaultColumnWidthPt = 64.0;
    private const double DefaultRowHeightPt = 15.0;

    private readonly Dictionary<int, double> _colWidths = [];
    private readonly Dictionary<int, double> _rowHeights = [];

    // 累積座標キャッシュ
    private readonly Dictionary<int, double> _colLeftCache = [];
    private readonly Dictionary<int, double> _rowTopCache = [];

    public SheetDimensionMap(Worksheet worksheet)
    {
        LoadColumnWidths(worksheet);
        LoadRowHeights(worksheet);
    }

    private void LoadColumnWidths(Worksheet worksheet)
    {
        foreach (var col in worksheet.GetFirstChild<Columns>()?.Elements<Column>() ?? [])
        {
            if (col.Width == null) continue;
            double widthPt = CharacterWidthToPt(col.Width.Value);
            int min = (int)(col.Min?.Value ?? 1) - 1; // 0-based
            int max = (int)(col.Max?.Value ?? col.Min?.Value ?? 1) - 1;
            for (int i = min; i <= max; i++)
                _colWidths[i] = widthPt;
        }
    }

    private void LoadRowHeights(Worksheet worksheet)
    {
        foreach (var row in worksheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            if (row.RowIndex == null || row.Height == null) continue;
            _rowHeights[(int)row.RowIndex.Value - 1] = row.Height.Value; // 0-based, already in pt
        }
    }

    public double GetColumnLeft(int colIndex)
    {
        if (_colLeftCache.TryGetValue(colIndex, out var cached)) return cached;
        double x = 0;
        for (int i = 0; i < colIndex; i++)
            x += _colWidths.TryGetValue(i, out var w) ? w : DefaultColumnWidthPt;
        _colLeftCache[colIndex] = x;
        return x;
    }

    public double GetRowTop(int rowIndex)
    {
        if (_rowTopCache.TryGetValue(rowIndex, out var cached)) return cached;
        double y = 0;
        for (int i = 0; i < rowIndex; i++)
            y += _rowHeights.TryGetValue(i, out var h) ? h : DefaultRowHeightPt;
        _rowTopCache[rowIndex] = y;
        return y;
    }

    public double GetColumnWidth(int colIndex) =>
        _colWidths.TryGetValue(colIndex, out var w) ? w : DefaultColumnWidthPt;

    public double GetRowHeight(int rowIndex) =>
        _rowHeights.TryGetValue(rowIndex, out var h) ? h : DefaultRowHeightPt;

    // EMU → ポイント変換(1pt = 12700 EMU)
    public static double EmuToPt(long emu) => emu / 12700.0;

    // Excelのキャラクター幅単位 → ポイント (概算: 1文字幅 ≈ 7.5pt)
    private static double CharacterWidthToPt(double charWidth) => charWidth * 7.5;
}
