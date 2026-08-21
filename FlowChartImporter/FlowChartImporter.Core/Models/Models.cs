using System.Text.Json.Serialization;

namespace FlowChartImporter.Core.Models;

public class FlowChart
{
    public string SchemaVersion { get; set; } = "1.2";

    public string SheetName { get; set; }

    public List<FlowNode> Nodes { get; set; }

    public List<FlowEdge> Edges { get; set; }

    /// <summary>データのやり取りを表す矢印(点線の矢印、およびDB(データストア)シェイプに繋がる矢印)。
    /// 業務フローのエッジ(Edges)とは別に保持する。
    /// 孤立ノード判定・開始/終了到達判定・分岐ラベル判定・CSV出力等、フロー関連の処理では使われない。</summary>
    public List<FlowEdge> DataEdges { get; set; }

    public FlowChart(string sheetName)
    {
        SheetName = sheetName;
        Nodes = [];
        Edges = [];
        DataEdges = [];
    }
}

public class FlowNode
{
    public string Id { get; set; }

    /// <summary>採番ロジックで付与される表示用番号</summary>
    public int Number { get; set; }

    public string Text { get; set; }

    public ShapeType ShapeType { get; set; }

    /// <summary>左端列の名称から判定した実施主体(部署・システム・他社等)。複数をまたぐ図形の場合は複数件になる</summary>
    public List<string> Actors { get; set; }

    public Position? Position { get; set; }

    /// <summary>図形に重なる書類シェイプのテキスト(関連ファイル)</summary>
    public List<string> RelatedFiles { get; set; }

    /// <summary>近くにある「[」(角かっこ)図形の内容</summary>
    public List<string> Remarks { get; set; }

    public FlowNode(string id)
    {
        Id = id;
        Text = string.Empty;
        Actors = [];
        RelatedFiles = [];
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

    /// <summary>
    /// Labelが分岐(ひし形)近くのYES/NOラベル図形から"Y"/"N"に正規化された場合の、
    /// 正規化前の元のテキスト(例: "[ YES ] = 与信確認が完了した場合")。未設定時はnull
    /// (CSV出力では使われず、JSONにのみ保持する)
    /// </summary>
    public string? LabelText { get; set; }

    public FlowEdge(string id, string fromNodeId, string toNodeId, string? label = null, string? labelText = null)
    {
        Id = id;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Label = label;
        LabelText = labelText;
    }
}

public class Position
{
    /// <summary>シート上の行インデックス(1始まり)</summary>
    public int Row { get; set; }

    /// <summary>シート上の列インデックス(1始まり)</summary>
    public int Column { get; set; }

    /// <summary>ポイント単位の幅</summary>
    public double Width { get; set; }

    /// <summary>ポイント単位の高さ</summary>
    public double Height { get; set; }

    public Position(int row, int column, double width, double height)
    {
        Row = row;
        Column = column;
        Width = width;
        Height = height;
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
    Database,
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
