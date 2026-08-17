using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// 候補図形(書類シェイプ、「[」図形等)を、最も近いノード(検索範囲内)に紐づける。
/// バウンディングボックスの重なりでは判定しない。候補図形は目視ではプロセスのすぐ近くに
/// 置かれていても、バウンディングボックスが実際には重なっていないことが多いため、
/// 中心間距離による最近傍探索に統一している。
/// </summary>
internal static class NearestNodeAssociator
{
    /// <summary>
    /// candidates の各図形について、selectValue で取り出した値を最も近いノードに addValue で追加する。
    /// selectValue が null または空文字を返した候補は無視する。
    /// </summary>
    public static void Associate(
        IEnumerable<ShapeInfo> candidates,
        IReadOnlyList<(ShapeInfo Shape, FlowNode Node)> nodeMap,
        double searchRadiusPoints,
        Func<ShapeInfo, string?> selectValue,
        Action<FlowNode, string> addValue)
    {
        foreach (var candidate in candidates)
        {
            var value = selectValue(candidate);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var nearestNode = FindNearest(candidate, nodeMap, searchRadiusPoints);
            if (nearestNode != null)
            {
                addValue(nearestNode, value);
            }
        }
    }

    private static FlowNode? FindNearest(
        ShapeInfo candidate,
        IReadOnlyList<(ShapeInfo Shape, FlowNode Node)> nodeMap,
        double searchRadiusPoints)
    {
        FlowNode? nearestNode = null;
        var bestDist = searchRadiusPoints;

        foreach (var (shape, node) in nodeMap)
        {
            var dist = GeometryUtils.Distance(candidate.CenterX, candidate.CenterY, shape.CenterX, shape.CenterY);
            if (dist <= bestDist)
            {
                bestDist = dist;
                nearestNode = node;
            }
        }

        return nearestNode;
    }
}
