using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// 書類シェイプを、最も近いノード(検索範囲内)の関連ファイルとして紐づける。
/// (RemarkAssociator と同じ「最も近いノードを探す」方式。書類シェイプは目視ではプロセスの
/// すぐ近くに置かれていても、バウンディングボックスが実際には重なっていないことが多いため)
/// </summary>
internal class DocumentShapeAssociator
{
    /// <summary>
    /// 書類シェイプを親ノードに紐づけ、RelatedFiles に追加する。
    /// </summary>
    public void Associate(
        IEnumerable<ShapeInfo> documentShapes,
        IReadOnlyList<(ShapeInfo Shape, FlowNode Node)> nodeMap,
        double searchRadiusPoints)
    {
        foreach (var doc in documentShapes)
        {
            FlowNode? nearestNode = null;
            var bestDist = searchRadiusPoints;

            foreach (var (shape, node) in nodeMap)
            {
                var dist = Distance(doc.CenterX, doc.CenterY, shape.CenterX, shape.CenterY);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    nearestNode = node;
                }
            }

            nearestNode?.RelatedFiles.Add(doc.Text);
        }
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
