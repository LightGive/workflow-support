using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing.NodeNumbering;

/// <summary>
/// X座標昇順(左→右)で採番し、同じ列グループ内はY座標昇順(上→下)で採番する。
/// </summary>
public class DefaultNodeNumberingStrategy : INodeNumberingStrategy
{
    public void AssignNumbers(IList<FlowNode> nodes)
    {
        var ordered = nodes
            .Where(n => n.Position != null)
            .OrderBy(n => n.Position!.X)
            .ThenBy(n => n.Position!.Y)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Number = i + 1;

        int next = ordered.Count + 1;
        foreach (var node in nodes.Where(n => n.Position == null))
            node.Number = next++;
    }
}
