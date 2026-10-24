using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using netDxf.Units;

namespace HeatingMold.Infrastructure.Export;

/// <summary>검증된 설계와 3차원 측정 결과를 밀리미터 단위 DXF 파일로 출력합니다.</summary>
public sealed class DxfDesignExporter : IDesignExporter
{
    private readonly DxfProjectionBuilder _projectionBuilder;
    private readonly SpecificationDataBuilder _specificationBuilder;
    private readonly Action<DxfDocument, string> _saveDocument;

    /// <summary>DXF 투영과 사양 표 생성기를 연결합니다.</summary>
    public DxfDesignExporter(
        DxfProjectionBuilder? projectionBuilder = null,
        SpecificationDataBuilder? specificationBuilder = null)
        : this(projectionBuilder, specificationBuilder, static (document, path) => document.Save(path))
    {
    }

    /// <summary>DXF 저장 경계를 테스트 대역으로 교체할 수 있게 초기화합니다.</summary>
    internal DxfDesignExporter(
        DxfProjectionBuilder? projectionBuilder,
        SpecificationDataBuilder? specificationBuilder,
        Action<DxfDocument, string> saveDocument)
    {
        _projectionBuilder = projectionBuilder ?? new DxfProjectionBuilder();
        _specificationBuilder = specificationBuilder ?? new SpecificationDataBuilder();
        _saveDocument = saveDocument ?? throw new ArgumentNullException(nameof(saveDocument));
    }

