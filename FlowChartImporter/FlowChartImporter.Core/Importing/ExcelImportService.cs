using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowChartImporter.Core.Importing.NodeNumbering;
using FlowChartImporter.Core.Models;
using FlowChartImporter.Core.Settings;

namespace FlowChartImporter.Core.Importing;

public class ExcelImportService
{
    private readonly ImportSettings _settings;
    private readonly INodeNumberingStrategy _numberingStrategy;

    public ExcelImportService(ImportSettings settings)
    {
        _settings = settings;
        _numberingStrategy = NodeNumberingStrategyFactory.Create(settings.NodeNumberingStrategy);
    }

    /// <summary>
    /// Excelファイルを解析してフロー図を抽出する。
    /// </summary>
    /// <param name="minRow">
    /// この行番号(1始まり)より上にあるシェイプ・テキストを無視する。
    /// 既定値の1を指定した場合は何も無視しない(シート先頭から対象)。
    /// </param>
    /// <param name="ignoreActor">
    /// 一番左(A列)の実施主体名がこの文字列と一致する行のシェイプ・テキストを無視する。
    /// nullまたは空文字の場合は何も無視しない。
    /// </param>
    public ImportResult Import(string filePath, string sheetName, int minRow = 1, string? ignoreActor = null)
    {
        using var doc = SpreadsheetDocument.Open(filePath, isEditable: false);
        var workbookPart = doc.WorkbookPart
            ?? throw new InvalidOperationException("WorkbookPart が見つかりません。");

        var worksheetPart = FindWorksheetPart(workbookPart, sheetName)
            ?? throw new InvalidOperationException($"シート '{sheetName}' が見つかりません。");

        var chart = new FlowChart(sheetName);
        var allWarnings = new List<string>();

        // 実施主体の行範囲を先に判定し、無視対象の指定があればその行範囲を求める
        var actorDetector = new ActorDetector();
        var actorRanges = actorDetector.Detect(workbookPart, worksheetPart);
        var ignoredRowRanges = string.IsNullOrEmpty(ignoreActor)
            ? null
            : actorRanges
                .Where(r => r.Name.Trim() == ignoreActor.Trim())
                .Select(r => (r.StartRow, r.EndRow))
                .ToList();

        // 1. 図形・コネクタを抽出
        var extractor = new ExcelShapeExtractor();
        var (shapes, connectors, extractWarnings) = extractor.Extract(worksheetPart, minRowIndex: minRow - 1, ignoredRowRanges);
        allWarnings.AddRange(extractWarnings);

        // 2. シェイプをノード候補・YES/NOラベル候補・備考候補等に分類し、Line シェイプをコネクタに変換
        var (lineShapes, documentShapes, nodeShapes, yesNoTextBoxes, remarkTextBoxes) = ClassifyShapes(shapes, allWarnings);
        AddLineConnectors(lineShapes, connectors);

        // 3. ノードを生成
        var (xmlIdToNodeId, nodeMap) = BuildNodes(chart, nodeShapes);

        // 4. 実施主体(部署・システム・他社等)を判定
        AssignActors(nodeMap, actorDetector, actorRanges);

        // 5. 書類シェイプ・備考(「[」図形)を親ノードに紐づけ
        NearestNodeAssociator.Associate(documentShapes, nodeMap, _settings.BranchLabelSearchRadiusPoints,
            selectValue: s => s.Text, addValue: (node, text) => node.RelatedFiles.Add(text));
        NearestNodeAssociator.Associate(remarkTextBoxes, nodeMap, _settings.BranchLabelSearchRadiusPoints,
            selectValue: s => s.Text.Trim(), addValue: (node, text) => node.Remarks.Add(text));

        // 6. コネクタをエッジに変換
        ResolveEdges(chart, connectors, nodeShapes, xmlIdToNodeId, nodeMap, yesNoTextBoxes);

        // 7. 矢印で他のノードと接続されていない孤立したシェイプは、
        // 業務フローとして意味を持たないため出力対象(JSON/CSV)から除外する。
        // (メモ・関連ファイルはノードそのものではなく、既に付随情報として関連ノードに統合済みのため対象外)
        ExcludeIsolatedNodes(chart, allWarnings);

        // 8. 開始ノードから終了ノードまでの経路上に無いノード(例: 開始からは辿り着けないが終了には
        // 合流するだけの、無関係な処理)は断片的な処理とみなし、出力対象から除外する。
        var diamondsWithExcludedBranch = ExcludeNodesOffStartToEndPath(chart, allWarnings);

        // 9. ノード番号を採番(矢印で接続されたノードのみが対象)
        _numberingStrategy.AssignNumbers(chart.Nodes);

        // 10. 分岐(ひし形)のYES/NOは必ず対になっているはずである。
        // 判定の結果、片方(YESのみ/NOのみ)しか見つからない場合はその判定を信用せず、
        // 警告したうえで分岐メモ(CSVの分岐ルート)には使わないようにする。
        NormalizeYesNoLabelPairs(chart, allWarnings, diamondsWithExcludedBranch);

        // 11. 検証
        var validator = new FlowChartValidator();
        allWarnings.AddRange(validator.Validate(chart, _settings));

        // 警告は採番した番号("No.N")の昇順で表示する。番号を含まない警告は末尾にまとめる。
        var sortedWarnings = allWarnings
            .OrderBy(w => ExtractNodeNumber(w) ?? int.MaxValue)
            .ToList();

        return new ImportResult(chart, sortedWarnings);
    }

