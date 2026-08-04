using System.Text.Json.Serialization;

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

public class FlowNode
{
    public string Id { get; set; }

    /// <summary>採番ロジックで付与される表示用番号</summary>
    public int Number { get; set; }

    public string Text { get; set; }

    public ShapeType ShapeType { get; set; }

    /// <summary>左端列の部署名から判定した担当部署。複数部署をまたぐ図形の場合は複数件になる</summary>
    public List<string> Departments { get; set; }

    public Position? Position { get; set; }

    /// <summary>図形の左下に重なる書類シェイプのテキスト(入力ファイル)</summary>
    public List<string> InputFiles { get; set; }

    /// <summary>図形の右下に重なる書類シェイプのテキスト(出力ファイル)</summary>
    public List<string> OutputFiles { get; set; }

    /// <summary>近くにあるテキストボックス(YES/NO判定用を除く)の内容</summary>
    public List<string> Remarks { get; set; }

    public FlowNode(string id)
    {
        Id = id;
        Text = string.Empty;
        Departments = [];
        InputFiles = [];
        OutputFiles = [];
        Remarks = [];
    }
}

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

public class Position
{
    /// <summary>シート上の行インデックス(1始まり)</summary>
    public int Row { get; set; }

    /// <summary>シート上の列インデックス(1始まり)</summary>
    public int Column { get; set; }

    /// <summary>EMU単位のX座標</summary>
    public double X { get; set; }

    /// <summary>EMU単位のY座標</summary>
    public double Y { get; set; }

    public Position(int row, int column, double x, double y)
    {
        Row = row;
        Column = column;
        X = x;
        Y = y;
    }
}

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
    Bracket,
    Other,
}

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
