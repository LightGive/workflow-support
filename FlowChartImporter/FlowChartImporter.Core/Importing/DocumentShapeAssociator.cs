using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

internal class DocumentShapeAssociator
{
    /// <summary>
    /// 書類シェイプを親ノードに紐づけ、InputFiles / OutputFiles に振り分ける。
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

            // 親ノードの中心より左→入力、右→出力
            bool isLeft = doc.CenterX < parent.Shape.CenterX;
            var text = doc.Text;

            if (isLeft)
            {
                parent.Node.InputFiles.Add(text);
            }
            else
            {
                parent.Node.OutputFiles.Add(text);
            }
        }
    }

    private static bool Overlaps(ShapeInfo a, ShapeInfo b) =>
        a.Left < b.Right && a.Right > b.Left &&
        a.Top < b.Bottom && a.Bottom > b.Top;
}
