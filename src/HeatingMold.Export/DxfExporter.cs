using HeatingMold.Core.Geometry;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace HeatingMold.Export;

/// <summary>
/// S3 — 투영 2D(면 중심 법선 정사영) 기반 DXF 내보내기.
/// 레이어: MOLD_OUTLINE(면 경계) / HEAT_WIRE(사행, 선폭) / DIMENSION(치수).
/// </summary>
public static class DxfExporter
{
    public const string MoldOutlineLayer = "MOLD_OUTLINE";
    public const string HeatWireLayer = "HEAT_WIRE";
    public const string DimensionLayer = "DIMENSION";

    public const string HiddenLayer = "HIDDEN_LINE";

    /// <summary>Place multiple orthographic views into one DXF, laid out left-to-right with a gap.</summary>
    public static void ExportViews(string path, IReadOnlyList<DxfView> views,
        double gap = 60, double dimOffset = 20)
    {
        var doc = new DxfDocument();
        var moldLayer = new Layer(MoldOutlineLayer) { Color = AciColor.Cyan };
        var hiddenLayer = new Layer(HiddenLayer) { Color = new AciColor(8) };
        var heatLayer = new Layer(HeatWireLayer) { Color = AciColor.Red };
        var dimLayer = new Layer(DimensionLayer) { Color = AciColor.Yellow };
        doc.Layers.Add(moldLayer);
        doc.Layers.Add(hiddenLayer);
        doc.Layers.Add(heatLayer);
        doc.Layers.Add(dimLayer);

        double cursorX = 0;
        foreach (DxfView view in views)
        {
            var all = view.AllPolylines().ToList();
            if (all.Count == 0) continue;

            double minX = all.SelectMany(p => p.Points).Min(v => v.X);
            double minY = all.SelectMany(p => p.Points).Min(v => v.Y);
            double maxX = all.SelectMany(p => p.Points).Max(v => v.X);
            double maxY = all.SelectMany(p => p.Points).Max(v => v.Y);
            var off = new Vec2(cursorX - minX, -minY);

            AddPolylines(doc, view.VisibleEdges, moldLayer, off);
            AddPolylines(doc, view.HiddenEdges, hiddenLayer, off);
            AddPolylines(doc, view.HeatBands, heatLayer, off);
            AddPolylines(doc, view.HeatLines, heatLayer, off);

            double w = maxX - minX, h = maxY - minY;
            var wDim = new LinearDimension(
                new Vector2(minX + off.X, minY + off.Y),
                new Vector2(maxX + off.X, minY + off.Y), -dimOffset, 0) { Layer = dimLayer };
            var hDim = new LinearDimension(
                new Vector2(minX + off.X, minY + off.Y),
                new Vector2(minX + off.X, maxY + off.Y), -dimOffset, 90) { Layer = dimLayer };
            doc.Entities.Add(wDim);
            doc.Entities.Add(hDim);

            cursorX += w + gap;
        }

        doc.Save(path);
    }

    private static void AddPolylines(DxfDocument doc, IEnumerable<DxfPolyline> polys, Layer layer, Vec2 off)
    {
        foreach (DxfPolyline pl in polys)
        {
            if (pl.Points.Count < 2) continue;
            var poly = new Polyline2D(
                pl.Points.Select(v => new Vector2(v.X + off.X, v.Y + off.Y)),
                isClosed: pl.Closed) { Layer = layer };
            doc.Entities.Add(poly);
        }
    }
}

/// <summary>DXF 2D polyline (points + closed flag).</summary>
public sealed class DxfPolyline
{
    public IReadOnlyList<Vec2> Points { get; init; } = Array.Empty<Vec2>();
    public bool Closed { get; init; }
}

/// <summary>One DXF view: visible / hidden / heat polylines.</summary>
public sealed class DxfView
{
    public List<DxfPolyline> VisibleEdges { get; } = new();
    public List<DxfPolyline> HiddenEdges { get; } = new();
    public List<DxfPolyline> HeatLines { get; } = new();
    public List<DxfPolyline> HeatBands { get; } = new();

    public IEnumerable<DxfPolyline> AllPolylines()
    {
        foreach (var p in VisibleEdges) yield return p;
        foreach (var p in HiddenEdges) yield return p;
        foreach (var p in HeatBands) yield return p;
        foreach (var p in HeatLines) yield return p;
    }
}
