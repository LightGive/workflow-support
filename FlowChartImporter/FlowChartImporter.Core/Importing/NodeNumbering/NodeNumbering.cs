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
/// 図形のアンカー列番号昇順(左→右)で採番する。同じ列の場合はアンカー行番号昇順(上→下)で
/// 採番する。一番左を0番とする。
/// DB(データストア)シェイプはCSVには出力されない(業務フローの処理ではない)ため、
/// この主採番からは除外し、残りの番号を割り振った後に続き番号を割り振るだけにする。
/// </summary>
public class DefaultNodeNumberingStrategy : INodeNumberingStrategy
{
    public void AssignNumbers(IList<FlowNode> nodes)
    {
        var numberedNodes = nodes.Where(n => n.ShapeType != ShapeType.Database).ToList();

        var ordered = numberedNodes
            .Where(n => n.Position != null)
            .OrderBy(n => n.Position!.Column)
            .ThenBy(n => n.Position!.Row)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Number = i;

        int next = ordered.Count;
        foreach (var node in numberedNodes.Where(n => n.Position == null))
            node.Number = next++;

        foreach (var node in nodes.Where(n => n.ShapeType == ShapeType.Database))
            node.Number = next++;
    }
}
