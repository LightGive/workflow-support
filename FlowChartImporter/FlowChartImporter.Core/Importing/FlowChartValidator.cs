using FlowChartImporter.Core.Models;
using FlowChartImporter.Core.Settings;

namespace FlowChartImporter.Core.Importing;

public class FlowChartValidator
{
    public IReadOnlyList<string> Validate(FlowChart chart, ImportSettings settings)
    {
        var warnings = new List<string>();

        var nodeById = chart.Nodes.ToDictionary(n => n.Id);

        var outgoing = chart.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var incoming = chart.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var edge in chart.Edges)
        {
            if (outgoing.ContainsKey(edge.FromNodeId))
                outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            if (incoming.ContainsKey(edge.ToNodeId))
                incoming[edge.ToNodeId].Add(edge.FromNodeId);
        }

        CheckDuplicateEdges(chart, nodeById, warnings);
        CheckIsolatedNodes(chart, outgoing, incoming, warnings);

        if (!string.IsNullOrWhiteSpace(settings.RouteCheckStartShapeType) &&
            !string.IsNullOrWhiteSpace(settings.RouteCheckEndShapeType))
        {
            CheckRouteCompleteness(chart, settings, outgoing, nodeById, warnings);
        }

        return warnings;
    }

    // ── 1. 重複エッジ ────────────────────────────────────────────
    private static void CheckDuplicateEdges(
        FlowChart chart,
        Dictionary<string, FlowNode> nodeById,
        List<string> warnings)
    {
        var duplicates = chart.Edges
            .GroupBy(e => (e.FromNodeId, e.ToNodeId))
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            var from = nodeById.GetValueOrDefault(group.Key.FromNodeId);
            var to = nodeById.GetValueOrDefault(group.Key.ToNodeId);
            warnings.Add(
                $"[重複エッジ] {NodeInfo(from)} → {NodeInfo(to)} の矢印が {group.Count()} 本あります。");
        }
    }

    // ── 2. 孤立ノード ────────────────────────────────────────────
    private static void CheckIsolatedNodes(
        FlowChart chart,
        Dictionary<string, List<string>> outgoing,
        Dictionary<string, List<string>> incoming,
        List<string> warnings)
    {
        foreach (var node in chart.Nodes)
        {
            bool hasOut = outgoing.TryGetValue(node.Id, out var outs) && outs.Count > 0;
            bool hasIn = incoming.TryGetValue(node.Id, out var ins) && ins.Count > 0;
            if (!hasOut && !hasIn)
                warnings.Add($"[孤立ノード] {NodeInfo(node)} は矢印が1本も接続されていません。");
        }
    }

    // ── 3. ルート完全性 ──────────────────────────────────────────
    private static void CheckRouteCompleteness(
        FlowChart chart,
        ImportSettings settings,
        Dictionary<string, List<string>> outgoing,
        Dictionary<string, FlowNode> nodeById,
        List<string> warnings)
    {
        if (!Enum.TryParse<ShapeType>(settings.RouteCheckStartShapeType, ignoreCase: true, out var startType))
        {
            warnings.Add($"[設定エラー] RouteCheckStartShapeType '{settings.RouteCheckStartShapeType}' は無効な値です。");
            return;
        }
        if (!Enum.TryParse<ShapeType>(settings.RouteCheckEndShapeType, ignoreCase: true, out var endType))
        {
            warnings.Add($"[設定エラー] RouteCheckEndShapeType '{settings.RouteCheckEndShapeType}' は無効な値です。");
            return;
        }

        var startNodes = chart.Nodes.Where(n => n.ShapeType == startType).ToList();
        var endNodeIds = chart.Nodes
            .Where(n => n.ShapeType == endType)
            .Select(n => n.Id)
            .ToHashSet();

        if (startNodes.Count == 0)
        {
            warnings.Add($"[ルート確認] 開始ノード (shapeType={settings.RouteCheckStartShapeType}) が見つかりません。");
            return;
        }
        if (endNodeIds.Count == 0)
        {
            warnings.Add($"[ルート確認] 終了ノード (shapeType={settings.RouteCheckEndShapeType}) が見つかりません。");
            return;
        }

        foreach (var startNode in startNodes)
        {
            var reachable = BfsReachable(startNode.Id, outgoing);

            // 終了ノードへの到達可否
            if (!reachable.Any(id => endNodeIds.Contains(id)))
            {
                warnings.Add(
                    $"[未到達] 開始ノード {NodeInfo(startNode)} から終了ノードに到達できるルートがありません。");
            }

            // 到達可能な範囲の中で行き止まりになっているノードを検出
            foreach (var nodeId in reachable)
            {
                if (endNodeIds.Contains(nodeId)) continue;
                if (outgoing.TryGetValue(nodeId, out var nexts) && nexts.Count == 0)
                {
                    var node = nodeById.GetValueOrDefault(nodeId);
                    warnings.Add(
                        $"[途中終了] {NodeInfo(node)} に後続の矢印がなく、終了ノードに到達できません。" +
                        $" (開始: {NodeInfo(startNode)})");
                }
            }
        }
    }

    private static HashSet<string> BfsReachable(
        string startId,
        Dictionary<string, List<string>> outgoing)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        visited.Add(startId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in outgoing.GetValueOrDefault(current, []))
            {
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        return visited;
    }

    private static string NodeInfo(FlowNode? node) =>
        node == null
            ? "(不明)"
            : $"No.{node.Number} '{Truncate(node.Text)}' (部署: {node.Department}, タイプ: {node.ShapeType})";

    private static string Truncate(string text, int max = 20) =>
        text.Length <= max ? text : text[..max] + "…";
}
