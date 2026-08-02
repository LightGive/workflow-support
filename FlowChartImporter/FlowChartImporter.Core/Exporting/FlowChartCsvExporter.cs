using System.Text;
using FlowChartImporter.Core.Models;
using FlowChartImporter.Core.Settings;

namespace FlowChartImporter.Core.Exporting;

/// <summary>
/// インポート結果のフロー内容をCSVに要約する。
/// 1列目: 処理名(採番フォーマット適用)
/// 2列目: フローの種類(開始/終了/分岐/処理)
/// 3列目: 開始からそのフローまでに必ず通過し、かつ通過方向が一意に定まる分岐の一覧
/// </summary>
public class FlowChartCsvExporter
{
    private const string VirtualRootId = "__root__";

    public string Export(FlowChart chart, ImportSettings settings)
    {
        var nodeById = chart.Nodes.ToDictionary(n => n.Id);

        var outgoing = chart.Nodes.ToDictionary(n => n.Id, _ => new List<FlowEdge>());
        var inDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var edge in chart.Edges)
        {
            if (outgoing.TryGetValue(edge.FromNodeId, out var list))
                list.Add(edge);
            if (inDegree.ContainsKey(edge.ToNodeId))
                inDegree[edge.ToNodeId]++;
        }

        var categories = chart.Nodes.ToDictionary(
            n => n.Id,
            n => ClassifyCategory(n, inDegree[n.Id], outgoing[n.Id].Count));

        var displayNames = chart.Nodes.ToDictionary(
            n => n.Id,
            n => FormatDisplayName(settings.NodeNumberFormat, n.Number));

        // 仮想ルート→開始ノード群、というグラフで支配木を計算する
        var successors = chart.Nodes.ToDictionary(
            n => n.Id,
            n => outgoing[n.Id].Select(e => e.ToNodeId).ToList());
        successors[VirtualRootId] = chart.Nodes
            .Where(n => categories[n.Id] == "開始")
            .Select(n => n.Id)
            .ToList();

        var idom = DominatorTree.Compute(VirtualRootId, successors);
        var reachable = chart.Nodes.ToDictionary(n => n.Id, n => BfsReachable(n.Id, successors));

        var sb = new StringBuilder();
        sb.Append("処理名,種類,分岐ルート,部署,テキスト\r\n");

        foreach (var node in chart.Nodes.OrderBy(n => n.Number))
        {
            var route = BuildBranchRoute(node.Id, idom, nodeById, displayNames, outgoing, reachable);
            sb.Append(CsvField(displayNames[node.Id])).Append(',')
              .Append(CsvField(categories[node.Id])).Append(',')
              .Append(CsvField(route)).Append(',')
              .Append(CsvField(node.Department)).Append(',')
              .Append(CsvField(node.Text)).Append("\r\n");
        }

        return sb.ToString();
    }

    // ── 種類判定 ─────────────────────────────────────────────────
    private static string ClassifyCategory(FlowNode node, int inDegree, int outDegree)
    {
        if (node.ShapeType == ShapeType.Diamond) return "分岐";
        if (node.ShapeType == ShapeType.Ellipse)
        {
            if (inDegree == 0) return "開始";
            if (outDegree == 0) return "終了";
        }
        return "処理";
    }

    private static string FormatDisplayName(string format, int number) =>
        string.IsNullOrEmpty(format) ? number.ToString() : format.Replace("{no}", number.ToString());

    // ── 分岐ルート判定 ───────────────────────────────────────────
    // 開始からそのノードに至る経路が必ず通過する分岐(diamond)のうち、
    // 通過する方向(出て行く矢印)が一意に定まるものだけを、開始に近い順に列挙する。
    private static string BuildBranchRoute(
        string nodeId,
        Dictionary<string, string> idom,
        Dictionary<string, FlowNode> nodeById,
        Dictionary<string, string> displayNames,
        Dictionary<string, List<FlowEdge>> outgoing,
        Dictionary<string, HashSet<string>> reachable)
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
            if (!nodeById.TryGetValue(ancestorId, out var ancestor)) continue; // 仮想ルートを除外
            if (ancestor.ShapeType != ShapeType.Diamond) continue;

            // このノードに到達できる、分岐から出る矢印の行き先を求める(重複行き先は1つにまとめる)
            var reachingTargets = outgoing[ancestorId]
                .Where(e => reachable.TryGetValue(e.ToNodeId, out var set) && set.Contains(nodeId))
                .Select(e => e.ToNodeId)
                .Distinct()
                .ToList();

            // 複数の行き先経由で到達できる場合、どちらを通ったか一意に定まらないため除外する
            if (reachingTargets.Count != 1) continue;

            var label = outgoing[ancestorId].First(e => e.ToNodeId == reachingTargets[0]).Label;
            parts.Add(string.IsNullOrWhiteSpace(label)
                ? displayNames[ancestorId]
                : $"{displayNames[ancestorId]}{label}");
        }

        return string.Join(",", parts);
    }

    private static HashSet<string> BfsReachable(string startId, Dictionary<string, List<string>> successors)
    {
        var visited = new HashSet<string> { startId };
        var queue = new Queue<string>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in successors.GetValueOrDefault(current, []))
                if (visited.Add(next))
                    queue.Enqueue(next);
        }

        return visited;
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
