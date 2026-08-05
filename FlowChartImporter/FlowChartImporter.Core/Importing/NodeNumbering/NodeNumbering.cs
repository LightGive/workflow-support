using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing.NodeNumbering;

public interface INodeNumberingStrategy
{
    void AssignNumbers(IList<FlowNode> nodes);
}

public static class NodeNumberingStrategyFactory
{
    public static INodeNumberingStrategy Create(string strategyName) =>
        strategyName.ToLowerInvariant() switch
        {
            "default" or "" => new DefaultNodeNumberingStrategy(),
            _ => throw new NotSupportedException($"Unknown node numbering strategy: '{strategyName}'"),
        };
}

/// <summary>
/// X座標(図形の左上を基準点とした位置)昇順(左→右)で採番し、
/// 同じX座標の場合はY座標昇順(上→下)で採番する。一番左を0番とする。
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
            ordered[i].Number = i;

        int next = ordered.Count;
        foreach (var node in nodes.Where(n => n.Position == null))
            node.Number = next++;
    }
}
