using System.Text;
using FlowChartImporter.Core.Models;
using FlowChartImporter.Core.Settings;

namespace FlowChartImporter.Core.Exporting;

/// <summary>
/// インポート結果のフロー内容をCSVに要約する。
/// 1列目: 処理名(採番フォーマット適用)
/// 2列目: フローの種類(開始/終了/分岐/処理/呼び出し)
/// 3列目: 開始からそのフローまでに必ず通過し、かつ通過方向が一意に定まる分岐のYES/NO一覧
/// 4列目: 実施主体(部署・システム・他社等)
/// 5列目: 内容(図形内テキスト)
/// 6列目: 備考(関連ファイル、近くの「[」図形の内容)
/// </summary>
public class FlowChartCsvExporter
{
    private const string VirtualRootId = "__root__";

    /// <summary>CSV出力に必要な各種ルックアップ(種類・表示名・隣接リスト・支配木)をまとめたもの。</summary>
    private readonly record struct ExportContext(
        Dictionary<string, FlowNode> NodeById,
        Dictionary<string, List<FlowEdge>> Outgoing,
        Dictionary<string, string> Categories,
        Dictionary<string, string> DisplayNames,
        Dictionary<string, List<string>> Successors,
        Dictionary<string, string> Idom);

    public string Export(FlowChart chart, ImportSettings settings)
    {
        var context = BuildExportContext(chart, settings);

        var sb = new StringBuilder();
        sb.Append("処理名,種類,分岐ルート,実施主体,内容,備考\r\n");

        // DB(データストア)シェイプは業務フローの処理ではないため、CSVには出力しない(JSONには残す)
        foreach (var node in chart.Nodes.Where(n => n.ShapeType != ShapeType.Database).OrderBy(n => n.Number))
        {
            var route = BuildBranchRoute(
                node.Id, context.Idom, context.NodeById, context.DisplayNames, context.Outgoing, context.Successors);
            sb.Append(CsvField(context.DisplayNames[node.Id])).Append(',')
              .Append(CsvField(context.Categories[node.Id])).Append(',')
              .Append(CsvField(route)).Append(',')
              .Append(CsvField(string.Join("/", node.Actors))).Append(',')
              .Append(CsvField(node.Text)).Append(',')
              .Append(CsvField(BuildRemarks(node))).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// ノード・エッジから、種類・表示名・隣接リスト(仮想ルートから開始ノード群への経路を含む)・
    /// 支配木といった、CSV行の組み立てに必要なルックアップを事前計算する。
    /// </summary>
    private static ExportContext BuildExportContext(FlowChart chart, ImportSettings settings)
    {
        var nodeById = chart.Nodes.ToDictionary(n => n.Id);

        var outgoing = chart.Nodes.ToDictionary(n => n.Id, _ => new List<FlowEdge>());
        var inDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var edge in chart.Edges)
        {
            if (outgoing.TryGetValue(edge.FromNodeId, out var list))
            {
                list.Add(edge);
            }
            if (inDegree.ContainsKey(edge.ToNodeId))
            {
                inDegree[edge.ToNodeId]++;
            }
        }

        var categories = chart.Nodes.ToDictionary(
            n => n.Id,
            n => ClassifyCategory(n, inDegree[n.Id], outgoing[n.Id].Count, settings));

        var displayNames = chart.Nodes.ToDictionary(
            n => n.Id,
            n => FormatDisplayName(settings.NodeNumberFormat, n.Number));

        // 仮想ルート→開始ノード群、というグラフで支配木を計算する
        var successors = chart.Nodes.ToDictionary(
            n => n.Id,
            n => outgoing[n.Id].Select(e => e.ToNodeId).ToList());
        successors[VirtualRootId] = chart.Nodes
            .Where(n => categories[n.Id] == settings.CategoryNameStart)
            .Select(n => n.Id)
            .ToList();

        var idom = DominatorTree.Compute(VirtualRootId, successors);

        return new ExportContext(nodeById, outgoing, categories, displayNames, successors, idom);
    }

    /// <summary>ノードの種類(開始/終了/分岐/処理/呼び出し)を、シェイプタイプと入出次数から判定する。</summary>
    private static string ClassifyCategory(FlowNode node, int inDegree, int outDegree, ImportSettings settings)
    {
        if (node.ShapeType == ShapeType.Diamond)
        {
            return settings.CategoryNameBranch;
        }
        if (node.ShapeType == ShapeType.Ellipse)
        {
            if (inDegree == 0)
            {
                return settings.CategoryNameStart;
            }
            if (outDegree == 0)
            {
                return settings.CategoryNameEnd;
            }
            return settings.CategoryNameCall; // 開始・終了のどちらでもない楕円 = 他フローの呼び出し
        }
        return settings.CategoryNameProcess;
    }

    private static string FormatDisplayName(string format, int number) =>
        string.IsNullOrEmpty(format) ? number.ToString() : format.Replace("{no}", number.ToString());

    /// <summary>ノードの備考欄(関連ファイル、近くの「[」図形の内容)のテキストを組み立てる。</summary>
    private static string BuildRemarks(FlowNode node)
    {
        var lines = new List<string>();
        if (node.RelatedFiles.Count > 0)
        {
            // 添付ファイル(書類シェイプ)のテキストは複数行にまたがっていることがあるが、
            // 備考欄では1件1行で列挙したいため、ファイル名内部の改行は取り除く
            lines.Add("関連ファイル: " + string.Join(", ", node.RelatedFiles.Select(StripNewlines)));
        }
        foreach (var remark in node.Remarks)
        {
            lines.Add("メモ: " + remark);
        }

        return string.Join("\n", lines);
    }

    private static string StripNewlines(string value) =>
        value.Replace("\r\n", "").Replace("\n", "").Replace("\r", "");

    /// <summary>
    /// 開始からそのノードに至る経路が必ず通過する分岐(diamond)のうち、
    /// 通過する方向(出て行く矢印)が一意に定まるものだけを、開始に近い順に列挙する。
    /// </summary>
    private static string BuildBranchRoute(
        string nodeId,
        Dictionary<string, string> idom,
        Dictionary<string, FlowNode> nodeById,
        Dictionary<string, string> displayNames,
        Dictionary<string, List<FlowEdge>> outgoing,
        Dictionary<string, List<string>> successors)
    {
        var dominators = new List<string>();
        var current = nodeId;
        while (idom.TryGetValue(current, out var parent))
        {
            dominators.Add(parent);
            current = parent;
        }
        dominators.Reverse(); // 開始に近い順

        var parts = new List<string>();
        foreach (var ancestorId in dominators)
        {
            if (!nodeById.TryGetValue(ancestorId, out var ancestor))
            {
                continue; // 仮想ルートを除外
            }
            if (ancestor.ShapeType != ShapeType.Diamond)
            {
                continue;
            }

            // このノードに到達できる、分岐から出る矢印のラベルを求める(重複ラベルは1つにまとめる)。
            // 同じ方向(ラベル)の矢印が複数の行き先(直接の子ノード)経由でも到達できる場合があるため、
            // 判定は「行き先ノード」ではなく「ラベル」の一意性で行う。
            // 分岐自身を再度通る経路(やり直しループ)は「その方向を通った」とはみなさないよう除外する。
            var reachingLabels = outgoing[ancestorId]
                .Where(e => CanReach(e.ToNodeId, nodeId, avoidId: ancestorId, successors))
                .Select(e => e.Label)
                .Distinct()
                .ToList();

            // 複数のラベル(方向)経由で到達できる場合、どちらを通ったか一意に定まらないため除外する
            if (reachingLabels.Count != 1)
            {
                continue;
            }

            var label = reachingLabels[0];
            parts.Add(string.IsNullOrWhiteSpace(label)
                ? displayNames[ancestorId]
                : $"{displayNames[ancestorId]}{label}");
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// fromId から targetId に到達できるかを、avoidId を経由せずに判定する。
    /// 分岐の出口ごとの到達判定で、分岐自身に戻るループ経路を辿らないようにするために使う。
    /// </summary>
    private static bool CanReach(
        string fromId, string targetId, string avoidId,
        Dictionary<string, List<string>> successors)
    {
        if (fromId == targetId)
        {
            return true;
        }

        var visited = new HashSet<string> { fromId, avoidId };
        var queue = new Queue<string>();
        queue.Enqueue(fromId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in successors.GetValueOrDefault(current, []))
            {
                if (next == targetId)
                {
                    return true;
                }
                if (visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
