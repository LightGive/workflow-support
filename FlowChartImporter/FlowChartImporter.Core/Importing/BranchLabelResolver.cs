using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// 分岐(ひし形)ノードから出る矢印のうち、矢印自体にテキストが無いものについて、
/// 近くにあるYES/NOテキストボックスとの距離を比較してラベルを割り当てる。
/// 判定には、矢印(コネクタ)の分岐側の端点(=矢印の起点)からラベル中心までの距離を使う
/// (角度ではなく距離で判定するのは、複数の矢印が同じ方向を向くケースで角度差がほぼ無くなり
/// 誤判定するため)。
///
/// 各矢印はまず独立して、より近い方の値(同じ値のテキストボックスが複数あれば最短距離のもの)を選ぶ。
/// ただし全ての矢印の起点が近接している場合、全て同じ値を選んでしまい片方の値が1本も無くなることが
/// あるため、その場合は距離比が最も1に近い(=入れ替えても不自然でない)矢印を1本入れ替える。
/// </summary>
internal static class BranchLabelResolver
{
    public static void ResolveMissingLabels(
        List<(string FromNodeId, string ToNodeId, string? Label, double StartX, double StartY, double EndX, double EndY)> connections,
        IReadOnlyDictionary<string, ShapeInfo> nodeShapeById,
        IReadOnlyList<ShapeInfo> yesNoTextBoxes,
        double searchRadiusPoints)
    {
        if (yesNoTextBoxes.Count == 0)
        {
            return;
        }

        var diamonds = nodeShapeById.Values.Where(s => s.ShapeType == ShapeType.Diamond).ToList();
        if (diamonds.Count == 0)
        {
            return;
        }

        // 各YES/NOラベル図形は、探索範囲が複数の分岐と重なっていても「最も近い1つの分岐」にのみ属させる。
        var textBoxesByOwnerDiamond = yesNoTextBoxes
            .Select(tb => (TextBox: tb, Nearest: diamonds
                .Select(d => (Diamond: d, Dist: GeometryUtils.Distance(d.CenterX, d.CenterY, tb.CenterX, tb.CenterY)))
                .OrderBy(t => t.Dist)
                .First()))
            .Where(t => t.Nearest.Dist <= searchRadiusPoints)
            .GroupBy(t => t.Nearest.Diamond, t => t.TextBox)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ShapeInfo>)g.ToList());

        var indexedByFromNode = connections
            .Select((connection, index) => (connection, index))
            .Where(t => nodeShapeById.TryGetValue(t.connection.FromNodeId, out var shape)
                        && shape.ShapeType == ShapeType.Diamond)
            .GroupBy(t => t.connection.FromNodeId);

        foreach (var group in indexedByFromNode)
        {
            var diamond = nodeShapeById[group.Key];

            if (!textBoxesByOwnerDiamond.TryGetValue(diamond, out var ownTextBoxes))
            {
                continue;
            }

            var candidateLabels = ownTextBoxes
                .Select(tb => (Label: MatchYesNo(tb.Text), tb.CenterX, tb.CenterY))
                .Where(t => t.Label != null)
                .ToList();

            if (candidateLabels.Count == 0)
            {
                continue;
            }

            var distinctValues = candidateLabels.Select(l => l.Label!).Distinct().ToList();

            var unlabeled = group.Where(t => t.connection.Label == null).ToList();
            if (unlabeled.Count == 0)
            {
                continue;
            }

            // 矢印ごとに、値(YES/NO)ごとの最短距離を求める。
            var distanceByValue = unlabeled.ToDictionary(
                u => u.index,
                u =>
                {
                    var origin = ConnectorOrigin(diamond, u.connection);
                    return distinctValues.ToDictionary(
                        v => v,
                        v => candidateLabels
                            .Where(l => l.Label == v)
                            .Min(l => GeometryUtils.Distance(origin.X, origin.Y, l.CenterX, l.CenterY)));
                });

            // まず、矢印ごとに独立して最も近い値を選ぶ。
            var assigned = unlabeled.ToDictionary(
                u => u.index,
                u => distanceByValue[u.index].OrderBy(kv => kv.Value).First().Key);

            // 選ばれなかった値がある場合、距離の比が最も1に近い矢印を1本入れ替える(移動元が1本しか
            // ない場合は入れ替えると空になるだけなのでスキップ)。
            if (distinctValues.Count == 2)
            {
                foreach (var missingValue in distinctValues.Where(v => !assigned.Values.Contains(v)))
                {
                    var otherValue = distinctValues.First(v => v != missingValue);
                    var otherGroup = assigned.Where(kv => kv.Value == otherValue).Select(kv => kv.Key).ToList();
                    if (otherGroup.Count <= 1)
                    {
                        continue;
                    }

                    var swapIndex = otherGroup
                        .OrderBy(i => distanceByValue[i][missingValue] / distanceByValue[i][otherValue])
                        .First();
                    assigned[swapIndex] = missingValue;
                }
            }

            foreach (var (index, label) in assigned)
            {
                var connection = connections[index];
                connections[index] = (connection.FromNodeId, connection.ToNodeId, label,
                    connection.StartX, connection.StartY, connection.EndX, connection.EndY);
            }
        }
    }

    /// <summary>"YES"→"Y"、"NO"→"N" に正規化する(大文字・小文字、全角・半角を区別しない)。該当しない場合はnull。</summary>
    public static string? MatchYesNo(string text)
    {
        // "[ No ]" や "YES]"、全角の "ＹＥＳ" のように、装飾のかっこ・空白や全角表記の場合があるため、
        // 英字(全角英字は半角に変換したうえで)以外の文字(かっこ・全角/半角スペース等)を除いてから比較する。
        var normalized = new string(text
            .Select(ToHalfWidthLetterOrNull)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToArray());
        if (string.Equals(normalized, "YES", StringComparison.OrdinalIgnoreCase))
        {
            return "Y";
        }
        if (string.Equals(normalized, "NO", StringComparison.OrdinalIgnoreCase))
        {
            return "N";
        }
        return null;
    }

    // 全角英字(Ａ-Ｚ、ａ-ｚ)を対応する半角英字に変換する。半角英字はそのまま、それ以外(数字・記号等)はnullを返す。
    private static char? ToHalfWidthLetterOrNull(char c)
    {
        if (c is >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
        {
            return (char)(c - 0xFEE0);
        }
        return char.IsLetter(c) ? c : null;
    }

    // 矢印(コネクタ)の分岐側の端点(=矢印の起点)。
    // 矢印の始点・終点のうち分岐の中心に近い方を、分岐側の端点とみなす。
    private static (double X, double Y) ConnectorOrigin(
        ShapeInfo diamond,
        (string FromNodeId, string ToNodeId, string? Label, double StartX, double StartY, double EndX, double EndY) connection)
    {
        var distStart = GeometryUtils.Distance(diamond.CenterX, diamond.CenterY, connection.StartX, connection.StartY);
        var distEnd = GeometryUtils.Distance(diamond.CenterX, diamond.CenterY, connection.EndX, connection.EndY);

        return distStart <= distEnd
            ? (connection.StartX, connection.StartY)
            : (connection.EndX, connection.EndY);
    }
}
