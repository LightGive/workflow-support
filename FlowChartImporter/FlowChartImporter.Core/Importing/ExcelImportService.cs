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

    public ImportResult Import(string filePath, string sheetName)
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
        var (shapes, connectors, extractWarnings) = extractor.Extract(worksheetPart);
        allWarnings.AddRange(extractWarnings);

        // Line シェイプはコネクタとして扱い、ノードには含めない
        var lineShapes = shapes.Where(s => s.ShapeType == ShapeType.Line).ToList();
        var documentShapes = shapes.Where(s => s.ShapeType == ShapeType.Document).ToList();

        // 矩形(Rectangle)はプロセスを表す図形だが、枠線が無いものはプロセスではなく、
        // 分岐先のYES/NOラベルやその他の注記(テキストボックス相当)として使われている。
        bool IsBorderlessRectangle(Internal.ShapeInfo s) => s.ShapeType == ShapeType.Rectangle && s.HasNoLine;

        // テキストボックス(Excelの「テキストボックス」挿入機能で作られた図形)はノードには含めない。
        // YES/NOテキストは分岐のラベル判定に、それ以外は近くのノードの備考として使う。
        bool IsYesNoLabelCandidate(Internal.ShapeInfo s) => s.IsTextBox || IsBorderlessRectangle(s);

        var yesNoTextBoxes = shapes
            .Where(s => IsYesNoLabelCandidate(s) && BranchLabelResolver.MatchYesNo(s.Text) != null)
            .ToList();

        // ノードへの備考(メモ)は「[」(角かっこ)の図形に書かれたテキストのみを対象とする。
        // 「[」自体は空で、テキストボックスや線なしRectangleとグループ化されている場合は、
        // その同じグループ内の図形のテキストも備考として取り込む。
        var remarkTextBoxes = shapes
            .Where(s => s.ShapeType == ShapeType.Bracket)
            .Select(bracket =>
            {
                if (bracket.GroupId == null)
                {
                    return bracket;
                }

                var groupedText = shapes
                    .Where(s => s.GroupId == bracket.GroupId && s.XmlId != bracket.XmlId
                                && (s.IsTextBox || IsBorderlessRectangle(s)))
                    .Select(s => s.Text.Trim())
                    .Where(t => t.Length > 0);

                var combined = string.Join("\n",
                    new[] { bracket.Text.Trim() }.Concat(groupedText).Where(t => t.Length > 0));

                return combined == bracket.Text ? bracket : bracket with { Text = combined };
            })
            .ToList();

        var nodeShapes = shapes.Where(s => s.ShapeType != ShapeType.Document
                                        && s.ShapeType != ShapeType.Line
                                        && s.ShapeType != ShapeType.Bracket
                                        && !s.IsTextBox
                                        && !IsBorderlessRectangle(s)).ToList();

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

        // 3. 担当部署を判定
        var deptDetector = new DepartmentDetector();
        var deptRanges = deptDetector.Detect(workbookPart, worksheetPart);

        foreach (var (shape, node) in nodeMap)
        {
            node.Departments = deptDetector.GetDepartments(deptRanges, shape.AnchorFromRow, shape.AnchorToRow);
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

        // 6. ノード番号を採番
        _numberingStrategy.AssignNumbers(chart.Nodes);

        // 7. 検証
        var validator = new FlowChartValidator();
        var validationWarnings = validator.Validate(chart, _settings);
        allWarnings.AddRange(validationWarnings);

        return new ImportResult(chart, allWarnings);
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