    // 矩形(Rectangle)はプロセスを表す図形だが、枠線が無い/点線のものはプロセスではなく、
    // 分岐先のYES/NOラベルやその他の注記(テキストボックス相当)として使われている。
    // テキストボックス・「[」図形はノードには含めない。YES/NOテキストは分岐のラベル判定に、
    // 「[」図形は近くのノードの備考として使う。
    private static (
        List<Internal.ShapeInfo> LineShapes,
        List<Internal.ShapeInfo> DocumentShapes,
        List<Internal.ShapeInfo> NodeShapes,
        List<Internal.ShapeInfo> YesNoTextBoxes,
        List<Internal.ShapeInfo> RemarkTextBoxes
        ) ClassifyShapes(List<Internal.ShapeInfo> shapes, List<string> warnings)
    {
        bool IsBorderlessRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.HasNoLine;
        bool IsDashedRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.IsDashed;
        bool IsYesNoLabelCandidate(Internal.ShapeInfo s) => s.IsTextBox || IsBorderlessRectangle(s);

        var lineShapes = shapes.Where(s => s.ShapeType == ShapeType.Line).ToList();
        var documentShapes = shapes.Where(s => s.ShapeType == ShapeType.Document).ToList();

        var yesNoTextBoxes = shapes
            .Where(s => IsYesNoLabelCandidate(s) && BranchLabelResolver.MatchYesNo(s.Text) != null)
            .ToList();

        // YES/NOラベル候補(テキストボックス・枠線なし矩形)だが、YES/NOとして認識できないテキストが
        // 書かれている図形は、備考(「[」図形)としても扱われず、分岐ラベルにも使われないため
        // 内容がそのまま失われてしまう。気づけるよう警告しておく。
        foreach (var shape in shapes.Where(s => IsYesNoLabelCandidate(s)
                     && !string.IsNullOrWhiteSpace(s.Text)
                     && BranchLabelResolver.MatchYesNo(s.Text) == null))
        {
            warnings.Add(
                $"[YES/NO判定不可] '{WarningFormatting.Truncate(shape.Text)}' (行{shape.AnchorFromRow + 1}, 列{shape.AnchorFromCol + 1}) "
                + "はYES/NOのテキストとして認識できず、備考としても扱われないため出力されません。");
        }

        // ノードへの備考(メモ)は「[」(角かっこ)の図形に書かれたテキストのみを対象とする。
        var remarkTextBoxes = shapes
            .Where(s => s.ShapeType == ShapeType.Bracket)
            .ToList();

        var nodeShapes = shapes.Where(s => s.ShapeType != ShapeType.Document
                                        && s.ShapeType != ShapeType.Line
                                        && s.ShapeType != ShapeType.Bracket
                                        && !s.IsTextBox
                                        && !IsBorderlessRectangle(s)
                                        && !IsDashedRectangle(s)).ToList();

        return (lineShapes, documentShapes, nodeShapes, yesNoTextBoxes, remarkTextBoxes);
    }

