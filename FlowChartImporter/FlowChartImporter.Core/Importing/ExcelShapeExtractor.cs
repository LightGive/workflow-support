using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.Drawing.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;
using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;
using DwgSheet = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace FlowChartImporter.Core.Importing;

internal class ExcelShapeExtractor
{
    private const double EmuToPt = 1.0 / 12700.0;

    // グループ図形(xdr:grpSp)内の子図形の座標系(EMU、子座標系原点基準)を
    // ワークシート上の絶対座標系(EMU)に変換するアフィン変換。
    // グループに属さない図形は Identity (変換なし) を使う。
    private readonly record struct GroupTransform(double ScaleX, double ScaleY, double OffX, double OffY)
    {
        public static readonly GroupTransform Identity = new(1, 1, 0, 0);

        public double TransformX(double x) => OffX + x * ScaleX;
        public double TransformY(double y) => OffY + y * ScaleY;
        public double ScaleLengthX(double w) => w * ScaleX;
        public double ScaleLengthY(double h) => h * ScaleY;
    }

    /// <summary>
    /// シート内の図形・コネクタを抽出する。
    /// </summary>
    /// <param name="minRowIndex">
    /// この行(0始まり)より上(小さい行)にアンカーされたシェイプ・コネクタ・グループを無視する。
    /// 既定値の0を指定した場合は何も無視しない。
    /// </param>
    /// <param name="ignoredRowRanges">
    /// この行範囲(0始まり、両端含む)にアンカーされたシェイプ・コネクタ・グループを無視する。
    /// 無視する実施主体の行範囲を指定するために使う。nullの場合は何も無視しない。
    /// </param>
    public (List<ShapeInfo> Shapes, List<ConnectorInfo> Connectors, List<string> Warnings)
        Extract(WorksheetPart worksheetPart, int minRowIndex = 0, IReadOnlyList<(int StartRow, int EndRow)>? ignoredRowRanges = null)
    {
        var shapes = new List<ShapeInfo>();
        var connectors = new List<ConnectorInfo>();
        var warnings = new List<string>();

        bool IsIgnoredRow(int row) =>
            row < minRowIndex || (ignoredRowRanges?.Any(r => row >= r.StartRow && row <= r.EndRow) ?? false);

        var drawingsPart = worksheetPart.DrawingsPart;
        if (drawingsPart == null)
        {
            return (shapes, connectors, warnings);
        }

        var dimMap = new SheetDimensionMap(worksheetPart.Worksheet!);
        var wsDr = drawingsPart.WorksheetDrawing;
        if (wsDr == null)
        {
            return (shapes, connectors, warnings);
        }

        foreach (var anchor in wsDr.Elements<TwoCellAnchor>())
        {
            var (fromCol, fromColOffEmu, fromRow, fromRowOffEmu) = ReadMarker(anchor.FromMarker);
            var (toCol, toColOffEmu, toRow, toRowOffEmu) = ReadMarker(anchor.ToMarker);

            if (IsIgnoredRow(fromRow))
            {
                continue;
            }

            // セルアンカーベースの座標(行列番号用)
            double anchorLeft = dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu);
            double anchorTop = dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu);

            // xdr:sp (通常シェイプ)
            var sp = anchor.GetFirstChild<DwgSheet.Shape>();
            if (sp != null)
            {
                var info = ExtractShape(sp, GroupTransform.Identity, anchorLeft, anchorTop, fromRow, fromCol, toRow, toCol, warnings);
                if (info != null)
                {
                    shapes.Add(info);
                }
                continue;
            }

            // xdr:cxnSp (明示的なコネクタ)
            var cxnSp = anchor.GetFirstChild<DwgSheet.ConnectionShape>();
            if (cxnSp != null)
            {
                var info = ExtractConnector(cxnSp, GroupTransform.Identity, anchorLeft, anchorTop,
                    dimMap.GetColumnLeft(toCol) + SheetDimensionMap.EmuToPt(toColOffEmu),
                    dimMap.GetRowTop(toRow) + SheetDimensionMap.EmuToPt(toRowOffEmu));
                if (info != null)
                {
                    connectors.Add(info);
                }
                continue;
            }

