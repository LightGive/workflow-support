using FlowChartImporter.Core.Importing.Internal;
using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>
/// 分岐(ひし形)ノードから出る矢印のうち、矢印自体にテキストが無いものについて、
/// 近くにあるYES/NOテキストボックスとの角度を比較し、最も角度が小さい矢印にラベルを割り当てる。
/// </summary>
internal static class BranchLabelResolver
{
    public static void ResolveMissingLabels(
        List<(string FromNodeId, string ToNodeId, string? Label, double StartX, double StartY, double EndX, double EndY, bool IsElbow)> connections,
        IReadOnlyDictionary<string, ShapeInfo> nodeShapeById,
        IReadOnlyList<ShapeInfo> yesNoTextBoxes,
        double searchRadiusPoints)
    {
        if (yesNoTextBoxes.Count == 0)
        {
            return;
        }

        var indexedByFromNode = connections
            .Select((connection, index) => (connection, index))
            .Where(t => nodeShapeById.TryGetValue(t.connection.FromNodeId, out var shape)
                        && shape.ShapeType == ShapeType.Diamond)
            .GroupBy(t => t.connection.FromNodeId);

        foreach (var group in indexedByFromNode)
        {
            var diamond = nodeShapeById[group.Key];

            var nearbyTextBoxes = yesNoTextBoxes
                .Where(tb => Distance(diamond.CenterX, diamond.CenterY, tb.CenterX, tb.CenterY) <= searchRadiusPoints)
                .ToList();

            foreach (var textBox in nearbyTextBoxes)
            {
                var yesNo = MatchYesNo(textBox.Text);
                if (yesNo == null)
                {
                    continue;
                }

                var toTextBox = (X: textBox.CenterX - diamond.CenterX, Y: textBox.CenterY - diamond.CenterY);
                if (toTextBox.X == 0 && toTextBox.Y == 0)
                {
                    continue;
                }

                int? bestIndex = null;
                double bestAngle = double.MaxValue;

                foreach (var (connection, index) in group)
                {
                    // Label は group のスナップショット取得時点の値なので、
                    // 同じ分岐を別のテキストボックスが先に処理して割り当て済みになっている場合があるため、
                    // 常に connections の最新値を見て判定する(でないと後勝ちで上書きしてしまう)。
                    if (connections[index].Label != null)
                    {
                        continue;
                    }

                    var toArrowEnd = OutwardVector(diamond, connection);
                    if (toArrowEnd.X == 0 && toArrowEnd.Y == 0)
                    {
                        continue;
                    }

                    var angle = AngleBetween(toTextBox, toArrowEnd);
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        bestIndex = index;
                    }
                }

                if (bestIndex != null)
                {
                    var c = connections[bestIndex.Value];
                    connections[bestIndex.Value] = (c.FromNodeId, c.ToNodeId, yesNo, c.StartX, c.StartY, c.EndX, c.EndY, c.IsElbow);
                }
            }
        }
    }

    /// <summary>"YES"→"Y"、"NO"→"N" に正規化する(大文字・小文字を区別しない)。該当しない場合はnull。</summary>
    public static string? MatchYesNo(string text)
    {
        var trimmed = text.Trim();
        if (string.Equals(trimmed, "YES", StringComparison.OrdinalIgnoreCase))
        {
            return "Y";
        }
        if (string.Equals(trimmed, "NO", StringComparison.OrdinalIgnoreCase))
        {
            return "N";
        }
        return null;
    }

    // 分岐の中心から見た矢印の「外向き」方向ベクトル。
    // 直線: 矢印の始点・終点のうち分岐から遠い方を、矢印の先(行き先方向)とみなす。
    // カギ型接続線: 経路の途中で折れ曲がり、横に出て後ろに戻るようなケースがあるため、
    //   遠い方の端点ではなく、分岐に近い方の端点(=最初に出た位置)がどの辺に接しているかで
    //   最初に出た方向(上下左右いずれか)を判定する。
    private static (double X, double Y) OutwardVector(
        ShapeInfo diamond,
        (string FromNodeId, string ToNodeId, string? Label, double StartX, double StartY, double EndX, double EndY, bool IsElbow) connection)
    {
        var distStart = Distance(diamond.CenterX, diamond.CenterY, connection.StartX, connection.StartY);
        var distEnd = Distance(diamond.CenterX, diamond.CenterY, connection.EndX, connection.EndY);

        if (connection.IsElbow)
        {
            var (nearX, nearY) = distStart <= distEnd
                ? (connection.StartX, connection.StartY)
                : (connection.EndX, connection.EndY);
            return NearestEdgeDirection(diamond, nearX, nearY);
        }

        return distStart > distEnd
            ? (connection.StartX - diamond.CenterX, connection.StartY - diamond.CenterY)
            : (connection.EndX - diamond.CenterX, connection.EndY - diamond.CenterY);
    }

    // 図形のバウンディングボックスのうち、指定した点に最も近い辺の外向き方向を返す。
    private static (double X, double Y) NearestEdgeDirection(ShapeInfo shape, double x, double y)
    {
        var distLeft = Math.Abs(x - shape.Left);
        var distRight = Math.Abs(shape.Right - x);
        var distTop = Math.Abs(y - shape.Top);
        var distBottom = Math.Abs(shape.Bottom - y);

        var min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        if (min == distLeft)
        {
            return (-1, 0);
        }
        if (min == distRight)
        {
            return (1, 0);
        }
        if (min == distTop)
        {
            return (0, -1);
        }
        return (0, 1);
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