    // Line シェイプを ConnectorInfo に変換してコネクタリストに追加する
    private static void AddLineConnectors(List<Internal.ShapeInfo> lineShapes, List<Internal.ConnectorInfo> connectors)
    {
        foreach (var line in lineShapes)
        {
            var (startX, startY, endX, endY) = GetLineEndpoints(line);
            connectors.Add(new Internal.ConnectorInfo
            {
                XmlId = line.XmlId,
                StartShapeXmlId = null,
                EndShapeXmlId = null,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                Label = string.IsNullOrWhiteSpace(line.Text) ? null : line.Text.Trim(),
                IsDashed = line.IsDashed,
            });
        }
    }

    private static (
        Dictionary<uint, string> XmlIdToNodeId,
        List<(Internal.ShapeInfo Shape, FlowNode Node)> NodeMap
        ) BuildNodes(FlowChart chart, List<Internal.ShapeInfo> nodeShapes)
    {
        var xmlIdToNodeId = new Dictionary<uint, string>();
        var nodeMap = new List<(Internal.ShapeInfo Shape, FlowNode Node)>();

        foreach (var shape in nodeShapes)
        {
            var node = new FlowNode(Guid.NewGuid().ToString())
            {
                Text = shape.Text,
                ShapeType = shape.ShapeType,
                Position = new Position(
                    row: shape.AnchorFromRow + 1,    // 1始まり
                    column: shape.AnchorFromCol + 1, // 1始まり
                    width: shape.Width,
                    height: shape.Height),
            };
            chart.Nodes.Add(node);
            xmlIdToNodeId[shape.XmlId] = node.Id;
            nodeMap.Add((shape, node));
        }

        return (xmlIdToNodeId, nodeMap);
    }

    private static void AssignActors(
        List<(Internal.ShapeInfo Shape, FlowNode Node)> nodeMap,
        ActorDetector actorDetector,
        List<ActorDetector.ActorRange> actorRanges)
    {
        foreach (var (shape, node) in nodeMap)
        {
            node.Actors = actorDetector.GetActors(actorRanges, shape.AnchorFromRow, shape.AnchorToRow);
        }
    }