            // xdr:grpSp (グループ化された図形)
            var grpSp = anchor.GetFirstChild<DwgSheet.GroupShape>();
            if (grpSp != null)
            {
                ProcessGroupShape(grpSp, GroupTransform.Identity, fromRow, fromCol, toRow, toCol, shapes, connectors, warnings);
            }
        }

        foreach (var anchor in wsDr.Elements<OneCellAnchor>())
        {
            var (fromCol, fromColOffEmu, fromRow, fromRowOffEmu) = ReadMarker(anchor.FromMarker);

            if (IsIgnoredRow(fromRow))
            {
                continue;
            }

            double anchorLeft = dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu);
            double anchorTop = dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu);

            var sp = anchor.GetFirstChild<DwgSheet.Shape>();
            if (sp != null)
            {
                var info = ExtractShape(sp, GroupTransform.Identity, anchorLeft, anchorTop, fromRow, fromCol, fromRow, fromCol, warnings);
                if (info != null)
                {
                    shapes.Add(info);
                }
                continue;
            }

            var grpSp = anchor.GetFirstChild<DwgSheet.GroupShape>();
            if (grpSp != null)
            {
                ProcessGroupShape(grpSp, GroupTransform.Identity, fromRow, fromCol, fromRow, fromCol, shapes, connectors, warnings);
            }
        }

        return (shapes, connectors, warnings);
    }

    // グループ図形の中身(通常シェイプ・コネクタ・入れ子のグループ)を再帰的に処理し、
    // 子図形固有の座標系を親の変換と合成した絶対座標系に変換する。
    private void ProcessGroupShape(
        DwgSheet.GroupShape grpSp,
        GroupTransform parentTransform,
        int fromRow, int fromCol, int toRow, int toCol,
        List<ShapeInfo> shapes,
        List<ConnectorInfo> connectors,
        List<string> warnings)
    {
        var transform = ComposeGroupTransform(parentTransform, grpSp.GroupShapeProperties?.TransformGroup);

        foreach (var sp in grpSp.Elements<DwgSheet.Shape>())
        {
            var info = ExtractShape(sp, transform, 0, 0, fromRow, fromCol, toRow, toCol, warnings);
            if (info != null)
            {
                shapes.Add(info);
            }
        }

        foreach (var cxnSp in grpSp.Elements<DwgSheet.ConnectionShape>())
        {
            var info = ExtractConnector(cxnSp, transform, 0, 0, 0, 0);
            if (info != null)
            {
                connectors.Add(info);
            }
        }

        foreach (var nestedGroup in grpSp.Elements<DwgSheet.GroupShape>())
        {
            ProcessGroupShape(nestedGroup, transform, fromRow, fromCol, toRow, toCol, shapes, connectors, warnings);
        }
    }

    private static GroupTransform ComposeGroupTransform(GroupTransform parent, TransformGroup? xfrm)
    {
        if (xfrm?.Offset == null || xfrm.Extents == null || xfrm.ChildOffset == null || xfrm.ChildExtents == null
            || xfrm.ChildExtents.Cx?.Value is not (> 0) || xfrm.ChildExtents.Cy?.Value is not (> 0))
        {
            return parent;
        }

        double localScaleX = (double)xfrm.Extents.Cx!.Value! / xfrm.ChildExtents.Cx!.Value!;
        double localScaleY = (double)xfrm.Extents.Cy!.Value! / xfrm.ChildExtents.Cy!.Value!;
        double localOffX = (xfrm.Offset.X?.Value ?? 0) - (xfrm.ChildOffset.X?.Value ?? 0) * localScaleX;
        double localOffY = (xfrm.Offset.Y?.Value ?? 0) - (xfrm.ChildOffset.Y?.Value ?? 0) * localScaleY;

        return new GroupTransform(
            ScaleX: localScaleX * parent.ScaleX,
            ScaleY: localScaleY * parent.ScaleY,
            OffX: parent.OffX + localOffX * parent.ScaleX,
            OffY: parent.OffY + localOffY * parent.ScaleY);
    }

    private static ShapeInfo? ExtractShape(
        DwgSheet.Shape sp,
        GroupTransform transform,
        double anchorLeft, double anchorTop,
        int fromRow, int fromCol, int toRow, int toCol,
        List<string> warnings)
    {
        var cNvPr = sp.NonVisualShapeProperties?.NonVisualDrawingProperties;
        if (cNvPr?.Id?.Value == null)
        {
            warnings.Add($"ID のないシェイプをスキップしました (name={cNvPr?.Name?.Value})");
            return null;
        }

        // a:xfrm から正確な EMU 座標を取得
        var xfrm = sp.ShapeProperties?.Transform2D;
        double left, top, width, height;
        bool flipH = false, flipV = false;

        if (xfrm?.Offset != null && xfrm.Extents != null)
        {
            left = transform.TransformX(xfrm.Offset.X?.Value ?? 0) * EmuToPt;
            top = transform.TransformY(xfrm.Offset.Y?.Value ?? 0) * EmuToPt;
            width = transform.ScaleLengthX(xfrm.Extents.Cx?.Value ?? 0) * EmuToPt;
            height = transform.ScaleLengthY(xfrm.Extents.Cy?.Value ?? 0) * EmuToPt;
            flipH = xfrm.HorizontalFlip?.Value ?? false;
            flipV = xfrm.VerticalFlip?.Value ?? false;
        }
        else
        {
            left = anchorLeft;
            top = anchorTop;
            width = 0;
            height = 0;
        }

        var prstGeom = sp.ShapeProperties?.GetFirstChild<PresetGeometry>();
        var preset = prstGeom?.Preset?.InnerText;
        var shapeType = ShapeGeometryClassifier.MapPreset(preset);
        if (shapeType == Models.ShapeType.Unknown)
        {
            // 一部のファイルでは「フローチャート: 判断」(ひし形)がプリセットではなく
            // 自由図形(a:custGeom)のひし形パスとして保存されているため、その形状も判定する。
            var custGeom = sp.ShapeProperties?.GetFirstChild<CustomGeometry>();
            if (ShapeGeometryClassifier.IsDiamondPath(custGeom))
            {
                shapeType = Models.ShapeType.Diamond;
            }
        }
        var text = ExtractText(sp);
        var isTextBox = sp.NonVisualShapeProperties?.NonVisualShapeDrawingProperties?.TextBox?.Value ?? false;
        var outline = sp.ShapeProperties?.GetFirstChild<Outline>();
        var hasNoLine = outline?.GetFirstChild<NoFill>() != null;
        var isDashed = ShapeGeometryClassifier.IsDashedLine(outline);

        return new ShapeInfo
        {
            XmlId = cNvPr.Id.Value,
            Name = cNvPr.Name?.Value ?? string.Empty,
            ShapeType = shapeType,
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            FlipH = flipH,
            FlipV = flipV,
            AnchorFromRow = fromRow,
            AnchorFromCol = fromCol,
            AnchorToRow = toRow,
            AnchorToCol = toCol,
            IsTextBox = isTextBox,
            HasNoLine = hasNoLine,
            IsDashed = isDashed,
        };
    }

    // コネクタ自身の a:xfrm (off/ext) を基準座標系に変換し、その対角線を始点・終点とする。
    // カギ型接続線は同じ接続先(stCxn)を共有する複数のコネクタでもそれぞれ経路が異なり、
    // a:xfrm(バウンディングボックス)はコネクタごとに異なるため、複数本を正しく区別できる。
    // (トップレベルのアンカーの from/to は複数のコネクタで共有されることがあり、区別できないため使わない)
    // a:xfrm が無い場合のみ、呼び出し元が渡すフォールバック座標(アンカー基準)を使う。
    private static ConnectorInfo? ExtractConnector(
        DwgSheet.ConnectionShape cxnSp, GroupTransform transform,
        double fallbackStartX, double fallbackStartY, double fallbackEndX, double fallbackEndY)
    {
        var cNvPr = cxnSp.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties;
        if (cNvPr?.Id?.Value == null)
        {
            return null;
        }

        double startX, startY, endX, endY;
        var xfrm = cxnSp.ShapeProperties?.Transform2D;
        if (xfrm?.Offset != null && xfrm.Extents != null)
        {
            double left = transform.TransformX(xfrm.Offset.X?.Value ?? 0) * EmuToPt;
            double top = transform.TransformY(xfrm.Offset.Y?.Value ?? 0) * EmuToPt;
            double right = left + transform.ScaleLengthX(xfrm.Extents.Cx?.Value ?? 0) * EmuToPt;
            double bottom = top + transform.ScaleLengthY(xfrm.Extents.Cy?.Value ?? 0) * EmuToPt;

            bool flipH = xfrm.HorizontalFlip?.Value ?? false;
            bool flipV = xfrm.VerticalFlip?.Value ?? false;
            (startX, endX) = flipH ? (right, left) : (left, right);
            (startY, endY) = flipV ? (bottom, top) : (top, bottom);
        }
        else
        {
            (startX, startY, endX, endY) = (fallbackStartX, fallbackStartY, fallbackEndX, fallbackEndY);
        }

        var cxnSpPr = cxnSp.NonVisualConnectionShapeProperties
            ?.NonVisualConnectorShapeDrawingProperties;
        var stCxn = cxnSpPr?.GetFirstChild<StartConnection>();
        var endCxn = cxnSpPr?.GetFirstChild<EndConnection>();

        return new ConnectorInfo
        {
            XmlId = cNvPr.Id.Value,
            StartShapeXmlId = stCxn?.Id?.Value,
            EndShapeXmlId = endCxn?.Id?.Value,
            StartX = startX,
            StartY = startY,
            EndX = endX,
            EndY = endY,
            IsDashed = ShapeGeometryClassifier.IsDashedLine(cxnSp.ShapeProperties?.GetFirstChild<Outline>()),
        };
    }

    private static string ExtractText(DwgSheet.Shape sp)
    {
        // xdr:txBody を取得 (Drawing.Spreadsheet.TextBody)
        var textBody = sp.GetFirstChild<DwgSheet.TextBody>();
        if (textBody == null)
        {
            return string.Empty;
        }

        return string.Join("\n", textBody.Elements<Paragraph>()
            .Select(p => string.Concat(
                p.Elements<Run>().Select(r => r.Text?.Text ?? string.Empty))))
            .Trim();
    }

    private static (int col, long colOff, int row, long rowOff) ReadMarker(MarkerType? marker)
    {
        if (marker == null)
        {
            return (0, 0, 0, 0);
        }
        return (
            int.TryParse(marker.ColumnId?.Text, out var c) ? c : 0,
            long.TryParse(marker.ColumnOffset?.Text, out var co) ? co : 0,
            int.TryParse(marker.RowId?.Text, out var r) ? r : 0,
            long.TryParse(marker.RowOffset?.Text, out var ro) ? ro : 0
        );
    }

}
