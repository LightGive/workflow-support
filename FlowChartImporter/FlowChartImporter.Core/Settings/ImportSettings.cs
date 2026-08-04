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

    /// <summary>
    /// CSV出力等で使う処理名の表示フォーマット。"{no}" が採番された番号(Number)に置換される。
    /// 例: "A-{no}" → "A-10"
    /// </summary>
    public string NodeNumberFormat { get; set; } = "{no}";

    /// <summary>
    /// 分岐(ひし形)ノードの近くにあるYES/NOテキストボックスを検索する範囲(ポイント単位)。
    /// 矢印自体にテキストが無い場合のみ使われる。
    /// </summary>
    public double BranchLabelSearchRadiusPoints { get; set; } = 200.0;

    /// <summary>CSV出力の「種類」列で使う、開始ノードの種類名</summary>
    public string CategoryNameStart { get; set; } = "開始";

    /// <summary>CSV出力の「種類」列で使う、終了ノードの種類名</summary>
    public string CategoryNameEnd { get; set; } = "終了";

    /// <summary>CSV出力の「種類」列で使う、分岐(ひし形)ノードの種類名</summary>
    public string CategoryNameBranch { get; set; } = "分岐";

    /// <summary>CSV出力の「種類」列で使う、それ以外(通常の処理)ノードの種類名</summary>
    public string CategoryNameProcess { get; set; } = "処理";

    /// <summary>CSV出力の「種類」列で使う、呼び出し(開始・終了以外の楕円)ノードの種類名</summary>
    public string CategoryNameCall { get; set; } = "呼び出し";
}