    private void ResolveEdges(
        FlowChart chart,
        List<Internal.ConnectorInfo> connectors,
        List<Internal.ShapeInfo> nodeShapes,
        Dictionary<uint, string> xmlIdToNodeId,
        List<(Internal.ShapeInfo Shape, FlowNode Node)> nodeMap,
        List<Internal.ShapeInfo> yesNoTextBoxes)
    {
        // 点線の矢印はデータのやり取りを表すものであり、業務フローの流れとしては扱わない
        // (孤立ノード判定・開始/終了到達判定・分岐ラベル判定・CSV出力の対象外)。
        // ただし読み込み自体は行い、DataEdgesとしてJSONに保持する。
        // (DB(データストア)シェイプに繋がる矢印も、線種が実線であっても同様にDataEdgesとして扱う。後述)
        var flowConnectors = connectors.Where(c => !c.IsDashed).ToList();
        var dataConnectors = connectors.Where(c => c.IsDashed).ToList();
        var resolver = new ConnectorResolver(_settings.ConnectionTolerancePoints);
        var connections = resolver.Resolve(flowConnectors, nodeShapes, xmlIdToNodeId);

        // 矢印自体にテキストが無い分岐について、近くのYES/NOテキストボックスからラベルを補う
        var nodeShapeById = nodeMap.ToDictionary(t => t.Node.Id, t => t.Shape);
        BranchLabelResolver.ResolveMissingLabels(
            connections, nodeShapeById, yesNoTextBoxes, _settings.BranchLabelSearchRadiusPoints);

        // DB(データストア)シェイプに繋がる矢印は、線種が実線でもデータのやり取りを表すとみなし、
        // 点線の矢印と同様に業務フローのエッジ(Edges)ではなくDataEdgesとして扱う。
        var databaseNodeIds = nodeMap
            .Where(t => t.Node.ShapeType == ShapeType.Database)
            .Select(t => t.Node.Id)
            .ToHashSet();
        bool TouchesDatabase(ResolvedConnection c) =>
            databaseNodeIds.Contains(c.FromNodeId) || databaseNodeIds.Contains(c.ToNodeId);

        int edgeSeq = 1;
        foreach (var (fromId, toId, label, _, _, _, _) in connections.Where(c => !TouchesDatabase(c)))
            chart.Edges.Add(new FlowEdge($"edge{edgeSeq++}", fromId, toId, label));

        var dataConnections = resolver.Resolve(dataConnectors, nodeShapes, xmlIdToNodeId)
            .Concat(connections.Where(TouchesDatabase));
        int dataEdgeSeq = 1;
        foreach (var (fromId, toId, label, _, _, _, _) in dataConnections)
            chart.DataEdges.Add(new FlowEdge($"dataEdge{dataEdgeSeq++}", fromId, toId, label));
    }

    private static void ExcludeIsolatedNodes(FlowChart chart, List<string> allWarnings)
    {
        // 業務フローの矢印(Edges)だけでなく、データのやり取りを表す矢印(DataEdges。点線の矢印、
        // およびDB(データストア)シェイプに繋がる矢印)が1本でも繋がっていれば「孤立」とはみなさない
        // (外部システム・DB等、業務フローには参加しないがデータのやり取りだけがある図形をJSONに残すため)。
        var connectedNodeIds = chart.Edges
            .Concat(chart.DataEdges)
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .ToHashSet();
        var isolatedNodes = chart.Nodes.Where(n => !connectedNodeIds.Contains(n.Id)).ToList();
        foreach (var node in isolatedNodes)
            allWarnings.Add(
                $"[孤立シェイプ除外] {WarningFormatting.DescribeShape(node.Text, node.Actors, node.ShapeType)} は矢印が1本も接続されていないため出力対象から除外しました。");
        chart.Nodes.RemoveAll(n => !connectedNodeIds.Contains(n.Id));
        chart.DataEdges.RemoveAll(e => !connectedNodeIds.Contains(e.FromNodeId) || !connectedNodeIds.Contains(e.ToNodeId));
    }

