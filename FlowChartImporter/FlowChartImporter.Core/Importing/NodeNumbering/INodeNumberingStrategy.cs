using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing.NodeNumbering;

public interface INodeNumberingStrategy
{
    void AssignNumbers(IList<FlowNode> nodes);
}
