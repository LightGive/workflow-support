namespace FlowChartImporter.Core.Importing.Internal;

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
}
