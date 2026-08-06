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

    /// <summary>OOXML上の txBox="1" (Excelの「テキストボックス」挿入機能で作られた図形)かどうか</summary>
    public bool IsTextBox { get; init; }

    /// <summary>図形の枠線 (a:ln) が明示的に noFill (線なし) に設定されているかどうか</summary>
    public bool HasNoLine { get; init; }

    /// <summary>線種(a:prstDash)が実線以外(点線・破線など)かどうか</summary>
    public bool IsDashed { get; init; }

    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    // セルベースのアンカー位置(0始まり)
    public int AnchorFromRow { get; init; }
    public int AnchorFromCol { get; init; }
    public int AnchorToRow { get; init; }
    public int AnchorToCol { get; init; }
}

internal class ConnectorInfo
{
    public required uint XmlId { get; init; }

    // XML上の明示的な接続先シェイプID (null = XMLに記載なし)
    public uint? StartShapeXmlId { get; init; }
    public uint? EndShapeXmlId { get; init; }

    // 始点・終点座標(ポイント単位、座標近接フォールバック用)
    public double StartX { get; init; }
    public double StartY { get; init; }
    public double EndX { get; init; }
    public double EndY { get; init; }

    // コネクタ(矢印)自体に書かれたテキスト(分岐の "Y"/"N" 等)
    public string? Label { get; init; }

    // 線種(a:prstDash)が実線以外(点線・破線など)かどうか。
    // データのやり取りを表す線であり、フローとしては扱わない。
    public bool IsDashed { get; init; }
}
