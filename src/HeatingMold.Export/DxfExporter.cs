using System.Globalization;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using netDxf;
using netDxf.Entities;
using netDxf.Header;
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
    public const string ElectricalSpecLayer = "ELECTRICAL_SPEC";

    private const string ElectricalSpecTextStyle = "ELECTRICAL_SPEC_TEXT";

    /// <summary>
    /// Place multiple orthographic views into one DXF, laid out left-to-right with a gap.
    /// When a spec table is supplied the document is written as R2010 so Korean text is stored as UTF-8.
    /// </summary>
    public static void ExportViews(string path, IReadOnlyList<DxfView> views,
        double gap = 60, double dimOffset = 20, ElectricalSpecTable? specTable = null)
    {
        var doc = specTable == null ? new DxfDocument() : new DxfDocument(DxfVersion.AutoCad2010);
        var moldLayer = new Layer(MoldOutlineLayer) { Color = AciColor.Cyan };
        var hiddenLayer = new Layer(HiddenLayer) { Color = new AciColor(8) };
        var heatLayer = new Layer(HeatWireLayer) { Color = AciColor.Red };
        var dimLayer = new Layer(DimensionLayer) { Color = AciColor.Yellow };
        doc.Layers.Add(moldLayer);
        doc.Layers.Add(hiddenLayer);
        doc.Layers.Add(heatLayer);
        doc.Layers.Add(dimLayer);

        Layer? specLayer = null;
        TextStyle? specStyle = null;
        if (specTable != null)
        {
            specLayer = new Layer(ElectricalSpecLayer) { Color = AciColor.Default };
            specStyle = new TextStyle(ElectricalSpecTextStyle, "malgun.ttf");
            doc.Layers.Add(specLayer);
            doc.TextStyles.Add(specStyle);
        }

        double cursorX = 0;
        double contentMaxY = 0;
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

            contentMaxY = Math.Max(contentMaxY, maxY + off.Y);
            cursorX += w + gap;
        }

        if (specTable != null)
            AddElectricalSpecTable(doc, specLayer!, specStyle!,
                new Vec2(0, -contentMaxY - 40), specTable);

        doc.Save(path);
    }

    private static void AddElectricalSpecTable(DxfDocument doc, Layer layer, TextStyle style,
        Vec2 origin, ElectricalSpecTable table)
    {
        const double textHeight = 3.2;
        const double rowHeight = 7.0;
        double[] widths = { 16, 22, 24, 22, 22, 24 };
        string[] headers = { "회로", "길이(m)", "저항(Ω)", "전압(V)", "전류(A)", "전력(W)" };
        double tableWidth = widths.Sum();
        double top = origin.Y;
        int rowCount = table.Rows.Count + 2; // header + circuits + total
        double bottom = top - rowHeight * rowCount;
        var inv = CultureInfo.InvariantCulture;

        AddSpecText(doc, layer, style, "전기 사양표", origin.X, top + 18, 5.0);
        AddSpecText(doc, layer, style, table.Wire.Describe() + " · " + table.Supply.Describe(),
            origin.X, top + 11, 3.2);

        AddSpecLine(doc, layer, origin.X, top, origin.X + tableWidth, top);
        AddSpecLine(doc, layer, origin.X, bottom, origin.X + tableWidth, bottom);
        AddSpecLine(doc, layer, origin.X, top, origin.X, bottom);
        AddSpecLine(doc, layer, origin.X + tableWidth, top, origin.X + tableWidth, bottom);
        for (int r = 1; r <= rowCount; r++)
            AddSpecLine(doc, layer, origin.X, top - rowHeight * r, origin.X + tableWidth, top - rowHeight * r);

        double x = origin.X;
        for (int c = 0; c < widths.Length - 1; c++)
        {
            x += widths[c];
            AddSpecLine(doc, layer, x, top, x, bottom);
        }

        x = origin.X;
        for (int c = 0; c < headers.Length; c++)
        {
            AddSpecText(doc, layer, style, headers[c], x + 1, top - rowHeight / 2, textHeight);
            x += widths[c];
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            ElectricalSpecRow row = table.Rows[r];
            double y = top - rowHeight * (r + 1) - rowHeight / 2;
            x = origin.X;
            AddSpecText(doc, layer, style, row.CircuitId, x + 1, y, textHeight); x += widths[0];
            AddSpecText(doc, layer, style, row.LengthM.ToString("F2", inv), x + 1, y, textHeight); x += widths[1];
            AddSpecText(doc, layer, style, row.ResistanceOhm.ToString("F2", inv), x + 1, y, textHeight); x += widths[2];
            AddSpecText(doc, layer, style, row.VoltageV.ToString("F1", inv), x + 1, y, textHeight); x += widths[3];
            AddSpecText(doc, layer, style, row.CurrentA.ToString("F2", inv), x + 1, y, textHeight); x += widths[4];
            AddSpecText(doc, layer, style, row.PowerW.ToString("F1", inv), x + 1, y, textHeight);
        }

        double totalY = top - rowHeight * (table.Rows.Count + 1) - rowHeight / 2;
        x = origin.X;
        AddSpecText(doc, layer, style, "합계", x + 1, totalY, textHeight);
        x += widths[0] + widths[1] + widths[2] + widths[3];
        AddSpecText(doc, layer, style, table.TotalCurrentA.ToString("F2", inv), x + 1, totalY, textHeight); x += widths[4];
        AddSpecText(doc, layer, style, table.TotalPowerW.ToString("F1", inv), x + 1, totalY, textHeight);

        AddSpecText(doc, layer, style,
            "L: 가열부 길이(인출부 제외). 외부 리드 저항은 반영하지 않음.", origin.X, bottom - 7, 2.8);
        AddSpecText(doc, layer, style,
            "R = rL, I = V/R, P = V²/(rL). AC RMS, 상온 일정 저항 기준.", origin.X, bottom - 12, 2.8);
    }

    private static void AddSpecText(DxfDocument doc, Layer layer, TextStyle style,
        string value, double x, double y, double height)
    {
        if (string.IsNullOrEmpty(value)) return;
        var text = new Text(value, new Vector2(x, y), height)
        {
            Layer = layer,
            Style = style,
            Alignment = TextAlignment.MiddleLeft
        };
        doc.Entities.Add(text);
    }

    private static void AddSpecLine(DxfDocument doc, Layer layer,
        double x1, double y1, double x2, double y2)
    {
        doc.Entities.Add(new Line(new Vector2(x1, y1), new Vector2(x2, y2)) { Layer = layer });
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
