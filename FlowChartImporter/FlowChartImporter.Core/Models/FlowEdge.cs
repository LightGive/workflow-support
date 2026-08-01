using System.Text.Json.Serialization;

namespace FlowChartImporter.Core.Models;

public class FlowEdge
{
    public string Id { get; set; }

    [JsonPropertyName("from")]
    public string FromNodeId { get; set; }

    [JsonPropertyName("to")]
    public string ToNodeId { get; set; }

    public FlowEdge(string id, string fromNodeId, string toNodeId)
    {
        Id = id;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
    }
}
