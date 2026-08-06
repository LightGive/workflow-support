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
        new DocumentShapeAssociator().Associate(documentShapes, nodeMap);
        new RemarkAssociator().Associate(remarkTextBoxes, nodeMap, _settings.BranchLabelSearchRadiusPoints);

        // 6. コネクタをエッジに変換
        ResolveEdges(chart, connectors, nodeShapes, xmlIdToNodeId, nodeMap, yesNoTextBoxes);

        // 7. 矢印で他のノードと接続されていない孤立したシェイプは、
        // 業務フローとして意味を持たないため出力対象(JSON/CSV)から除外する。
        // (メモ・関連ファイルはノードそのものではなく、既に付随情報として関連ノードに統合済みのため対象外)
        ExcludeIsolatedNodes(chart, allWarnings);

        // 8. ノード番号を採番(矢印で接続されたノードのみが対象)
        _numberingStrategy.AssignNumbers(chart.Nodes);

        // 9. 検証
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
                    x: shape.Left,
                    y: shape.Top),
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
                $"[孤立シェイプ除外] '{Truncate(node.Text)}' (実施主体: {string.Join("/", node.Actors)}, タイプ: {node.ShapeType}) は矢印が1本も接続されていないため出力対象から除外しました。");
        chart.Nodes.RemoveAll(n => !connectedNodeIds.Contains(n.Id));
    }

    private static readonly System.Text.RegularExpressions.Regex NodeNumberPattern = new(@"No\.(\d+)");

    private static int? ExtractNodeNumber(string warning)
    {
        var match = NodeNumberPattern.Match(warning);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string Truncate(string text, int max = 20) =>
        text.Length <= max ? text : text[..max] + "…";

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
