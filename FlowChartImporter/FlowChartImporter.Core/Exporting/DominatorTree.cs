namespace FlowChartImporter.Core.Exporting;

/// <summary>
/// Cooper, Harvey, Kennedy の反復法による支配木(dominator tree)の計算。
/// 「rootからそのノードに至るすべての経路が必ず通過するノード」を求めるために使う。
/// </summary>
internal static class DominatorTree
{
    /// <summary>
    /// ノードID→直近支配ノード(immediate dominator)IDのマップを返す。
    /// rootId 自身はキーに含めない。rootId から到達不能なノードも含めない。
    /// </summary>
    public static Dictionary<string, string> Compute(
        string rootId,
        IReadOnlyDictionary<string, List<string>> successors)
    {
        var postorder = new List<string>();
        var visited = new HashSet<string>();
        DfsPostorder(rootId, successors, visited, postorder);

        var postorderIndex = new Dictionary<string, int>();
        for (int i = 0; i < postorder.Count; i++)
            postorderIndex[postorder[i]] = i;

        // 逆後行順(root が最後 = 最大インデックス)で処理する
        var reversePostorder = Enumerable.Reverse(postorder).ToList();

        var predecessors = new Dictionary<string, List<string>>();
        foreach (var (from, tos) in successors) 
        {
            foreach (var to in tos)
            {
                if (!visited.Contains(to))
                {
                    continue; // 到達不能なノードへのエッジは無視
                }
                if (!predecessors.TryGetValue(to, out var list))
                {
                    predecessors[to] = list = [];
                }
                list.Add(from);
            }
        }

        var idom = new Dictionary<string, string> { [rootId] = rootId };

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in reversePostorder)
            {
                if (node == rootId)
                {
                    continue;
                }
                if (!predecessors.TryGetValue(node, out var preds))
                {
                    continue;
                }

                string? newIdom = null;
                foreach (var p in preds)
                {
                    if (!idom.ContainsKey(p))
                    {
                        continue;
                    }
                    newIdom = newIdom == null ? p : Intersect(newIdom, p, idom, postorderIndex);
                }

                if (newIdom != null && (!idom.TryGetValue(node, out var cur) || cur != newIdom))
                {
                    idom[node] = newIdom;
                    changed = true;
                }
            }
        }

        idom.Remove(rootId);
        return idom;
    }

    private static string Intersect(
        string a, string b,
        Dictionary<string, string> idom,
        Dictionary<string, int> postorderIndex)
    {
        while (a != b)
        {
            while (postorderIndex[a] < postorderIndex[b])
                a = idom[a];
            while (postorderIndex[b] < postorderIndex[a])
                b = idom[b];
        }
        return a;
    }

    private static void DfsPostorder(
        string nodeId,
        IReadOnlyDictionary<string, List<string>> successors,
        HashSet<string> visited,
        List<string> postorder)
    {
        if (!visited.Add(nodeId))
        {
            return;
        }
        foreach (var next in successors.GetValueOrDefault(nodeId, []))
            DfsPostorder(next, successors, visited, postorder);
        postorder.Add(nodeId);
    }
}
