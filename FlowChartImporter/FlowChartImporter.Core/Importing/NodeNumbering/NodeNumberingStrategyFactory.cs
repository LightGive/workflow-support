namespace FlowChartImporter.Core.Importing.NodeNumbering;

public static class NodeNumberingStrategyFactory
{
    public static INodeNumberingStrategy Create(string strategyName) =>
        strategyName.ToLowerInvariant() switch
        {
            "default" or "" => new DefaultNodeNumberingStrategy(),
            _ => throw new NotSupportedException($"Unknown node numbering strategy: '{strategyName}'"),
        };
}
