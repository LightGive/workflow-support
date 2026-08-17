using FlowChartImporter.Core.Models;

namespace FlowChartImporter.Core.Importing;

/// <summary>複数の解決ロジック(書類・備考の紐づけ、YES/NOラベル判定など)で共通して使う幾何計算。</summary>
internal static class GeometryUtils
{
    public static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>検証・除外処理の警告メッセージで共通して使うノードの整形ロジック。</summary>
internal static class WarningFormatting
{
    /// <summary>採番前後どちらでも使える、図形の基本情報の整形("'テキスト' (実施主体: ..., タイプ: ...)")。</summary>
    public static string DescribeShape(string text, IEnumerable<string> actors, ShapeType shapeType) =>
        $"'{Truncate(text)}' (実施主体: {string.Join("/", actors)}, タイプ: {shapeType})";

    /// <summary>採番済みノードの整形("No.N 'テキスト' (実施主体: ..., タイプ: ...)")。nullの場合は"(不明)"。</summary>
    public static string DescribeNode(FlowNode? node) =>
        node == null ? "(不明)" : $"No.{node.Number} {DescribeShape(node.Text, node.Actors, node.ShapeType)}";

    public static string Truncate(string text, int max = 20) =>
        text.Length <= max ? text : text[..max] + "…";
}
