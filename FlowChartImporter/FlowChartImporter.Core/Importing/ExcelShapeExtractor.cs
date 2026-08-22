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
    // OOXML の a:xfrm の rot 属性の単位(1度 = 60,000)
    private const double RotationUnitsPerDegree = 60000.0;

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
    /// セルアンカー(from/toマーカー)から求めた図形・コネクタのボックス(ポイント単位)。
    /// アンカーが無い場合(グループ内の子図形など)はUnknown(全てNaN)を使う。
    /// OneCellAnchorのようにサイズ(Right/Bottom)だけ不明な場合はHasSizeがfalseになる。
    /// </summary>
    private readonly record struct AnchorBox(double Left, double Top, double Right, double Bottom)
    {
        public static readonly AnchorBox Unknown = new(double.NaN, double.NaN, double.NaN, double.NaN);

        public bool HasPosition => !double.IsNaN(Left);
        public bool HasSize => !double.IsNaN(Right);
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

            // セルアンカーベースの座標(行列番号用、かつ図形・コネクタの正式な位置として使う)
            var anchorBox = new AnchorBox(
                Left: dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu),
                Top: dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu),
                Right: dimMap.GetColumnLeft(toCol) + SheetDimensionMap.EmuToPt(toColOffEmu),
                Bottom: dimMap.GetRowTop(toRow) + SheetDimensionMap.EmuToPt(toRowOffEmu));

            // xdr:cxnSp (明示的なコネクタ)。TwoCellAnchorのみに現れる(OneCellAnchorでは扱わない)。
            var cxnSp = anchor.GetFirstChild<DwgSheet.ConnectionShape>();
            if (cxnSp != null)
            {
                var info = ExtractConnector(cxnSp, GroupTransform.Identity, anchorBox);
                if (info != null)
                {
                    connectors.Add(info);
                }
                continue;
            }

            ProcessShapeOrGroupAnchor(anchor, fromRow, fromCol, toRow, toCol, anchorBox, shapes, connectors, warnings);
        }

        foreach (var anchor in wsDr.Elements<OneCellAnchor>())
        {
            var (fromCol, fromColOffEmu, fromRow, fromRowOffEmu) = ReadMarker(anchor.FromMarker);

            if (IsIgnoredRow(fromRow))
            {
                continue;
            }

            // OneCellAnchor には "to" マーカーが無く終点セルからサイズを求められないため、
            // サイズ(Right/Bottom)は不明(NaN)のまま渡し、a:xfrm のExtentsにフォールバックする
            // (ExtractShape → ResolveShapePosition内で処理)。
            var anchorBox = new AnchorBox(
                Left: dimMap.GetColumnLeft(fromCol) + SheetDimensionMap.EmuToPt(fromColOffEmu),
                Top: dimMap.GetRowTop(fromRow) + SheetDimensionMap.EmuToPt(fromRowOffEmu),
                Right: double.NaN,
                Bottom: double.NaN);

            ProcessShapeOrGroupAnchor(anchor, fromRow, fromCol, fromRow, fromCol, anchorBox, shapes, connectors, warnings);
        }

        return (shapes, connectors, warnings);
    }

    /// <summary>
    /// TwoCellAnchor/OneCellAnchorの両方で共通の、xdr:sp(通常シェイプ)・xdr:grpSp(グループ)の
    /// 処理を行う(xdr:cxnSpの扱いはTwoCellAnchorのみ異なるため、呼び出し側で個別に処理する)。
    /// </summary>
    private void ProcessShapeOrGroupAnchor(
        OpenXmlElement anchor,
        int fromRow, int fromCol, int toRow, int toCol,
        AnchorBox anchorBox,
        List<ShapeInfo> shapes, List<ConnectorInfo> connectors, List<string> warnings)
    {
        var sp = anchor.GetFirstChild<DwgSheet.Shape>();
        if (sp != null)
        {
            var info = ExtractShape(sp, GroupTransform.Identity, anchorBox, fromRow, fromCol, toRow, toCol, warnings);
            if (info != null)
            {
                shapes.Add(info);
            }
            return;
        }

        var grpSp = anchor.GetFirstChild<DwgSheet.GroupShape>();
        if (grpSp != null)
        {
            ProcessGroupShape(grpSp, GroupTransform.Identity, fromRow, fromCol, toRow, toCol, shapes, connectors, warnings);
        }
    }

    /// <summary>
    /// グループ図形の中身(通常シェイプ・コネクタ・入れ子のグループ)を再帰的に処理し、
    /// 子図形固有の座標系を親の変換と合成した絶対座標系に変換する。
    /// </summary>
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
            // グループ内の子図形には独自のアンカー(セル位置)が無いため、フォールバック座標は渡せない。
            // a:xfrm が無い図形は位置不明として ExtractShape 内で除外される。
            var info = ExtractShape(sp, transform, AnchorBox.Unknown, fromRow, fromCol, toRow, toCol, warnings);
            if (info != null)
            {
                shapes.Add(info);
            }
        }

        foreach (var cxnSp in grpSp.Elements<DwgSheet.ConnectionShape>())
        {
            var info = ExtractConnector(cxnSp, transform, AnchorBox.Unknown);
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
        AnchorBox anchorBox,
        int fromRow, int fromCol, int toRow, int toCol,
        List<string> warnings)
    {
        var cNvPr = sp.NonVisualShapeProperties?.NonVisualDrawingProperties;
        if (cNvPr?.Id?.Value == null)
        {
            warnings.Add($"ID のないシェイプをスキップしました (name={cNvPr?.Name?.Value})");
            return null;
        }

        var xfrm = sp.ShapeProperties?.Transform2D;
        bool flipH = xfrm?.HorizontalFlip?.Value ?? false;
        bool flipV = xfrm?.VerticalFlip?.Value ?? false;

        var position = ResolveShapePosition(xfrm, transform, anchorBox);
        if (position == null)
        {
            // アンカーも a:xfrm も無い場合は、位置不明の図形として読み飛ばす。
            warnings.Add($"位置不明のシェイプをスキップしました (name={cNvPr.Name?.Value})");
            return null;
        }
        var (left, top, width, height) = position.Value;

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

    /// <summary>
    /// シェイプの位置・サイズを求める。セルアンカーがあれば(from/to のセル位置+列幅・行高から求めた値を)
    /// 正式なものとして使う。a:xfrm の off/ext はExcelが実際の描画に使わないキャッシュ値でズレることが
    /// あるため、セルアンカーが無い場合(グループ内の子図形、またはOneCellAnchorのサイズ)のみ使う。
    /// アンカーも a:xfrm も無ければ位置不明としてnullを返す。
    /// </summary>
    private static (double Left, double Top, double Width, double Height)? ResolveShapePosition(
        Transform2D? xfrm, GroupTransform transform, AnchorBox anchorBox)
    {
        if (anchorBox.HasPosition)
        {
            double width, height;
            if (anchorBox.HasSize)
            {
                width = anchorBox.Right - anchorBox.Left;
                height = anchorBox.Bottom - anchorBox.Top;
            }
            else if (xfrm?.Extents != null)
            {
                width = SheetDimensionMap.EmuToPt(transform.ScaleLengthX(xfrm.Extents.Cx?.Value ?? 0));
                height = SheetDimensionMap.EmuToPt(transform.ScaleLengthY(xfrm.Extents.Cy?.Value ?? 0));
            }
            else
            {
                width = 0;
                height = 0;
            }
            return (anchorBox.Left, anchorBox.Top, width, height);
        }

        if (xfrm?.Offset != null && xfrm.Extents != null)
        {
            double left = SheetDimensionMap.EmuToPt(transform.TransformX(xfrm.Offset.X?.Value ?? 0));
            double top = SheetDimensionMap.EmuToPt(transform.TransformY(xfrm.Offset.Y?.Value ?? 0));
            double width = SheetDimensionMap.EmuToPt(transform.ScaleLengthX(xfrm.Extents.Cx?.Value ?? 0));
            double height = SheetDimensionMap.EmuToPt(transform.ScaleLengthY(xfrm.Extents.Cy?.Value ?? 0));
            return (left, top, width, height);
        }

        return null;
    }

    /// <summary>
    /// コネクタのバウンディングボックスの対角線を始点・終点とする。ボックスの位置・サイズは、
    /// ResolveShapePositionと同様の理由でセルアンカー(from/to)から求めた値を正式なものとして使う。
    /// どちらの端点が始点になるかは a:xfrm の flipH/flipV(あれば)で決める。
    /// </summary>
    private static ConnectorInfo? ExtractConnector(
        DwgSheet.ConnectionShape cxnSp, GroupTransform transform, AnchorBox fallbackBox)
    {
        var cNvPr = cxnSp.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties;
        if (cNvPr?.Id?.Value == null)
        {
            return null;
        }

        var xfrm = cxnSp.ShapeProperties?.Transform2D;
        bool flipH = xfrm?.HorizontalFlip?.Value ?? false;
        bool flipV = xfrm?.VerticalFlip?.Value ?? false;

        // セルアンカーは回転・反転が既に反映された見た目のボックスを表すため rot は適用しない
        // (適用すると回転済み座標を二重に回転させてしまう)。アンカーが無い場合(グループ内の子コネクタ等)は
        // a:xfrm の off/ext(回転前のローカル座標系)をボックスとして使う。
        var box = ResolveConnectorBox(xfrm, transform, fallbackBox);
        if (box == null)
        {
            // アンカーも a:xfrm も無い場合は、位置不明のコネクタとして読み飛ばす。
            return null;
        }
        var (left, top, right, bottom) = box.Value;

        var (startX, endX) = flipH ? (right, left) : (left, right);
        var (startY, endY) = flipV ? (bottom, top) : (top, bottom);

        // rot(60,000分の1度単位、時計回りが正)は、a:xfrm のボックスを使った場合のみ、
        // 反転後のボックスをその中心まわりに回転させて実際の見た目の座標に補正する。
        bool usedXfrmBox = !fallbackBox.HasPosition;
        int rotation60000ths = usedXfrmBox ? (xfrm?.Rotation?.Value ?? 0) : 0;
        if (rotation60000ths != 0)
        {
            double centerX = (left + right) / 2;
            double centerY = (top + bottom) / 2;
            double angleRad = rotation60000ths / RotationUnitsPerDegree * (Math.PI / 180.0);
            (startX, startY) = RotatePoint(startX, startY, centerX, centerY, angleRad);
            (endX, endY) = RotatePoint(endX, endY, centerX, centerY, angleRad);
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
            StartConnectionSiteIndex = stCxn?.Index?.Value,
            EndConnectionSiteIndex = endCxn?.Index?.Value,
            StartX = startX,
            StartY = startY,
            EndX = endX,
            EndY = endY,
            IsDashed = ShapeGeometryClassifier.IsDashedLine(cxnSp.ShapeProperties?.GetFirstChild<Outline>()),
        };
    }

    private static (double Left, double Top, double Right, double Bottom)? ResolveConnectorBox(
        Transform2D? xfrm, GroupTransform transform, AnchorBox fallbackBox)
    {
        if (fallbackBox.HasPosition)
        {
            return (fallbackBox.Left, fallbackBox.Top, fallbackBox.Right, fallbackBox.Bottom);
        }

        if (xfrm?.Offset != null && xfrm.Extents != null)
        {
            double left = SheetDimensionMap.EmuToPt(transform.TransformX(xfrm.Offset.X?.Value ?? 0));
            double top = SheetDimensionMap.EmuToPt(transform.TransformY(xfrm.Offset.Y?.Value ?? 0));
            double right = left + SheetDimensionMap.EmuToPt(transform.ScaleLengthX(xfrm.Extents.Cx?.Value ?? 0));
            double bottom = top + SheetDimensionMap.EmuToPt(transform.ScaleLengthY(xfrm.Extents.Cy?.Value ?? 0));
            return (left, top, right, bottom);
        }

        return null;
    }

    /// <summary>点(x,y)を中心(centerX,centerY)まわりに angleRad(時計回り、Y軸下向き前提)だけ回転させる。</summary>
    private static (double X, double Y) RotatePoint(double x, double y, double centerX, double centerY, double angleRad)
    {
        double dx = x - centerX;
        double dy = y - centerY;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        return (centerX + dx * cos - dy * sin, centerY + dx * sin + dy * cos);
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
