using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// YES/NO判定用ではないテキストボックスを、最も近いノード(検索範囲内)の備考として紐づける。
/// </summary>
internal class RemarkAssociator
{
    public void Associate(
        IReadOnlyList<ShapeInfo> remarkTextBoxes,
        IReadOnlyList<(ShapeInfo Shape, FlowNode Node)> nodeMap,
        double searchRadiusPoints)
    {
        foreach (var textBox in remarkTextBoxes)
        {
            var text = textBox.Text.Trim();
            if (text.Length == 0) continue;

            FlowNode? nearestNode = null;
            var bestDist = searchRadiusPoints;

            foreach (var (shape, node) in nodeMap)
            {
                var dist = Distance(textBox.CenterX, textBox.CenterY, shape.CenterX, shape.CenterY);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    nearestNode = node;
                }
            }

            nearestNode?.Remarks.Add(text);
        }
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
