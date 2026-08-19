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
        var (lineShapes, documentShapes, nodeShapes, yesNoTextBoxes, remarkTextBoxes) = ClassifyShapes(shapes);
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

        // 8. 矢印でつながった一塊(処理の集まり)の中に、開始・終了ノード(楕円かつ入次数/出次数が0)が
        // 1つも無い場合、その一塊は開始・終了が不明な断片的な処理とみなし、出力対象から除外する。
        ExcludeFlowsWithoutStartOrEnd(chart, allWarnings);

        // 9. ノード番号を採番(矢印で接続されたノードのみが対象)
        _numberingStrategy.AssignNumbers(chart.Nodes);

        // 10. 分岐(ひし形)のYES/NOは必ず対になっているはずである。
        // 判定の結果、片方(YESのみ/NOのみ)しか見つからない場合はその判定を信用せず、
        // 警告したうえで分岐メモ(CSVの分岐ルート)には使わないようにする。
        NormalizeYesNoLabelPairs(chart, allWarnings);

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
        ) ClassifyShapes(List<Internal.ShapeInfo> shapes)
    {
        bool IsBorderlessRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.HasNoLine;
        bool IsDashedRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.IsDashed;
        bool IsYesNoLabelCandidate(Internal.ShapeInfo s) => s.IsTextBox || IsBorderlessRectangle(s);

        var lineShapes = shapes.Where(s => s.ShapeType == ShapeType.Line).ToList();
        var documentShapes = shapes.Where(s => s.ShapeType == ShapeType.Document).ToList();

        var yesNoTextBoxes = shapes
            .Where(s => IsYesNoLabelCandidate(s) && BranchLabelResolver.MatchYesNo(s.Text) != null)
            .ToList();

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
        // 点線の矢印はデータのやり取りを表すものであり、業務フローの流れではないため対象外とする
        var flowConnectors = connectors.Where(c => !c.IsDashed).ToList();
        var resolver = new ConnectorResolver(_settings.ConnectionTolerancePoints);
        var connections = resolver.Resolve(flowConnectors, nodeShapes, xmlIdToNodeId);

        // 矢印自体にテキストが無い分岐について、近くのYES/NOテキストボックスからラベルを補う
        var nodeShapeById = nodeMap.ToDictionary(t => t.Node.Id, t => t.Shape);
        BranchLabelResolver.ResolveMissingLabels(
            connections, nodeShapeById, yesNoTextBoxes, _settings.BranchLabelSearchRadiusPoints);

        int edgeSeq = 1;
        foreach (var (fromId, toId, label, _, _, _, _) in connections)
            chart.Edges.Add(new FlowEdge($"edge{edgeSeq++}", fromId, toId, label));
    }

    private static void ExcludeIsolatedNodes(FlowChart chart, List<string> allWarnings)
    {
        var connectedNodeIds = chart.Edges
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .ToHashSet();
        var isolatedNodes = chart.Nodes.Where(n => !connectedNodeIds.Contains(n.Id)).ToList();
        foreach (var node in isolatedNodes)
            allWarnings.Add(
                $"[孤立シェイプ除外] {WarningFormatting.DescribeShape(node.Text, node.Actors, node.ShapeType)} は矢印が1本も接続されていないため出力対象から除外しました。");
        chart.Nodes.RemoveAll(n => !connectedNodeIds.Contains(n.Id));
    }

    // 矢印でつながった一塊(連結成分)ごとに、開始ノード(楕円かつ入次数0)・終了ノード(楕円かつ出次数0)が
    // それぞれ1つ以上あるかを調べる。どちらか一方でも無い一塊は、どこから始まりどこで終わるか不明な
    // 断片的な処理とみなし、出力対象(JSON/CSV)から除外する。
    // (孤立シェイプ除外を先に行っているため、ここで扱う一塊は必ず矢印を1本以上持つ)
    private static void ExcludeFlowsWithoutStartOrEnd(FlowChart chart, List<string> allWarnings)
    {
        var adjacency = chart.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        var outDegree = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var edge in chart.Edges)
        {
            if (adjacency.TryGetValue(edge.FromNodeId, out var fromNeighbors))
            {
                fromNeighbors.Add(edge.ToNodeId);
            }
            if (adjacency.TryGetValue(edge.ToNodeId, out var toNeighbors))
            {
                toNeighbors.Add(edge.FromNodeId);
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

        var nodeById = chart.Nodes.ToDictionary(n => n.Id);
        var visited = new HashSet<string>();
        var idsToExclude = new HashSet<string>();

        foreach (var node in chart.Nodes)
        {
            if (!visited.Add(node.Id))
            {
                continue;
            }

            var component = CollectConnectedComponent(node.Id, adjacency, visited);
            bool hasStart = component.Any(id => nodeById[id].ShapeType == ShapeType.Ellipse && inDegree[id] == 0);
            bool hasEnd = component.Any(id => nodeById[id].ShapeType == ShapeType.Ellipse && outDegree[id] == 0);
            if (hasStart && hasEnd)
            {
                continue;
            }

            string missingDescription = !hasStart && !hasEnd
                ? "開始ノードも終了ノードも無い"
                : !hasStart
                    ? "開始ノードが無い"
                    : "終了ノードが無い";

            foreach (var id in component)
            {
                var excludedNode = nodeById[id];
                allWarnings.Add(
                    $"[開始/終了ノード無し] {WarningFormatting.DescribeShape(excludedNode.Text, excludedNode.Actors, excludedNode.ShapeType)} は、{missingDescription}一連の処理に含まれるため出力対象から除外しました。");
                idsToExclude.Add(id);
            }
        }

        if (idsToExclude.Count == 0)
        {
            return;
        }

        chart.Nodes.RemoveAll(n => idsToExclude.Contains(n.Id));
        chart.Edges.RemoveAll(e => idsToExclude.Contains(e.FromNodeId) || idsToExclude.Contains(e.ToNodeId));
    }

    private static List<string> CollectConnectedComponent(
        string startId, Dictionary<string, List<string>> adjacency, HashSet<string> visited)
    {
        var component = new List<string> { startId };
        var queue = new Queue<string>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency.GetValueOrDefault(current, []))
            {
                if (visited.Add(next))
                {
                    component.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        return component;
    }

    // 分岐(ひし形)から出るYES/NOラベルは、原則としてYESの矢印とNOの矢印が1本ずつ対で存在するはずである。
    // (矢印自体のテキスト・近くのYES/NOラベル図形どちらから判定した場合も対象)
    // 判定の結果、片方しか見つからない場合はその判定自体を信用できないとみなし、
    // 警告したうえで分岐メモ(CSVの分岐ルート)に使われないようラベルをクリアする。
    private static void NormalizeYesNoLabelPairs(FlowChart chart, List<string> warnings)
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
            var foundName = found == "Y" ? "YES" : "NO";
            var missingName = found == "Y" ? "NO" : "YES";
            warnings.Add(
                $"[YES/NO不整合] {WarningFormatting.DescribeNode(diamond)} は {foundName} の矢印しかなく、{missingName} に対応する矢印がありません。分岐メモにはこの分岐のラベルを使用しません。");

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
