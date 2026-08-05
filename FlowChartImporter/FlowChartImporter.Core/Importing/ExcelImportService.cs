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
    public ImportResult Import(string filePath, string sheetName, int minRow = 1)
    {
        using var doc = SpreadsheetDocument.Open(filePath, isEditable: false);
        var workbookPart = doc.WorkbookPart
            ?? throw new InvalidOperationException("WorkbookPart が見つかりません。");

        var worksheetPart = FindWorksheetPart(workbookPart, sheetName)
            ?? throw new InvalidOperationException($"シート '{sheetName}' が見つかりません。");

        var chart = new FlowChart(sheetName);
        var allWarnings = new List<string>();

        // 1. 図形・コネクタを抽出
        var extractor = new ExcelShapeExtractor();
        var (shapes, connectors, extractWarnings) = extractor.Extract(worksheetPart, minRowIndex: minRow - 1);
        allWarnings.AddRange(extractWarnings);

        // Line シェイプはコネクタとして扱い、ノードには含めない
        var lineShapes = shapes.Where(s => s.ShapeType == ShapeType.Line).ToList();
        var documentShapes = shapes.Where(s => s.ShapeType == ShapeType.Document).ToList();

        // 矩形(Rectangle)はプロセスを表す図形だが、枠線が無いものはプロセスではなく、
        // 分岐先のYES/NOラベルやその他の注記(テキストボックス相当)として使われている。
        bool IsBorderlessRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.HasNoLine;

        // 点線で囲われた矩形もプロセスとしては扱わない。
        bool IsDashedRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.IsDashed;

        // テキストボックス(Excelの「テキストボックス」挿入機能で作られた図形)はノードには含めない。
        // YES/NOテキストは分岐のラベル判定に、それ以外は近くのノードの備考として使う。
        bool IsYesNoLabelCandidate(Internal.ShapeInfo s) => s.IsTextBox || IsBorderlessRectangle(s);

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

        // Line シェイプを ConnectorInfo に変換してコネクタリストに追加
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
                IsElbow = line.IsElbowConnector,
                IsDashed = line.IsDashed,
            });
        }

        // 2. ノードを生成
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

        // 3. 実施主体(部署・システム・他社等)を判定
        var actorDetector = new ActorDetector();
        var actorRanges = actorDetector.Detect(workbookPart, worksheetPart);

        foreach (var (shape, node) in nodeMap)
        {
            node.Actors = actorDetector.GetActors(actorRanges, shape.AnchorFromRow, shape.AnchorToRow);
        }

        // 4. 書類シェイプを親ノードに紐づけ
        var associator = new DocumentShapeAssociator();
        associator.Associate(documentShapes, nodeMap);

        // 4b. YES/NO以外のテキストボックスを最も近いノードの備考として紐づけ
        var remarkAssociator = new RemarkAssociator();
        remarkAssociator.Associate(remarkTextBoxes, nodeMap, _settings.BranchLabelSearchRadiusPoints);

        // 5. コネクタをエッジに変換
        // 点線の矢印はデータのやり取りを表すものであり、業務フローの流れではないため対象外とする
        var flowConnectors = connectors.Where(c => !c.IsDashed).ToList();
        var resolver = new ConnectorResolver(_settings.ConnectionTolerancePoints);
        var connections = resolver.Resolve(flowConnectors, nodeShapes, xmlIdToNodeId);

        // 矢印自体にテキストが無い分岐について、近くのYES/NOテキストボックスからラベルを補う
        var nodeShapeById = nodeMap.ToDictionary(t => t.Node.Id, t => t.Shape);
        BranchLabelResolver.ResolveMissingLabels(
            connections, nodeShapeById, yesNoTextBoxes, _settings.BranchLabelSearchRadiusPoints);

        int edgeSeq = 1;
        foreach (var (fromId, toId, label, _, _, _, _, _) in connections)
            chart.Edges.Add(new FlowEdge($"edge{edgeSeq++}", fromId, toId, label));

        // 6. 矢印で他のノードと接続されていない孤立したシェイプは、
        // 業務フローとして意味を持たないため出力対象(JSON/CSV)から除外する。
        // (メモ・入出力ファイルはノードそのものではなく、既に付随情報として関連ノードに統合済みのため対象外)
        var connectedNodeIds = chart.Edges
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .ToHashSet();
        var isolatedNodes = chart.Nodes.Where(n => !connectedNodeIds.Contains(n.Id)).ToList();
        foreach (var node in isolatedNodes)
            allWarnings.Add(
                $"[孤立シェイプ除外] '{Truncate(node.Text)}' (実施主体: {string.Join("/", node.Actors)}, タイプ: {node.ShapeType}) は矢印が1本も接続されていないため出力対象から除外しました。");
        chart.Nodes.RemoveAll(n => !connectedNodeIds.Contains(n.Id));

        // 7. ノード番号を採番(矢印で接続されたノードのみが対象)
        _numberingStrategy.AssignNumbers(chart.Nodes);

        // 8. 検証
        var validator = new FlowChartValidator();
        var validationWarnings = validator.Validate(chart, _settings);
        allWarnings.AddRange(validationWarnings);

        // 警告は採番した番号("No.N")の昇順で表示する。番号を含まない警告は末尾にまとめる。
        var sortedWarnings = allWarnings
            .OrderBy(w => ExtractNodeNumber(w) ?? int.MaxValue)
            .ToList();

        return new ImportResult(chart, sortedWarnings);
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
