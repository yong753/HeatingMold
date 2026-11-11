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
/// 레이어: MOLD_OUTLINE(면 경계) / HEAT_AREA(발열영역) / HEAT_WIRE(사행, 선폭) / DIMENSION(치수).
/// </summary>
public static class DxfExporter
{
    public const string MoldOutlineLayer = "MOLD_OUTLINE";
    public const string HeatWireLayer = "HEAT_WIRE";
    public const string HeatAreaLayer = "HEAT_AREA";
    public const string DimensionLayer = "DIMENSION";

    public const string HiddenLayer = "HIDDEN_LINE";
    public const string ElectricalSpecLayer = "ELECTRICAL_SPEC";

    private const string ElectricalSpecTextStyle = "ELECTRICAL_SPEC_TEXT";
    private const string DimensionStyleName = "HEATINGMOLD_DIM";

    /// <summary>치수 문자 높이(mm).</summary>
    private const double DimensionTextHeight = 20.0;

    /// <summary>
    /// 표 배율 기준이 되는 모델 최대 치수(mm). 200mm당 1배.
    /// 도면 대비 비율이 아니라 출력했을 때 읽을 수 있는 크기를 기준으로 한다.
    /// </summary>
    private const double SpecTableReferenceExtent = 200.0;

    /// <summary>표가 도면을 과도하게 차지하지 않도록 하는 최대 배율.</summary>
    private const double SpecTableMaxScale = 16.0;

    /// <summary>
    /// Place multiple orthographic views into one DXF, laid out left-to-right with a gap.
    /// When a spec table is supplied the document is written as R2010 so Korean text is stored as UTF-8.
    /// </summary>
    public static bool ExportViews(string path, IReadOnlyList<DxfView> views,
        double gap = 60, double dimOffset = 20, ElectricalSpecTable? specTable = null)
    {
        var doc = CreateDocument(specTable, out Layer moldLayer, out Layer hiddenLayer,
            out Layer heatLayer, out Layer heatAreaLayer, out Layer dimLayer, out Layer? specLayer,
            out TextStyle? specStyle, out DimensionStyle dimStyle);

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

            AddPolylines(doc, view.HeatAreas, heatAreaLayer, off);
            AddPolylines(doc, view.VisibleEdges, moldLayer, off);
            AddPolylines(doc, view.HiddenEdges, hiddenLayer, off);
            AddPolylines(doc, view.HeatBands, heatLayer, off);
            AddPolylines(doc, view.HeatLines, heatLayer, off);

            double w = maxX - minX, h = maxY - minY;
            var wDim = new LinearDimension(
                new Vector2(minX + off.X, minY + off.Y),
                new Vector2(maxX + off.X, minY + off.Y), -dimOffset, 0, dimStyle) { Layer = dimLayer };
            // 회전 90°에서 기준선이 아래→위이므로 양수 offset이 금형 왼쪽을 가리킨다.
            var hDim = new LinearDimension(
                new Vector2(minX + off.X, minY + off.Y),
                new Vector2(minX + off.X, maxY + off.Y), dimOffset, 90, dimStyle) { Layer = dimLayer };
            doc.Entities.Add(wDim);
            doc.Entities.Add(hDim);

            contentMaxY = Math.Max(contentMaxY, maxY + off.Y);
            cursorX += w + gap;
        }

        if (specTable != null)
            AddElectricalSpecTable(doc, specLayer!, specStyle!,
                new Vec2(0, -contentMaxY - 40), specTable);

