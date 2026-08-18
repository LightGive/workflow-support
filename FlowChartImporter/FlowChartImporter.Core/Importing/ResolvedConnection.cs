namespace FlowChartImporter.Core.Importing;

/// <summary>ノードID同士に解決済みのコネクタ(矢印)1本分。座標は分岐ラベル判定(BranchLabelResolver)に使う。</summary>
internal sealed record ResolvedConnection(
    string FromNodeId,
    string ToNodeId,
    string? Label,
    double StartX,
    double StartY,
    double EndX,
    double EndY);
