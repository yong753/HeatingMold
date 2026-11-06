using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 실제 CommitVertex의 성공 반영과 실패 롤백을 확인한다. Undo/Redo 이력은 제거됐으므로
/// 성공한 편집은 보관되지 않고, 실패한 커밋만 커밋 전 상태로 복구되어야 한다.
/// </summary>
static class VertexEditChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(target, args);

    /// <summary>테스트 어셈블리에서 internal WorkingArea를 직접 참조하지 않기 위한 reflection 래퍼.</summary>
    sealed class AreaHandle
    {
        readonly Type type;
        public object Value { get; }
        public AreaHandle(object value, Type type) { Value = value; this.type = type; }
        T Get<T>(string name) => (T)type.GetProperty(name)!.GetValue(Value)!;
        void Set(string name, object value) => type.GetProperty(name)!.SetValue(Value, value);
        public SerpentineResult Pattern { get => Get<SerpentineResult>("Pattern"); set => Set("Pattern", value); }
        public int[] NativePieceIds { get => Get<int[]>("NativePieceIds"); set => Set("NativePieceIds", value); }
        public double[] RoutePieceLengths { get => Get<double[]>("RoutePieceLengths"); set => Set("RoutePieceLengths", value); }
        public List<RoutePieceResult> ProjectedPieces { get => Get<List<RoutePieceResult>>("ProjectedPieces"); set => Set("ProjectedPieces", value); }
        public Vec2? StartAnchor { get => Get<Vec2?>("StartAnchor"); set => Set("StartAnchor", value); }
        public Vec2? EndAnchor { get => Get<Vec2?>("EndAnchor"); set => Set("EndAnchor", value); }
        public bool RouteEdited { get => Get<bool>("RouteEdited"); set => Set("RouteEdited", value); }
    }

    static SerpentineResult Pattern(params Vec2[] points)
    {
        var path = points.ToList();
        var segments = new List<SerpentineSegment>();
        for (int i = 1; i < path.Count; i++) segments.Add(new SerpentineSegment(0, path[i - 1], path[i]));
        return new SerpentineResult
        {
            Path = path,
            Segments = segments,
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = path.Zip(path.Skip(1), (a, b) => a.DistanceTo(b)).Sum(),
            RoutePieces = new[] { new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, path) }
        };
    }

    static AreaHandle Area(object window, Type areaType, WorkingSideDefinition side, int topFace, string name, int id)
    {
        var value = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(value, name);
        areaType.GetProperty("Frame")!.SetValue(value, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(value, id);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(value)!).Add(topFace);
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(value)!).Add(new()
            { new(-12, -8), new(12, -8), new(12, 8), new(-12, 8) });
        ((IList)Field<object>(window, "_areas")).Add(value);
        return new AreaHandle(value, areaType);
    }

    static void Commit(MainWindow window, AreaHandle area, int group, Vec2 datum)
    {
        SetField(window, "_vertexGroups", RouteVertexGroups.Build(area.Pattern!.RoutePieces).ToList());
        SetField(window, "_vertexDragGroup", group);
        Invoke(window, "CommitVertex", datum);
        SetField(window, "_vertexDragGroup", -1);
    }

    public static void Run(MainWindow window)
    {
        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for vertex edit checks");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for vertex edit checks");
        if (topFace < 0 || side == null) return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        Check(window.FindName("VertexUndoButton") == null && window.FindName("VertexRedoButton") == null,
            "vertex undo/redo controls are removed from the UI");
        Check(typeof(MainWindow).GetMethod("ApplyVertexHistory", flags) == null &&
              typeof(MainWindow).GetMethod("VertexUndoCount", flags) == null &&
              typeof(MainWindow).GetField("_vertexUndo", flags) == null &&
              typeof(MainWindow).GetField("_vertexRedo", flags) == null,
            "vertex undo/redo commands and history storage are removed");

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        AreaHandle area = Area(window, areaType, side, topFace, "VertexEditCheck", 4301);
        Invoke(window, "ActivateArea", area.Value);

        var p0 = Pattern(new Vec2(-10, 0), new Vec2(0, 0), new Vec2(10, 0));
        area.Pattern = p0;
        area.NativePieceIds = new[] { 9001 };
        area.RoutePieceLengths = new[] { 20.0 };
        area.ProjectedPieces = new List<RoutePieceResult>();
        area.StartAnchor = new Vec2(-10, 0);
        area.EndAnchor = new Vec2(10, 0);
        area.RouteEdited = false;
        areaType.GetProperty("AppliedInputs")!.SetValue(area.Value,
            Invoke(window, "CaptureAppliedInputs", area.Value, 5.0, 50.0));

        // 다른 영역의 매핑 실패로 전체 재스테이징이 실패하면 방금 커밋한 편집을 되돌린다.
        AreaHandle bad = Area(window, areaType, side, topFace, "VertexEditBad", 4302);
        bad.Pattern = Pattern(new Vec2(1000, 1000), new Vec2(1100, 1000));

        Commit(window, area, 1, new Vec2(0, 2));
        Check(ReferenceEquals(area.Pattern, p0), "failed native rebuild restores the previous pattern");
        Check(area.NativePieceIds.Length == 1 && area.NativePieceIds[0] == 9001,
            "failed native rebuild restores native piece ids");
        Check(area.RoutePieceLengths.Length == 1 && Math.Abs(area.RoutePieceLengths[0] - 20) < 1e-6 &&
              area.ProjectedPieces.Count == 0,
            "failed native rebuild restores lengths and projected geometry");
        Check(area.StartAnchor.HasValue && area.EndAnchor.HasValue &&
              area.StartAnchor.Value.DistanceTo(new Vec2(-10, 0)) < 1e-9 &&
              area.EndAnchor.Value.DistanceTo(new Vec2(10, 0)) < 1e-9,
            "failed native rebuild restores both exit anchors");
        Check(!area.RouteEdited, "failed native rebuild preserves the previous edited flag");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("유지"),
            "failed native rebuild reports that the previous route was retained");

        ((IList)Field<object>(window, "_areas")).Remove(bad.Value);
        Invoke(window, "ActivateArea", area.Value);

        Commit(window, area, 1, new Vec2(0, 2));
        Check(!ReferenceEquals(area.Pattern, p0), "successful commit applies the edited pattern");
        Check(area.RouteEdited, "successful commit marks the area as manually edited");
        Check(area.ProjectedPieces.Count == 1 &&
              area.RoutePieceLengths.Length == 1 && area.RoutePieceLengths[0] > 0,
            "successful commit refreshes projected pieces and lengths");

        Bridge.ClearVertexHandles();
        Bridge.ClearRoutePieces();
        Invoke(window, "ClearAllAreas", false);
        Console.WriteLine($"{count} vertex edit checks passed.");
    }
}
