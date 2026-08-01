using System.Text.Json.Serialization;

namespace FlowChartImporter.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ShapeType>))]
public enum ShapeType
{
    Unknown,
    Rectangle,
    Diamond,
    Ellipse,
    Document,
    Parallelogram,
    Line,
    Other,
}
