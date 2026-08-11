using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// 分岐(ひし形)ノードから出る矢印のうち、矢印自体にテキストが無いものについて、
/// 近くにあるYES/NOテキストボックスとの角度を比較し、最も角度が小さいテキストボックスのラベルを割り当てる。
/// 矢印ごとに独立して最も近いテキストボックスを選ぶため、YES/NOそれぞれから複数本の矢印が
/// 出ている(分岐先が3本・2本等に分かれている)場合でも、それぞれの矢印が正しく判定される。
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

        // 各YES/NOラベル図形は、探索範囲が複数の分岐と重なる場合でも「最も近い1つの分岐」にのみ属するものとして扱う。
        // 分岐同士が近接して並ぶ図では、あるYES/NOラベルが本来属さない別の分岐の探索範囲にも入ってしまうことがあり、
        // そのまま角度比較すると本来の持ち主より別の分岐の矢印に近い角度になって誤って割り当てられる場合があるため。
        var textBoxesByOwnerDiamond = yesNoTextBoxes
            .Select(tb => (TextBox: tb, Nearest: diamonds
                .Select(d => (Diamond: d, Dist: Distance(d.CenterX, d.CenterY, tb.CenterX, tb.CenterY)))
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

            var nearbyLabels = ownTextBoxes
                .Select(tb => (Label: MatchYesNo(tb.Text), Direction: (X: tb.CenterX - diamond.CenterX, Y: tb.CenterY - diamond.CenterY)))
                .Where(t => t.Label != null && (t.Direction.X != 0 || t.Direction.Y != 0))
                .ToList();

            if (nearbyLabels.Count == 0)
            {
                continue;
            }

            foreach (var (connection, index) in group)
            {
                if (connection.Label != null)
                {
                    continue;
                }

                var toArrowEnd = OutwardVector(diamond, connection);
                if (toArrowEnd.X == 0 && toArrowEnd.Y == 0)
                {
                    continue;
                }

                string? bestLabel = null;
                double bestAngle = double.MaxValue;

                foreach (var (label, direction) in nearbyLabels)
                {
                    var angle = AngleBetween(direction, toArrowEnd);
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        bestLabel = label;
                    }
                }

                if (bestLabel != null)
                {
                    connections[index] = (connection.FromNodeId, connection.ToNodeId, bestLabel,
                        connection.StartX, connection.StartY, connection.EndX, connection.EndY);
                }
            }
        }
    }

    /// <summary>"YES"→"Y"、"NO"→"N" に正規化する(大文字・小文字を区別しない)。該当しない場合はnull。</summary>
    public static string? MatchYesNo(string text)
    {
        // "[ No ]" や "YES]" のように、装飾のかっこ・空白が付いている場合があるため、
        // 英字以外の文字(かっこ・全角/半角スペース等)を除いてから比較する。
        var normalized = new string(text.Where(char.IsLetter).ToArray());
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

    // 分岐の中心から見た矢印の「外向き」方向ベクトル。
    // 矢印の始点・終点のうち分岐から遠い方を、矢印の行き先方向とみなす。
    // カギ型接続線(折れ曲がる矢印)は分岐の近くでは同じ辺から複数本が同じ方向に出て
    // 後から分かれることが多いため、近い方の端点ではなく遠い方(=最終的な行き先)を使うことで、
    // 同じ辺から出る複数の矢印(YESから複数本・NOから複数本 等)でも正しく区別できる。
    private static (double X, double Y) OutwardVector(
        ShapeInfo diamond,
        (string FromNodeId, string ToNodeId, string? Label, double StartX, double StartY, double EndX, double EndY) connection)
    {
        var distStart = Distance(diamond.CenterX, diamond.CenterY, connection.StartX, connection.StartY);
        var distEnd = Distance(diamond.CenterX, diamond.CenterY, connection.EndX, connection.EndY);

        return distStart > distEnd
            ? (connection.StartX - diamond.CenterX, connection.StartY - diamond.CenterY)
            : (connection.EndX - diamond.CenterX, connection.EndY - diamond.CenterY);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double AngleBetween((double X, double Y) a, (double X, double Y) b)
    {
        var dot = a.X * b.X + a.Y * b.Y;
        var magA = Math.Sqrt(a.X * a.X + a.Y * a.Y);
        var magB = Math.Sqrt(b.X * b.X + b.Y * b.Y);
        var cos = Math.Clamp(dot / (magA * magB), -1.0, 1.0);
        return Math.Acos(cos);
    }
}
