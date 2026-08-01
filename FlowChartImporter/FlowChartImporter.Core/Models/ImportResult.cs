namespace FlowChartImporter.Core.Models;

public class ImportResult
{
    public FlowChart FlowChart { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ImportResult(FlowChart flowChart, IReadOnlyList<string> warnings)
    {
        FlowChart = flowChart;
        Warnings = warnings;
    }
}
