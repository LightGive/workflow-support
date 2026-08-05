using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

internal class DocumentShapeAssociator
{
    /// <summary>
    /// 書類シェイプを親ノードに紐づけ、RelatedFiles に追加する。
    /// </summary>
    public void Associate(
        IEnumerable<ShapeInfo> documentShapes,
        IList<(ShapeInfo Shape, FlowNode Node)> nodeMap)
    {
        foreach (var doc in documentShapes)
        {
            // バウンディングボックスが重なるノードを探す
            var parent = nodeMap.FirstOrDefault(m => Overlaps(doc, m.Shape));
            if (parent.Node == null)
            {
                continue;
            }

            parent.Node.RelatedFiles.Add(doc.Text);
        }
    }

    private static bool Overlaps(ShapeInfo a, ShapeInfo b) =>
        a.Left < b.Right && a.Right > b.Left &&
        a.Top < b.Bottom && a.Bottom > b.Top;
}