    // 各ノードについて、開始ノード(楕円かつ入次数0)から矢印をたどって到達できるか、および矢印をたどって
    // 終了ノード(楕円かつ出次数0)に到達できるかを調べる。どちらか一方でも満たさないノードは、開始・終了の
    // 一連の流れに属さない断片的な処理とみなし、出力対象(JSON/CSV)から除外する。
    // 判定を連結成分ではなくノード単位で行うのは、開始・終了が揃った一塊の中に「開始からは辿り着けないが
    // 終了には合流するだけの、無関係なプロセス」が混じっているケースを正しく除外するため。
    // (孤立シェイプ除外を先に行っているため、ここで扱うノードは必ず矢印(EdgesまたはDataEdges)を
    // 1本以上持つ。業務フローのEdgesを1本も持たない(DataEdgesのみで繋がっている)ノードは、
    // そもそも業務フローの経路に参加していないため、この判定の対象外として無条件に残す)
    // 戻り値: この除外によって出ていく矢印(YESまたはNO)の行き先ノードごと除外された分岐(ひし形)のID。
    // NormalizeYesNoLabelPairsで、除外が原因の片肺状態と純粋なラベル未検出とを区別するために使う。
    private static HashSet<string> ExcludeNodesOffStartToEndPath(FlowChart chart, List<string> allWarnings)
    {
        var successors = chart.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var predecessors = chart.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        var outDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var edge in chart.Edges)
        {
            if (successors.TryGetValue(edge.FromNodeId, out var succ))
            {
                succ.Add(edge.ToNodeId);
            }
            if (predecessors.TryGetValue(edge.ToNodeId, out var pred))
            {
                pred.Add(edge.FromNodeId);
            }
            if (outDegree.ContainsKey(edge.FromNodeId))
            {
                outDegree[edge.FromNodeId]++;
            }
            if (inDegree.ContainsKey(edge.ToNodeId))
            {
                inDegree[edge.ToNodeId]++;
            }
        }

        var startIds = chart.Nodes.Where(n => n.ShapeType == ShapeType.Ellipse && inDegree[n.Id] == 0).Select(n => n.Id);
        var endIds = chart.Nodes.Where(n => n.ShapeType == ShapeType.Ellipse && outDegree[n.Id] == 0).Select(n => n.Id);

        // 開始ノードから矢印を辿って到達できるノード全体
        var reachableFromStart = BfsMultiSource(startIds, successors);
        // 矢印を辿って終了ノードに到達できるノード全体(終了ノードから矢印を逆に辿って求める)
        var canReachEnd = BfsMultiSource(endIds, predecessors);

        var idsToExclude = new HashSet<string>();
        foreach (var node in chart.Nodes)
        {
            if (inDegree[node.Id] == 0 && outDegree[node.Id] == 0)
            {
                // 業務フローの矢印(Edges)を1本も持たない(DataEdgesのみで繋がっている)ノードは対象外。
                continue;
            }

            bool fromStart = reachableFromStart.Contains(node.Id);
            bool toEnd = canReachEnd.Contains(node.Id);
            if (fromStart && toEnd)
            {
                continue;
            }

            string reason = !fromStart && !toEnd
                ? "開始ノードにも終了ノードにも繋がっていない"
                : !fromStart
                    ? "開始ノードに繋がっていない"
                    : "終了ノードに繋がっていない";

            allWarnings.Add(
                $"[開始/終了ノード無し] {WarningFormatting.DescribeShape(node.Text, node.Actors, node.ShapeType)} は、{reason}ため出力対象から除外しました。");
            idsToExclude.Add(node.Id);
        }

        if (idsToExclude.Count == 0)
        {
            return [];
        }

        // 分岐(ひし形)から出る矢印の行き先だけが除外される場合、その分岐は結果的にYES/NOの
        // 片方しか残らなくなる。これは矢印のラベル判定自体の誤りではなく、この除外が原因のため、
        // 分岐自身は除外されない(=nodeByIdにまだ残っている)ケースだけを記録しておく。
        var nodeById = chart.Nodes.ToDictionary(n => n.Id);
        var diamondsWithExcludedBranch = chart.Edges
            .Where(e => idsToExclude.Contains(e.ToNodeId) && !idsToExclude.Contains(e.FromNodeId)
                        && nodeById.TryGetValue(e.FromNodeId, out var fromNode) && fromNode.ShapeType == ShapeType.Diamond)
            .Select(e => e.FromNodeId)
            .ToHashSet();

        chart.Nodes.RemoveAll(n => idsToExclude.Contains(n.Id));
        chart.Edges.RemoveAll(e => idsToExclude.Contains(e.FromNodeId) || idsToExclude.Contains(e.ToNodeId));
        chart.DataEdges.RemoveAll(e => idsToExclude.Contains(e.FromNodeId) || idsToExclude.Contains(e.ToNodeId));

