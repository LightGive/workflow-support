using FlowChartImporter.Core.Importing.Internal;

namespace FlowChartImporter.Core.Importing;

internal class ConnectorResolver
{
    private readonly double _tolerancePt;

    public ConnectorResolver(double tolerancePoints)
    {
        _tolerancePt = tolerancePoints;
    }

    /// <summary>
    /// コネクタをノードIDのペア(from, to)に解決する。
    /// XML明示接続を優先し、なければ座標近接判定にフォールバックする。
    /// </summary>
    public List<(string FromNodeId, string ToNodeId, string? Label)> Resolve(
        IEnumerable<ConnectorInfo> connectors,
        IList<ShapeInfo> nodeShapes,
        IReadOnlyDictionary<uint, string> xmlIdToNodeId)
    {
        var result = new List<(string, string, string?)>();

        foreach (var connector in connectors)
        {
            var fromNodeId = ResolveEnd(connector.StartShapeXmlId, connector.StartX, connector.StartY,
                nodeShapes, xmlIdToNodeId);
            var toNodeId = ResolveEnd(connector.EndShapeXmlId, connector.EndX, connector.EndY,
                nodeShapes, xmlIdToNodeId);

            if (fromNodeId != null && toNodeId != null)
                result.Add((fromNodeId, toNodeId, connector.Label));
        }

        return result;
    }

    private string? ResolveEnd(
        uint? explicitXmlId, double x, double y,
        IList<ShapeInfo> nodeShapes,
        IReadOnlyDictionary<uint, string> xmlIdToNodeId)
    {
        // 1. XML明示接続
        if (explicitXmlId.HasValue && xmlIdToNodeId.TryGetValue(explicitXmlId.Value, out var nodeId))
            return nodeId;

        // 2. 座標近接判定フォールバック
        return FindNearestNode(x, y, nodeShapes, xmlIdToNodeId);
    }

    private string? FindNearestNode(
        double x, double y,
        IList<ShapeInfo> shapes,
        IReadOnlyDictionary<uint, string> xmlIdToNodeId)
    {
        ShapeInfo? nearest = null;
        double minDist = _tolerancePt;

        foreach (var shape in shapes)
        {
            // バウンディングボックスの境界からの距離
            double dx = Math.Max(0, Math.Max(shape.Left - x, x - shape.Right));
            double dy = Math.Max(0, Math.Max(shape.Top - y, y - shape.Bottom));
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < minDist)
            {
                minDist = dist;
                nearest = shape;
            }
        }

        if (nearest == null) return null;
        return xmlIdToNodeId.TryGetValue(nearest.XmlId, out var id) ? id : null;
    }
}