    /// <summary>경계, 발열선, 연결 경로와 계산 사양을 DXF로 원자적으로 저장합니다.</summary>
    public OperationResult<ExportReceipt> Export(
        ExportSnapshot snapshot,
        ExportOptions options,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Format != ExportFormat.Dxf)
            return Failure("UnsupportedExportFormat", "DXF exporter는 DXF 형식만 출력합니다.");
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "DXF 출력 경로가 비어 있습니다.");

        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DxfProjectedDesignData projectedDesign = _projectionBuilder.Build(snapshot);
            DxfSpecificationTable specificationTable = _specificationBuilder.Build(snapshot);
            DxfDocument document = CreateDocument(projectedDesign, specificationTable, snapshot, cancellationToken);

            string fullPath = Path.GetFullPath(path.Trim());
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                return Failure("InvalidPath", "DXF 출력 경로에 상위 폴더가 없습니다.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.dxf");
            _saveDocument(document, temporaryPath);
            ExportReceipt receipt = AtomicExportFile.Commit(
                temporaryPath,
                fullPath,
                ExportFormat.Dxf,
                cancellationToken);
            temporaryPath = null;

            return OperationResult<ExportReceipt>.Success(receipt);
        }
        catch (OperationCanceledException)
        {
            return Failure("OperationCancelled", "DXF 출력이 취소되었습니다.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return Failure("DxfExportFailed", exception.Message);
        }
        finally
        {
            AtomicExportFile.TryDelete(temporaryPath);
        }
    }

    private static DxfDocument CreateDocument(
        DxfProjectedDesignData projectedDesign,
        DxfSpecificationTable specificationTable,
        ExportSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var document = new DxfDocument();
        document.DrawingVariables.InsUnits = DrawingUnits.Millimeters;
        Dictionary<string, Layer> layers = DxfLayerCatalog.CreateLayers()
            .ToDictionary(layer => layer.Name, StringComparer.Ordinal);
        foreach (Layer layer in layers.Values)
            document.Layers.Add(layer);

        foreach (DxfPolylineData item in projectedDesign.Polylines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Points.Count < 2 || !layers.TryGetValue(item.LayerName, out Layer? layer))
                continue;

            var polyline = new Polyline2D(
                item.Points.Select(point => new Vector2(point.X, point.Y)),
                isClosed: item.IsClosed)
            {
                Layer = layer
            };
            document.Entities.Add(polyline);
        }

        foreach (DxfTextData label in projectedDesign.Labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!layers.TryGetValue(label.LayerName, out Layer? layer))
                continue;

            document.Entities.Add(new Text(
                label.Value,
                new Vector3(label.Position.X, label.Position.Y, 0),
                label.HeightMm)
            {
                Layer = layer
            });
        }

        AddSpecificationTable(document, layers[DxfLayerCatalog.Specification], projectedDesign, specificationTable, snapshot, cancellationToken);

        return document;
    }

    private static void AddSpecificationTable(
        DxfDocument document,
        Layer layer,
        DxfProjectedDesignData projectedDesign,
        DxfSpecificationTable table,
        ExportSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        IEnumerable<Vec2> geometryPoints = projectedDesign.Polylines.SelectMany(item => item.Points)
            .Concat(projectedDesign.Labels.Select(label => label.Position));
        double maximumX = geometryPoints.Select(point => point.X).DefaultIfEmpty(0).Max();
        double maximumY = geometryPoints.Select(point => point.Y).DefaultIfEmpty(0).Max();
        double left = maximumX + 20;
        double top = maximumY + 18;
        double[] columnWidths = [32, 52, 62, 58];
        double tableWidth = columnWidths.Sum();
        const double headerHeight = 6;
        const double rowHeight = 4;
        const double textHeight = 2;

        document.Entities.Add(new Text(
            $"HeatingMold — {snapshot.Project.Name} (revision {snapshot.Project.Revision.Value})",
            new Vector3(left, top, 0),
            3)
        {
            Layer = layer
        });

        double headerTop = top - 7;
        double currentTop = headerTop;
        AddHorizontalLine(currentTop);
        AddTextCells(table.Headers, currentTop, headerHeight, isHeader: true);
        currentTop -= headerHeight;
        AddHorizontalLine(currentTop);

        foreach (DxfSpecificationRow row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddTextCells([row.Scope, row.Item, row.Value, row.UnitOrStatus], currentTop, rowHeight, isHeader: false);
            currentTop -= rowHeight;
            AddHorizontalLine(currentTop);
        }

        double x = left;
        AddVerticalLine(x, headerTop, currentTop);
        foreach (double width in columnWidths)
        {
            x += width;
            AddVerticalLine(x, headerTop, currentTop);
        }

        double noteTop = currentTop - 7;
        string noteText = string.Join("\\P", table.Notes);
        var notes = new MText(noteText, new Vector3(left, noteTop, 0), 2, tableWidth)
        {
            Layer = layer
        };
        document.Entities.Add(notes);

        void AddTextCells(IReadOnlyList<string> cells, double rowTop, double height, bool isHeader)
        {
            if (cells.Count != columnWidths.Length)
                throw new InvalidOperationException("DXF 사양표 셀 수가 열 수와 일치하지 않습니다.");

            double cellLeft = left;
            for (int index = 0; index < cells.Count; index++)
            {
                double cellTextHeight = isHeader ? 2.2 : textHeight;
                double baseline = rowTop - height + (height - cellTextHeight) / 2;
                document.Entities.Add(new Text(cells[index], new Vector3(cellLeft + 1, baseline, 0), cellTextHeight)
                {
                    Layer = layer
                });
                cellLeft += columnWidths[index];
            }
        }

        void AddHorizontalLine(double y) => document.Entities.Add(
            new Line(new Vector3(left, y, 0), new Vector3(left + tableWidth, y, 0)) { Layer = layer });

        void AddVerticalLine(double x, double topY, double bottomY) => document.Entities.Add(
            new Line(new Vector3(x, topY, 0), new Vector3(x, bottomY, 0)) { Layer = layer });
    }

    private static OperationResult<ExportReceipt> Failure(string code, string evidence) =>
        OperationResult<ExportReceipt>.Failure(
        [new ValidationIssue(code, ValidationSeverity.Error, $"Infrastructure.{code}", [], "dxf-export-v1", evidence: [evidence])]);
}
