using System.Text.Json.Serialization;

namespace FlowChartImporter.Core.Models;

public class FlowEdge
{
    public string Id { get; set; }

    [JsonPropertyName("from")]
    public string FromNodeId { get; set; }

    [JsonPropertyName("to")]
    public string ToNodeId { get; set; }

    /// <summary>コネクタ(矢印)自体に書かれたテキスト(分岐の "Y"/"N" 等)。未設定時はnull</summary>
    public string? Label { get; set; }

    public FlowEdge(string id, string fromNodeId, string toNodeId, string? label = null)
    {
        Id = id;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Label = label;
    }
}
