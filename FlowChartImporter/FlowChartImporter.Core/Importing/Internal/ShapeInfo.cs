using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing.Internal;

internal class ShapeInfo
{
    public required uint XmlId { get; init; }
    public required string Name { get; init; }
    public ShapeType ShapeType { get; init; }
    public string Text { get; init; } = string.Empty;

    // 図形の絶対位置・サイズ(ポイント単位)
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public bool FlipH { get; init; }
    public bool FlipV { get; init; }

    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    // セルベースのアンカー位置(0始まり)
    public int AnchorFromRow { get; init; }
    public int AnchorFromCol { get; init; }
    public int AnchorToRow { get; init; }
    public int AnchorToCol { get; init; }

    public int CenterAnchorRow => (AnchorFromRow + AnchorToRow) / 2;
}
