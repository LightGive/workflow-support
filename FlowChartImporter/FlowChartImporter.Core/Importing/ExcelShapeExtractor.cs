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

    public (List<ShapeInfo> Shapes, List<ConnectorInfo> Connectors, List<string> Warnings)
        Extract(WorksheetPart worksheetPart)
    {
        var shapes = new List<ShapeInfo>();
        var connectors = new List<ConnectorInfo>();
        var warnings = new List<string>();

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

            // セルアンカーベースの座標(行列番号用)
            double anchorLeft = dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu);
            double anchorTop = dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu);

            // xdr:sp (通常シェイプ)
            var sp = anchor.GetFirstChild<DwgSheet.Shape>();
            if (sp != null)
            {
                var info = ExtractShape(sp, anchorLeft, anchorTop, fromRow, fromCol, toRow, toCol, warnings);
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
                var info = ExtractExplicitConnector(cxnSp, anchorLeft, anchorTop,
                    dimMap.GetColumnLeft(toCol) + SheetDimensionMap.EmuToPt(toColOffEmu),
                    dimMap.GetRowTop(toRow) + SheetDimensionMap.EmuToPt(toRowOffEmu));
                if (info != null)
                {
                    connectors.Add(info);
                }
            }
        }

        foreach (var anchor in wsDr.Elements<OneCellAnchor>())
        {
            var (fromCol, fromColOffEmu, fromRow, fromRowOffEmu) = ReadMarker(anchor.FromMarker);
            double anchorLeft = dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu);
            double anchorTop = dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu);

            var sp = anchor.GetFirstChild<DwgSheet.Shape>();
            if (sp != null)
            {
                var info = ExtractShape(sp, anchorLeft, anchorTop, fromRow, fromCol, fromRow, fromCol, warnings);
                if (info != null)
                {
                    shapes.Add(info);
                }
            }
        }

        return (shapes, connectors, warnings);
    }

    private static ShapeInfo? ExtractShape(
        DwgSheet.Shape sp,
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
            left = (xfrm.Offset.X?.Value ?? 0) * EmuToPt;
            top = (xfrm.Offset.Y?.Value ?? 0) * EmuToPt;
            width = (xfrm.Extents.Cx?.Value ?? 0) * EmuToPt;
            height = (xfrm.Extents.Cy?.Value ?? 0) * EmuToPt;
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
        var shapeType = MapPreset(preset);
        if (shapeType == Models.ShapeType.Unknown)
        {
            // 一部のファイルでは「フローチャート: 判断」(ひし形)がプリセットではなく
            // 自由図形(a:custGeom)のひし形パスとして保存されているため、その形状も判定する。
            var custGeom = sp.ShapeProperties?.GetFirstChild<CustomGeometry>();
            if (IsDiamondPath(custGeom))
            {
                shapeType = Models.ShapeType.Diamond;
            }
        }
        var text = ExtractText(sp);
        var isTextBox = sp.NonVisualShapeProperties?.NonVisualShapeDrawingProperties?.TextBox?.Value ?? false;
        var isElbowConnector = preset is "bentConnector2" or "bentConnector3" or "bentConnector4" or "bentConnector5";
        var outline = sp.ShapeProperties?.GetFirstChild<Outline>();
        var hasNoLine = outline?.GetFirstChild<NoFill>() != null;
        var isDashed = IsDashedLine(outline);

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
            IsElbowConnector = isElbowConnector,
            HasNoLine = hasNoLine,
            IsDashed = isDashed,
        };
    }

    private static ConnectorInfo? ExtractExplicitConnector(
        DwgSheet.ConnectionShape cxnSp,
        double startX, double startY, double endX, double endY)
    {
        var cNvPr = cxnSp.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties;
        if (cNvPr?.Id?.Value == null)
        {
            return null;
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
            IsDashed = IsDashedLine(cxnSp.ShapeProperties?.GetFirstChild<Outline>()),
        };
    }

    // 自由図形(a:custGeom)のパスが、上下左右の中点を頂点とするひし形かどうかを判定する。
    // 「フローチャート: 判断」プリセットが custGeom に変換保存されているケースを検出するために使う。
    private static bool IsDiamondPath(CustomGeometry? custGeom)
    {
        var path = custGeom?.GetFirstChild<PathList>()?.Elements<DocumentFormat.OpenXml.Drawing.Path>().FirstOrDefault();
        if (path == null)
        {
            return false;
        }

        var points = new List<(double X, double Y)>();
        foreach (var child in path.ChildElements)
        {
            Point? pt = child switch
            {
                MoveTo moveTo => moveTo.Point,
                LineTo lineTo => lineTo.Point,
                _ => null,
            };
            if (pt == null)
            {
                continue;
            }
            if (!double.TryParse(pt.X?.Value, out var x) || !double.TryParse(pt.Y?.Value, out var y))
            {
                return false;
            }
            points.Add((x, y));
        }

        var distinct = points.Distinct().ToList();
        if (distinct.Count != 4)
        {
            return false;
        }

        double w = path.Width?.Value ?? 21600;
        double h = path.Height?.Value ?? 21600;
        double midX = w / 2.0, midY = h / 2.0;
        double tolerance = Math.Max(w, h) * 0.05;

        bool HasPointNear(double x, double y) =>
            distinct.Any(p => Math.Abs(p.X - x) <= tolerance && Math.Abs(p.Y - y) <= tolerance);

        return HasPointNear(midX, 0) && HasPointNear(w, midY) && HasPointNear(midX, h) && HasPointNear(0, midY);
    }

    // OpenXML 3.x の PresetLineDashValues は定数として扱えないため InnerText で文字列比較する
    private static bool IsDashedLine(Outline? outline)
    {
        var dashVal = outline?.GetFirstChild<PresetDash>()?.Val?.InnerText;
        return !string.IsNullOrEmpty(dashVal) && dashVal != "solid";
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

    // OpenXML 3.x の ShapeTypeValues は定数として扱えないため InnerText で文字列比較する
    private static Models.ShapeType MapPreset(string? preset) => preset switch
    {
        "rect" or "flowChartProcess" or "flowChartAlternateProcess" or "roundRect"
            => Models.ShapeType.Rectangle,
        "flowChartDecision" => Models.ShapeType.Diamond,
        "ellipse" => Models.ShapeType.Ellipse,
        "flowChartDocument" or "foldedCorner" => Models.ShapeType.Document,
        "parallelogram" => Models.ShapeType.Parallelogram,
        "leftBracket" or "rightBracket" => Models.ShapeType.Bracket,
        "line" or "straightConnector1"
            or "bentConnector2" or "bentConnector3" or "bentConnector4" or "bentConnector5"
            => Models.ShapeType.Line,
        null or "" => Models.ShapeType.Unknown,
        _ => Models.ShapeType.Other,
    };
}
