namespace FlowChartImporter.Core.Models;

public class FlowChart
{
    public string SchemaVersion { get; set; } = "1.0";

    public string SheetName { get; set; }

    public List<FlowNode> Nodes { get; set; }

    public List<FlowEdge> Edges { get; set; }

    public FlowChart(string sheetName)
    {
        SheetName = sheetName;
        Nodes = [];
        Edges = [];
    }
}
