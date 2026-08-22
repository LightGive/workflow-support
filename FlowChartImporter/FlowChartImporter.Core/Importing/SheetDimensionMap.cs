using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// シートの列幅・行高さからセルインデックス→絶対座標(ポイント)の変換を行う。
/// </summary>
internal class SheetDimensionMap
{
    // シートに defaultColWidth/defaultRowHeight (sheetFormatPr) の指定が無い場合のみ使うExcel既定値。
    // 列幅 8.43文字幅≒64pt、行高さ 15pt。
    private const double FallbackColumnWidthPt = 64.0;
    private const double FallbackRowHeightPt = 15.0;

    private readonly double _defaultColumnWidthPt;
    private readonly double _defaultRowHeightPt;

    private readonly Dictionary<int, double> _colWidths = [];
    private readonly Dictionary<int, double> _rowHeights = [];

    // 累積座標キャッシュ
    private readonly Dictionary<int, double> _colLeftCache = [];
    private readonly Dictionary<int, double> _rowTopCache = [];

    public SheetDimensionMap(Worksheet worksheet)
    {
        var sheetFormatPr = worksheet.GetFirstChild<SheetFormatProperties>();
        _defaultColumnWidthPt = sheetFormatPr?.DefaultColumnWidth?.Value is double w
            ? CharacterWidthToPt(w)
            : FallbackColumnWidthPt;
        _defaultRowHeightPt = sheetFormatPr?.DefaultRowHeight?.Value is double h
            ? h // sheetFormatPr の defaultRowHeight は元々ポイント単位
            : FallbackRowHeightPt;

        LoadColumnWidths(worksheet);
        LoadRowHeights(worksheet);
    }

    private void LoadColumnWidths(Worksheet worksheet)
    {
        foreach (var col in worksheet.GetFirstChild<Columns>()?.Elements<Column>() ?? [])
        {
            if (col.Width == null)
            {
                continue;
            }
            double widthPt = CharacterWidthToPt(col.Width.Value);
            int min = (int)(col.Min?.Value ?? 1) - 1; // 0-based
            int max = (int)(col.Max?.Value ?? col.Min?.Value ?? 1) - 1;
            for (int i = min; i <= max; i++)
            {
                _colWidths[i] = widthPt;
            }
        }
    }

    private void LoadRowHeights(Worksheet worksheet)
    {
        foreach (var row in worksheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            if (row.RowIndex == null || row.Height == null)
            {
                continue;
            }
            _rowHeights[(int)row.RowIndex.Value - 1] = row.Height.Value; // 0-based, already in pt
        }
    }

    public double GetColumnLeft(int colIndex)
    {
        if (_colLeftCache.TryGetValue(colIndex, out var cached))
        {
            return cached;
        }
        double x = 0;
        for (int i = 0; i < colIndex; i++)
        {
            x += _colWidths.TryGetValue(i, out var w) ? w : _defaultColumnWidthPt;
        }
        _colLeftCache[colIndex] = x;
        return x;
    }

    public double GetRowTop(int rowIndex)
    {
        if (_rowTopCache.TryGetValue(rowIndex, out var cached))
        {
            return cached;
        }
        double y = 0;
        for (int i = 0; i < rowIndex; i++)
        {
            y += _rowHeights.TryGetValue(i, out var h) ? h : _defaultRowHeightPt;
        }
        _rowTopCache[rowIndex] = y;
        return y;
    }

    public double GetColumnWidth(int colIndex) =>
        _colWidths.TryGetValue(colIndex, out var w) ? w : _defaultColumnWidthPt;

    public double GetRowHeight(int rowIndex) =>
        _rowHeights.TryGetValue(rowIndex, out var h) ? h : _defaultRowHeightPt;

    // EMU → ポイント変換(1pt = 12700 EMU)
    public static double EmuToPt(long emu) => emu / 12700.0;
    public static double EmuToPt(double emu) => emu / 12700.0;

    // 既定フォント(Calibri 11)の最大数字幅(ピクセル)。列幅→ピクセル変換に使う。
    private const double MaxDigitWidthPx = 7.0;

    // Excelのキャラクター幅単位 → ポイント。
    // ECMA-376 準拠の変換式(pixels = floor(((256*width + floor(128/MDW)) / 256) * MDW))で
    // 一旦ピクセルに変換し、96DPI換算(1px = 0.75pt)でポイントに変換する。
    // 単純な「1文字幅 ≈ 7.5pt」という概算は、特に既定幅に近い狭い列で実際の描画位置との
    // 誤差が大きく(数十pt単位でズレる)、分岐からの角度判定(BranchLabelResolver)のような
    // 僅かな誤差にも敏感な処理では無視できないため、より正確な式に置き換えている。
    private static double CharacterWidthToPt(double charWidth)
    {
        double pixels = Math.Floor(((256 * charWidth + Math.Floor(128 / MaxDigitWidthPx)) / 256) * MaxDigitWidthPx);
        return pixels * 0.75;
    }
}