        return diamondsWithExcludedBranch;
    }

    private static HashSet<string> BfsMultiSource(
        IEnumerable<string> sources, Dictionary<string, List<string>> adjacency)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var source in sources)
        {
            if (visited.Add(source))
            {
                queue.Enqueue(source);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency.GetValueOrDefault(current, []))
            {
                if (visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return visited;
    }

    // 分岐(ひし形)から出るYES/NOラベルは、原則としてYESの矢印とNOの矢印が1本ずつ対で存在するはずである。
    // (矢印自体のテキスト・近くのYES/NOラベル図形どちらから判定した場合も対象)
    // 判定の結果、片方しか見つからない場合はその判定自体を信用できないとみなし、
    // 警告したうえで分岐メモ(CSVの分岐ルート)に使われないようラベルをクリアする。
    private static void NormalizeYesNoLabelPairs(
        FlowChart chart, List<string> warnings, HashSet<string> diamondsWithExcludedBranch)
    {
        var nodeById = chart.Nodes.ToDictionary(n => n.Id);

        var edgesByDiamond = chart.Edges
            .Where(e => nodeById.TryGetValue(e.FromNodeId, out var n) && n.ShapeType == ShapeType.Diamond)
            .GroupBy(e => e.FromNodeId);

        foreach (var group in edgesByDiamond)
        {
            var diamond = nodeById[group.Key];
            var presentLabels = group
                .Select(e => e.Label)
                .Where(l => l is "Y" or "N")
                .Distinct()
                .ToList();

            if (presentLabels.Count != 1)
            {
                continue; // 0件(YES/NO以外のみ)、または2件(対になっている)ならそのまま
            }

            var found = presentLabels[0];

            // 反対側の矢印は、開始/終了ノード無し除外(8.)によってその行き先ごと既に除外されている場合、
            // 別途[開始/終了ノード無し]で理由を警告済みのため、ここでは紛らわしい重複警告を出さない
            // (ラベルをクリアして分岐メモに使わないようにする点は他のケースと同じ)。
            if (!diamondsWithExcludedBranch.Contains(diamond.Id))
            {
                var foundName = found == "Y" ? "YES" : "NO";
                var missingName = found == "Y" ? "NO" : "YES";
                warnings.Add(
                    $"[YES/NO不整合] {WarningFormatting.DescribeNode(diamond)} は {foundName} の矢印しかなく、{missingName} に対応する矢印がありません。分岐メモにはこの分岐のラベルを使用しません。");
            }

            foreach (var edge in group.Where(e => e.Label == found))
            {
                edge.Label = null;
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex NodeNumberPattern = new(@"No\.(\d+)");

    private static int? ExtractNodeNumber(string warning)
    {
        var match = NodeNumberPattern.Match(warning);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Line シェイプの始点・終点座標を返す。
    /// flipH/flipV によって斜め線の向きが変わる。
    /// </summary>
    private static (double startX, double startY, double endX, double endY) GetLineEndpoints(
        Internal.ShapeInfo line)
    {
        double x1 = line.Left, y1 = line.Top;
        double x2 = line.Right, y2 = line.Bottom;

        if (line.FlipH)
        {
            (x1, x2) = (x2, x1);
        }
        if (line.FlipV)
        {
            (y1, y2) = (y2, y1);
        }

        return (x1, y1, x2, y2);
    }

    private static WorksheetPart? FindWorksheetPart(WorkbookPart workbookPart, string sheetName)
    {
        var sheet = workbookPart.Workbook?.Sheets
            ?.Elements<Sheet>()
            .FirstOrDefault(s => s.Name?.Value == sheetName);

        if (sheet?.Id?.Value == null)
        {
            return null;
        }
        return workbookPart.GetPartById(sheet.Id.Value) as WorksheetPart;
    }
}
