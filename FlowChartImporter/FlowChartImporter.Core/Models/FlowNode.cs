namespace FlowChartImporter.Core.Models;

public class FlowNode
{
    public string Id { get; set; }

    /// <summary>採番ロジックで付与される表示用番号</summary>
    public int Number { get; set; }

    public string Text { get; set; }

    public ShapeType ShapeType { get; set; }

    /// <summary>左端列の部署名から判定した担当部署。未判定の場合は空文字</summary>
    public string Department { get; set; }

    public Position? Position { get; set; }

    /// <summary>図形の左下に重なる書類シェイプのテキスト(入力ファイル)</summary>
    public List<string> InputFiles { get; set; }

    /// <summary>図形の右下に重なる書類シェイプのテキスト(出力ファイル)</summary>
    public List<string> OutputFiles { get; set; }

    public FlowNode(string id)
    {
        Id = id;
        Text = string.Empty;
        Department = string.Empty;
        InputFiles = [];
        OutputFiles = [];
    }
}
