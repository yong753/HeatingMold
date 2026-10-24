using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace HeatingMold.Infrastructure.Export;

/// <summary>WPF에서 전달한 HLR 정사영 도면을 레이어별 DXF로 저장합니다.</summary>
public sealed class OrthographicDxfExporter : IOrthographicDxfExporter
{
    private const string HiddenLineLayer = "HIDDEN_LINE";
    private const string DimensionLayer = "DIMENSION";
    private const double ViewGapMm = 60;
    private const double DimensionOffsetMm = 20;

    /// <summary>정사영 도면을 배치하고 파일 해시가 포함된 영수증을 반환합니다.</summary>
    public OperationResult<ExportReceipt> ExportViews(
        string path,
        IReadOnlyList<OrthographicDxfView> views,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "DXF 출력 경로가 비어 있습니다.");
        if (views.Any(view => view is null))
            return Failure("InvalidOrthographicView", "정사영 도면 목록은 null 항목을 포함할 수 없습니다.");

        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DxfDocument document = CreateDocument(views, cancellationToken);
            string fullPath = Path.GetFullPath(path.Trim());
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                return Failure("InvalidPath", "DXF 출력 경로에 상위 폴더가 없습니다.");

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.dxf");
            document.Save(temporaryPath);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or InvalidOperationException)
        {
            return Failure("OrthographicDxfExportFailed", exception.Message);
        }
        finally
        {
            AtomicExportFile.TryDelete(temporaryPath);
        }
    }

    private static DxfDocument CreateDocument(
        IReadOnlyList<OrthographicDxfView> views,
        CancellationToken cancellationToken)
    {
        var document = new DxfDocument();
        Layer moldLayer = new(DxfLayerCatalog.MoldOutline) { Color = AciColor.Cyan };
        Layer hiddenLayer = new(HiddenLineLayer) { Color = new AciColor(8) };
        Layer heatingLayer = new(DxfLayerCatalog.HeatingWire) { Color = AciColor.Red };
        Layer dimensionLayer = new(DimensionLayer) { Color = AciColor.Yellow };
        document.Layers.Add(moldLayer);
        document.Layers.Add(hiddenLayer);
        document.Layers.Add(heatingLayer);
        document.Layers.Add(dimensionLayer);

        double cursorX = 0;
        foreach (OrthographicDxfView view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrthographicDxfPolyline[] polylines = view.AllPolylines().ToArray();
            if (polylines.Length == 0)
                continue;

            double minX = polylines.SelectMany(polyline => polyline.Points).Min(point => point.X);
            double minY = polylines.SelectMany(polyline => polyline.Points).Min(point => point.Y);
            double maxX = polylines.SelectMany(polyline => polyline.Points).Max(point => point.X);
            double maxY = polylines.SelectMany(polyline => polyline.Points).Max(point => point.Y);
            var offset = new Vec2(cursorX - minX, -minY);

            AddPolylines(document, view.VisibleEdges, moldLayer, offset, cancellationToken);
            AddPolylines(document, view.HiddenEdges, hiddenLayer, offset, cancellationToken);
            AddPolylines(document, view.HeatBands, heatingLayer, offset, cancellationToken);
            AddPolylines(document, view.HeatLines, heatingLayer, offset, cancellationToken);

            document.Entities.Add(new LinearDimension(
                new Vector2(minX + offset.X, minY + offset.Y),
                new Vector2(maxX + offset.X, minY + offset.Y),
                -DimensionOffsetMm,
                0)
            {
                Layer = dimensionLayer
            });
            document.Entities.Add(new LinearDimension(
                new Vector2(minX + offset.X, minY + offset.Y),
                new Vector2(minX + offset.X, maxY + offset.Y),
                -DimensionOffsetMm,
                90)
            {
                Layer = dimensionLayer
            });

            cursorX += maxX - minX + ViewGapMm;
        }

        return document;
    }

    private static void AddPolylines(
        DxfDocument document,
        IEnumerable<OrthographicDxfPolyline> polylines,
        Layer layer,
        Vec2 offset,
        CancellationToken cancellationToken)
    {
        foreach (OrthographicDxfPolyline item in polylines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var polyline = new Polyline2D(
                item.Points.Select(point => new Vector2(point.X + offset.X, point.Y + offset.Y)),
                item.IsClosed)
            {
                Layer = layer
            };
            document.Entities.Add(polyline);
        }
    }

    private static OperationResult<ExportReceipt> Failure(string code, string evidence) =>
        OperationResult<ExportReceipt>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Infrastructure.{code}",
            [],
            "orthographic-dxf-export-v1",
            evidence: [evidence])]);
}
