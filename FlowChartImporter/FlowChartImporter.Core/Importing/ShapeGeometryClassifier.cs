using DocumentFormat.OpenXml.Drawing;

namespace FlowChartImporter.Core.Importing;

// OOXMLの図形プロパティ(プリセット図形名・自由図形パス・線種)から、
// このアプリで扱う ShapeType や付随する判定結果を求める。
internal static class ShapeGeometryClassifier
{
    // OOXML プリセット図形名 (a:prstGeom の prst 属性値)
    private const string PresetRectangle = "rect";
    private const string PresetFlowChartProcess = "flowChartProcess";
    private const string PresetFlowChartAlternateProcess = "flowChartAlternateProcess";
    private const string PresetRoundRectangle = "roundRect";
    private const string PresetFlowChartDecision = "flowChartDecision";
    private const string PresetDiamond = "diamond";
    private const string PresetEllipse = "ellipse";
    private const string PresetFlowChartConnector = "flowChartConnector";
    private const string PresetFlowChartDocument = "flowChartDocument";
    private const string PresetFoldedCorner = "foldedCorner";
    private const string PresetParallelogram = "parallelogram";
    private const string PresetLeftBracket = "leftBracket";
    private const string PresetRightBracket = "rightBracket";
    private const string PresetFlowChartMagneticDisk = "flowChartMagneticDisk";
    private const string PresetLine = "line";
    private const string PresetStraightConnector1 = "straightConnector1";
    private const string PresetBentConnector2 = "bentConnector2";
    private const string PresetBentConnector3 = "bentConnector3";
    private const string PresetBentConnector4 = "bentConnector4";
    private const string PresetBentConnector5 = "bentConnector5";

    // OOXML 線種名 (a:prstDash の val 属性値)
    private const string DashStyleSolid = "solid";

    // OpenXML 3.x の ShapeTypeValues は定数として扱えないため InnerText で文字列比較する
    public static Models.ShapeType MapPreset(string? preset) => preset switch
    {
        PresetRectangle or PresetFlowChartProcess or PresetFlowChartAlternateProcess or PresetRoundRectangle
            => Models.ShapeType.Rectangle,
        PresetFlowChartDecision or PresetDiamond => Models.ShapeType.Diamond,
        PresetEllipse or PresetFlowChartConnector => Models.ShapeType.Ellipse,
        PresetFlowChartDocument or PresetFoldedCorner => Models.ShapeType.Document,
        PresetParallelogram => Models.ShapeType.Parallelogram,
        PresetLeftBracket or PresetRightBracket => Models.ShapeType.Bracket,
        PresetFlowChartMagneticDisk => Models.ShapeType.Database,
        PresetLine or PresetStraightConnector1
            or PresetBentConnector2 or PresetBentConnector3 or PresetBentConnector4 or PresetBentConnector5
            => Models.ShapeType.Line,
        null or "" => Models.ShapeType.Unknown,
        _ => Models.ShapeType.Other,
    };

    // 自由図形(a:custGeom)のパスが、上下左右の中点を頂点とするひし形かどうかを判定する。
    // 「フローチャート: 判断」プリセットが custGeom に変換保存されているケースを検出するために使う。
    public static bool IsDiamondPath(CustomGeometry? custGeom)
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
    public static bool IsDashedLine(Outline? outline)
    {
        var dashVal = outline?.GetFirstChild<PresetDash>()?.Val?.InnerText;
        return !string.IsNullOrEmpty(dashVal) && dashVal != DashStyleSolid;
    }
}