        // netDxf Save returns false on a failed write and may throw for I/O errors.
        // Callers must not treat a temp file's existence alone as success.
        return doc.Save(path);
    }

    /// <summary>
    /// Write exactly one plan view in its own coordinate system. Coordinates are not
    /// re-normalized: the confirmed working-plane origin and axes must survive into the
    /// DXF. The spec table is placed below both the drawing and its dimension lines.
    /// </summary>
    public static bool ExportPlan(string path, DxfView view,
        ElectricalSpecTable? specTable = null, double dimOffset = 20)
    {
        if (view == null) return false;
        var all = view.AllPolylines().SelectMany(p => p.Points).ToList();
        if (all.Count == 0) return false;

        var doc = CreateDocument(specTable, out Layer moldLayer, out Layer hiddenLayer,
            out Layer heatLayer, out Layer heatAreaLayer, out Layer dimLayer, out Layer? specLayer,
            out TextStyle? specStyle, out DimensionStyle dimStyle);

        double minX = all.Min(v => v.X);
        double minY = all.Min(v => v.Y);
        double maxX = all.Max(v => v.X);
        double maxY = all.Max(v => v.Y);

        var off = new Vec2(0, 0);
        AddPolylines(doc, view.HeatAreas, heatAreaLayer, off);
        AddPolylines(doc, view.VisibleEdges, moldLayer, off);
        AddPolylines(doc, view.HiddenEdges, hiddenLayer, off);
        AddPolylines(doc, view.HeatBands, heatLayer, off);
        AddPolylines(doc, view.HeatLines, heatLayer, off);

        // 치수는 금형 외곽 투상선만 측정한다. 발열경로가 외곽을 벗어나도 금형 치수를
        // 오염시키지 않으며, 경로 길이는 전기 사양표의 커밋된 3D 길이를 쓴다.
        var outline = view.VisibleEdges.SelectMany(p => p.Points).ToList();
        if (outline.Count > 0)
        {
            double moldMinX = outline.Min(v => v.X);
            double moldMinY = outline.Min(v => v.Y);
            double moldMaxX = outline.Max(v => v.X);
            double moldMaxY = outline.Max(v => v.Y);
            doc.Entities.Add(new LinearDimension(
                new Vector2(moldMinX, moldMinY), new Vector2(moldMaxX, moldMinY), -dimOffset, 0, dimStyle) { Layer = dimLayer });
            // 회전 90°에서 기준선이 아래→위이므로 양수 offset이 금형 왼쪽을 가리킨다.
            doc.Entities.Add(new LinearDimension(
                new Vector2(moldMinX, moldMinY), new Vector2(moldMinX, moldMaxY), dimOffset, 90, dimStyle) { Layer = dimLayer });
        }

        // 표는 모델 좌표와 같은 mm 단위이므로, 큰 모델에서 고정 크기로 두면
        // 상대적으로 사라진다. 금형 외곽선 크기에 비례해 확대하되 작은 모델은
        // 기존 크기를 유지한다.
        double tableScale = 1.0;
        if (outline.Count > 0)
        {
            double extent = Math.Max(
                outline.Max(v => v.X) - outline.Min(v => v.X),
                outline.Max(v => v.Y) - outline.Min(v => v.Y));
            if (double.IsFinite(extent) && extent > 0)
                tableScale = Math.Clamp(extent / SpecTableReferenceExtent, 1.0, SpecTableMaxScale);
        }
        if (specTable != null)
            AddElectricalSpecTable(doc, specLayer!, specStyle!,
                new Vec2(minX, minY - dimOffset - 40 * tableScale), specTable, tableScale);

        return doc.Save(path);
    }

    private static DxfDocument CreateDocument(ElectricalSpecTable? specTable,
        out Layer moldLayer, out Layer hiddenLayer, out Layer heatLayer, out Layer heatAreaLayer,
        out Layer dimLayer, out Layer? specLayer, out TextStyle? specStyle, out DimensionStyle dimStyle)
    {
        var doc = specTable == null ? new DxfDocument() : new DxfDocument(DxfVersion.AutoCad2010);
        moldLayer = new Layer(MoldOutlineLayer) { Color = AciColor.Cyan };
        hiddenLayer = new Layer(HiddenLayer) { Color = new AciColor(8) };
        heatLayer = new Layer(HeatWireLayer) { Color = AciColor.Red };
        heatAreaLayer = new Layer(HeatAreaLayer) { Color = AciColor.Default };
        dimLayer = new Layer(DimensionLayer) { Color = AciColor.Yellow };
        // netDxf 기본 치수 스타일의 문자 높이는 0.18이라 사실상 읽을 수 없다.
        // 제품 평면도 기준으로 20mm 높이의 전용 스타일을 사용한다.
        dimStyle = new DimensionStyle(DimensionStyleName) { TextHeight = DimensionTextHeight };
        doc.DimensionStyles.Add(dimStyle);
        doc.Layers.Add(moldLayer);
        doc.Layers.Add(hiddenLayer);
        doc.Layers.Add(heatLayer);
        doc.Layers.Add(heatAreaLayer);
        doc.Layers.Add(dimLayer);

        specLayer = null;
        specStyle = null;
        if (specTable != null)
        {
            specLayer = new Layer(ElectricalSpecLayer) { Color = AciColor.Default };
            specStyle = new TextStyle(ElectricalSpecTextStyle, "malgun.ttf");
            doc.Layers.Add(specLayer);
            doc.TextStyles.Add(specStyle);
        }
        return doc;
    }

    private static void AddElectricalSpecTable(DxfDocument doc, Layer layer, TextStyle style,
        Vec2 origin, ElectricalSpecTable table, double scale = 1.0)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
        double textHeight = 3.2 * scale;
        double rowHeight = 7.0 * scale;
        double[] widths = { 16 * scale, 22 * scale, 24 * scale, 22 * scale, 22 * scale, 24 * scale };
        string[] headers = { "회로", "길이(m)", "저항(Ω)", "전압(V)", "전류(A)", "전력(W)" };
        double tableWidth = widths.Sum();
        double top = origin.Y;
        int rowCount = table.Rows.Count + 2; // header + circuits + total
        double bottom = top - rowHeight * rowCount;
        var inv = CultureInfo.InvariantCulture;

        AddSpecText(doc, layer, style, "전기 사양표", origin.X, top + 18 * scale, 5.0 * scale);
        AddSpecText(doc, layer, style, table.Wire.Describe() + " · " + ElectricalSupplyCaption(table),
            origin.X, top + 11 * scale, 3.2 * scale);

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
            AddSpecText(doc, layer, style, headers[c], x + scale, top - rowHeight / 2, textHeight);
            x += widths[c];
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            ElectricalSpecRow row = table.Rows[r];
            double y = top - rowHeight * (r + 1) - rowHeight / 2;
            x = origin.X;
            AddSpecText(doc, layer, style, row.CircuitId, x + scale, y, textHeight); x += widths[0];
            AddSpecText(doc, layer, style, row.LengthM.ToString("F2", inv), x + scale, y, textHeight); x += widths[1];
            AddSpecText(doc, layer, style, row.ResistanceOhm.ToString("F2", inv), x + scale, y, textHeight); x += widths[2];
            AddSpecText(doc, layer, style, row.VoltageV.ToString("F1", inv), x + scale, y, textHeight); x += widths[3];
            AddSpecText(doc, layer, style, row.CurrentA.ToString("F2", inv), x + scale, y, textHeight); x += widths[4];
            AddSpecText(doc, layer, style, row.PowerW.ToString("F1", inv), x + scale, y, textHeight);
        }

        double totalY = top - rowHeight * (table.Rows.Count + 1) - rowHeight / 2;
        x = origin.X;
        AddSpecText(doc, layer, style, "합계", x + scale, totalY, textHeight);
        x += widths[0] + widths[1] + widths[2] + widths[3];
        AddSpecText(doc, layer, style, table.TotalCurrentA.ToString("F2", inv), x + scale, totalY, textHeight); x += widths[4];
        AddSpecText(doc, layer, style, table.TotalPowerW.ToString("F1", inv), x + scale, totalY, textHeight);

        AddSpecText(doc, layer, style,
            "L: 인출점 사이 실제 도체 전체 길이(외부 리드 제외). 외부 리드 저항은 반영하지 않음.",
            origin.X, bottom - 7 * scale, 2.8 * scale);
        AddSpecText(doc, layer, style,
            table.IsTargetPowerMode
                ? "P_i = P·L_i/ΣL, R_i = rL_i, V_i = √(P_i·R_i), I_i = √(P_i/R_i). AC RMS, 상온 일정 저항 기준."
                : "R = rL, I = V/R, P = V²/(rL). AC RMS, 상온 일정 저항 기준.",
            origin.X, bottom - 12 * scale, 2.8 * scale);
    }

    /// <summary>
    /// 사양표 상단 공급 조건 문구. 목표전력 방식에는 공통 인가전압이 없으므로
    /// 계산된 필요 전압을 공급 전압처럼 표기하지 않는다.
    /// </summary>
    private static string ElectricalSupplyCaption(ElectricalSpecTable table)
    {
        if (table.IsTargetPowerMode)
            return "목표 발열전력 길이비 배분 · 회로별 필요 전압 계산 · 허용 전압/전류/전력 판정";
        return table.Supply?.Describe() ?? "";
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
            if (pl.Color != null) poly.Color = pl.Color;
            doc.Entities.Add(poly);
        }
    }
}

/// <summary>DXF 2D polyline (points + closed flag).</summary>
public sealed class DxfPolyline
{
    public IReadOnlyList<Vec2> Points { get; init; } = Array.Empty<Vec2>();
    public bool Closed { get; init; }

    /// <summary>엔티티 색상. null이면 ByLayer 색상을 사용한다.</summary>
    public AciColor? Color { get; set; }
}

/// <summary>One DXF view: visible / hidden / heat-area / heat polylines.</summary>
public sealed class DxfView
{
    public List<DxfPolyline> VisibleEdges { get; } = new();
    public List<DxfPolyline> HiddenEdges { get; } = new();
    public List<DxfPolyline> HeatAreas { get; } = new();
    public List<DxfPolyline> HeatLines { get; } = new();
    public List<DxfPolyline> HeatBands { get; } = new();

    public IEnumerable<DxfPolyline> AllPolylines()
    {
        foreach (var p in VisibleEdges) yield return p;
        foreach (var p in HiddenEdges) yield return p;
        foreach (var p in HeatAreas) yield return p;
        foreach (var p in HeatBands) yield return p;
        foreach (var p in HeatLines) yield return p;
    }
}
