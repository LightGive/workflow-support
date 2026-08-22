using System.Text.RegularExpressions;
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
    /// <param name="warnings">
    /// 警告の追加先。矢印の起点が近接していて自動入れ替えが発生した場合に
    /// [YES/NO自動入れ替え] を追加する。debugLabelsがtrueの場合は判定の詳細も [YES/NOデバッグ] として追加する。
    /// </param>
    /// <param name="debugLabels">
    /// trueの場合、各分岐について見つかったYES/NOラベル候補・矢印ごとの距離・最終的な割り当て結果を
    /// [YES/NOデバッグ] としてwarningsに追加する(--debug-labelsオプション用)。
    /// </param>
    public static void ResolveMissingLabels(
        List<ResolvedConnection> connections,
        IReadOnlyDictionary<string, ShapeInfo> nodeShapeById,
        IReadOnlyList<ShapeInfo> yesNoTextBoxes,
        double searchRadiusPoints,
        List<string> warnings,
        bool debugLabels = false)
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
            string DescribeDiamond() =>
                $"'{WarningFormatting.Truncate(diamond.Text)}' (行{diamond.AnchorFromRow + 1}, 列{diamond.AnchorFromCol + 1})";
            string DescribeTarget(string toNodeId) =>
                nodeShapeById.TryGetValue(toNodeId, out var target) ? $"'{WarningFormatting.Truncate(target.Text)}'" : "(不明)";
            string ValueName(string v) => v == "Y" ? "YES" : "NO";

            if (!textBoxesByOwnerDiamond.TryGetValue(diamond, out var ownTextBoxes))
            {
                if (debugLabels && group.Any(t => t.connection.Label == null))
                {
                    warnings.Add(
                        $"[YES/NOデバッグ] {DescribeDiamond()}: 検索範囲(branchLabelSearchRadiusPoints)内にYES/NOラベル候補が見つかりませんでした。");
                }
                continue;
            }

            var candidateLabels = ownTextBoxes
                .Select(tb => (Label: MatchYesNo(tb.Text), Text: tb.Text, tb.CenterX, tb.CenterY))
                .Where(t => t.Label != null)
                .ToList();

            if (debugLabels)
            {
                foreach (var candidate in candidateLabels)
                {
                    warnings.Add(
                        $"[YES/NOデバッグ] {DescribeDiamond()}: 候補テキスト '{WarningFormatting.Truncate(candidate.Text)}' → {ValueName(candidate.Label!)} と判定");
                }
            }

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

            // 矢印ごとに、値(YES/NO)ごとの最短距離と、その最短距離を持つ候補図形(元のテキストを
            // LabelTextとして残すため)を求める。
            var nearestByValue = unlabeled.ToDictionary(
                u => u.index,
                u =>
                {
                    var origin = ConnectorOrigin(diamond, u.connection);
                    return distinctValues.ToDictionary(
                        v => v,
                        v => candidateLabels
                            .Where(l => l.Label == v)
                            .Select(l => (l.Text, Distance: GeometryUtils.Distance(origin.X, origin.Y, l.CenterX, l.CenterY)))
                            .OrderBy(t => t.Distance)
                            .First());
                });
            var distanceByValue = nearestByValue.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(v => v.Key, v => v.Value.Distance));

            // まず、矢印ごとに独立して最も近い値を選ぶ。
            var assigned = unlabeled.ToDictionary(
                u => u.index,
                u => distanceByValue[u.index].OrderBy(kv => kv.Value).First().Key);

            if (debugLabels)
            {
                foreach (var u in unlabeled)
                {
                    var dists = string.Join(", ", distanceByValue[u.index].Select(kv => $"{ValueName(kv.Key)}={kv.Value:F1}pt"));
                    warnings.Add(
                        $"[YES/NOデバッグ] {DescribeDiamond()} → {DescribeTarget(u.connection.ToNodeId)}: {dists} → 初期選択={ValueName(assigned[u.index])}");
                }
            }

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

                    // 矢印の起点が近接していたため全ての矢印が同じ値を独立に選んでしまい、
                    // 1本を自動的に反対の値へ入れ替えた。誤判定の可能性があるため必ず警告する。
                    warnings.Add(
                        $"[YES/NO自動入れ替え] {DescribeDiamond()} から {DescribeTarget(connections[swapIndex].ToNodeId)} への矢印を、"
                        + $"矢印の起点が近接していたため {ValueName(otherValue)} から {ValueName(missingValue)} に自動修正しました。分岐からの矢印の配置をご確認ください。");
                }
            }

            if (debugLabels)
            {
                foreach (var (index, label) in assigned)
                {
                    warnings.Add(
                        $"[YES/NOデバッグ] {DescribeDiamond()} 最終結果: → {DescribeTarget(connections[index].ToNodeId)} = {ValueName(label)}");
                }
            }

            foreach (var (index, label) in assigned)
            {
                connections[index] = connections[index] with
                {
                    Label = label,
                    LabelText = nearestByValue[index][label].Text,
                };
            }
        }
    }

    // YES/NOラベル候補(テキストボックス・枠線なし矩形)は実運用上YES/NO以外の用途に作られないため、
    // 先頭一致には限定せず、テキスト中のどこにYES/NOがあっても単語として認識する
    // (例: "[ YES ]=こういう状況の時"、"この場合はYESとなる" のどちらも認識できる)。
    // ただし "Note" や "Yesterday" のように英字の単語の一部になっている場合は誤って拾わないよう、
    // 前後が英字で連続していない(=独立した単語になっている)ことを要求する。
    private static readonly Regex YesNoWordPattern = new(
        @"(?<![A-Za-z])(YES|NO)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"YES"→"Y"、"NO"→"N" に正規化する(大文字・小文字、全角・半角を区別しない)。該当しない場合はnull。</summary>
    public static string? MatchYesNo(string text)
    {
        var normalized = NormalizeFullWidthLetters(text);
        var match = YesNoWordPattern.Match(normalized);
        if (!match.Success)
        {
            return null;
        }
        return string.Equals(match.Groups[1].Value, "YES", StringComparison.OrdinalIgnoreCase) ? "Y" : "N";
    }

    // 全角英字(U+FF21-FF3A, U+FF41-FF5A)と対応する半角英字(ASCII)のコードポイント差
    private const int FullWidthToHalfWidthOffset = 0xFEE0;

    // 全角英字(Ａ-Ｚ、ａ-ｚ)を対応する半角英字に変換する。それ以外の文字はそのまま残す
    // (YES/NO以外の部分、例えば末尾の説明文はそのまま保持する必要があるため、除去はしない)。
    private static string NormalizeFullWidthLetters(string text) =>
        new(text.Select(c => c is >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ' ? (char)(c - FullWidthToHalfWidthOffset) : c).ToArray());

    // 矢印(コネクタ)の分岐側の端点(=矢印の起点)。
    // 矢印の始点・終点のうち分岐の中心に近い方を、分岐側の端点とみなす。
    private static (double X, double Y) ConnectorOrigin(ShapeInfo diamond, ResolvedConnection connection)
    {
        var distStart = GeometryUtils.Distance(diamond.CenterX, diamond.CenterY, connection.StartX, connection.StartY);
        var distEnd = GeometryUtils.Distance(diamond.CenterX, diamond.CenterY, connection.EndX, connection.EndY);

        return distStart <= distEnd
            ? (connection.StartX, connection.StartY)
            : (connection.EndX, connection.EndY);
    }
}
