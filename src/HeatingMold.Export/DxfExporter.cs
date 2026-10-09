using HeatingMold.Core.Geometry;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace HeatingMold.Export;

/// <summary>
/// S3 — UV 좌표(평면 전개 2D) 기반 DXF 내보내기.
/// 레이어: MOLD_OUTLINE(면 경계) / HEAT_WIRE(사행, 선폭) / DIMENSION(치수).
/// </summary>
public static class DxfExporter
{
    public const string MoldOutlineLayer = "MOLD_OUTLINE";
    public const string HeatWireLayer = "HEAT_WIRE";
    public const string DimensionLayer = "DIMENSION";

    public static void Export(
        string path,
        IReadOnlyList<IReadOnlyList<Vec2>> outlineRings,
        IReadOnlyList<Vec2> heatPath,
        double ribbonWidth,
        double dimOffset = 15)
    {
        var doc = new DxfDocument();
        var moldLayer = new Layer(MoldOutlineLayer) { Color = AciColor.Cyan };
        var heatLayer = new Layer(HeatWireLayer) { Color = AciColor.Red };
        var dimLayer = new Layer(DimensionLayer) { Color = AciColor.Yellow };
        doc.Layers.Add(moldLayer);
        doc.Layers.Add(heatLayer);
        doc.Layers.Add(dimLayer);

        foreach (IReadOnlyList<Vec2> ring in outlineRings)
        {
            if (ring.Count < 2) continue;
            var poly = new Polyline2D(ring.Select(v => new Vector2(v.X, v.Y)), isClosed: true)
            {
                Layer = moldLayer,
            };
            doc.Entities.Add(poly);
        }

        if (heatPath.Count >= 2)
        {
            var heat = new Polyline2D(heatPath.Select(v => new Vector2(v.X, v.Y)), isClosed: false)
            {
                Layer = heatLayer,
            };
            doc.Entities.Add(heat);

            // 선폭 = 리본 밴드 외곽선 (같은 HEAT_WIRE 레이어)
            foreach (List<Vec2> band in RegionOps.StrokePath(heatPath, ribbonWidth))
            {
                if (band.Count < 3) continue;
                var bandPoly = new Polyline2D(band.Select(v => new Vector2(v.X, v.Y)), isClosed: true)
                {
                    Layer = heatLayer,
                };
                doc.Entities.Add(bandPoly);
            }

            double minX = heatPath.Min(v => v.X), maxX = heatPath.Max(v => v.X);
            double minY = heatPath.Min(v => v.Y), maxY = heatPath.Max(v => v.Y);

            var widthDim = new LinearDimension(new Vector2(minX, minY), new Vector2(maxX, minY), -dimOffset, 0)
            {
                Layer = dimLayer,
            };
            var heightDim = new LinearDimension(new Vector2(minX, minY), new Vector2(minX, maxY), -dimOffset, 90)
            {
                Layer = dimLayer,
            };
            doc.Entities.Add(widthDim);
            doc.Entities.Add(heightDim);
        }

        doc.Save(path);
    }
}
