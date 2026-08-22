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
    public List<ResolvedConnection> Resolve(
        IEnumerable<ConnectorInfo> connectors,
        IList<ShapeInfo> nodeShapes,
        IReadOnlyDictionary<uint, string> xmlIdToNodeId)
    {
        var result = new List<ResolvedConnection>();
        var shapeByXmlId = nodeShapes.ToDictionary(s => s.XmlId);

        foreach (var connector in connectors)
        {
            var fromNodeId = ResolveEnd(connector.StartShapeXmlId, connector.StartX, connector.StartY,
                nodeShapes, xmlIdToNodeId);
            var toNodeId = ResolveEnd(connector.EndShapeXmlId, connector.EndX, connector.EndY,
                nodeShapes, xmlIdToNodeId);

            if (fromNodeId != null && toNodeId != null)
            {
                var (startX, startY) = RefineEndpoint(
                    connector.StartShapeXmlId, connector.StartConnectionSiteIndex,
                    connector.StartX, connector.StartY, shapeByXmlId);
                var (endX, endY) = RefineEndpoint(
                    connector.EndShapeXmlId, connector.EndConnectionSiteIndex,
                    connector.EndX, connector.EndY, shapeByXmlId);

                result.Add(new ResolvedConnection(fromNodeId, toNodeId, connector.Label, startX, startY, endX, endY));
            }
        }

        return result;
    }

    /// <summary>
    /// 接続先シェイプ・接続サイト番号(a:stCxn/a:endCxnのidx。基本図形では
    /// 0=上辺中点, 1=右辺中点, 2=下辺中点, 3=左辺中点)が判明している場合は、コネクタの
    /// バウンディングボックス対角線からの近似座標ではなく、実際の接続サイト座標を使う
    /// (対角線での近似は、カギ型接続線が迂回する経路だと実際の接続位置からズレるため)。
    /// </summary>
    private static (double X, double Y) RefineEndpoint(
        uint? shapeXmlId, uint? siteIndex, double fallbackX, double fallbackY,
        IReadOnlyDictionary<uint, ShapeInfo> shapeByXmlId)
    {
        if (shapeXmlId.HasValue && siteIndex.HasValue
            && shapeByXmlId.TryGetValue(shapeXmlId.Value, out var shape))
        {
            return siteIndex.Value switch
            {
                0 => (shape.CenterX, shape.Top),
                1 => (shape.Left, shape.CenterY),
                2 => (shape.CenterX, shape.Bottom),
                3 => (shape.Right, shape.CenterY),
                _ => (fallbackX, fallbackY),
            };
        }
        return (fallbackX, fallbackY);
    }

    private string? ResolveEnd(
        uint? explicitXmlId, double x, double y,
        IList<ShapeInfo> nodeShapes,
        IReadOnlyDictionary<uint, string> xmlIdToNodeId)
    {
        // 1. XML明示接続
        if (explicitXmlId.HasValue && xmlIdToNodeId.TryGetValue(explicitXmlId.Value, out var nodeId))
        {
            return nodeId;
        }

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

        if (nearest == null)
        {
            return null;
        }
        return xmlIdToNodeId.TryGetValue(nearest.XmlId, out var id) ? id : null;
    }
}
