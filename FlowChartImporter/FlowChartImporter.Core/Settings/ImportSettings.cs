namespace FlowChartImporter.Core.Settings;

public class ImportSettings
{
    /// <summary>コネクタ接続の座標近接判定に使う許容誤差(ポイント単位、1pt = 12700 EMU)</summary>
    public double ConnectionTolerancePoints { get; set; } = 10.0;

    /// <summary>ノード採番戦略名。"default" = 左→右・同列内は上→下</summary>
    public string NodeNumberingStrategy { get; set; } = "default";

    /// <summary>
    /// ルート完全性チェックの開始ノードのシェイプタイプ。
    /// null または空文字の場合はルートチェックを行わない。
    /// 例: "ellipse"
    /// </summary>
    public string? RouteCheckStartShapeType { get; set; }

    /// <summary>
    /// ルート完全性チェックの終了ノードのシェイプタイプ。
    /// null または空文字の場合はルートチェックを行わない。
    /// 例: "ellipse"
    /// </summary>
    public string? RouteCheckEndShapeType { get; set; }
}
