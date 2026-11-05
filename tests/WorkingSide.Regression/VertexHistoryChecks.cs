using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 실제 CommitVertex로 만든 이력이 Undo/Redo에서 패턴·네이티브 ID·길이·앵커를
/// 복원하는지 확인한다. 과거 구현은 스냅샷을 꺼내기만 하고 현재 패턴을 다시 그려
/// Undo가 무동작이었다. 스냅샷을 스택에 직접 넣지 않고 편집 경로를 통해 검증한다.
/// </summary>
static class VertexHistoryChecks
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

    static int UndoCount(MainWindow window, AreaHandle area) =>
        (int)Invoke(window, "VertexUndoCount", area.Value)!;
    static int RedoCount(MainWindow window, AreaHandle area) =>
        (int)Invoke(window, "VertexRedoCount", area.Value)!;

    public static void Run(MainWindow window)
    {
        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for vertex history checks");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for vertex history checks");
        if (topFace < 0 || side == null) return;

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        AreaHandle area = Area(window, areaType, side, topFace, "VertexHistoryCheck", 4301);
        Invoke(window, "ActivateArea", area.Value);

        // P0: CommitVertex가 만든 첫 이력의 기준 상태.
        var p0 = Pattern(new Vec2(-10, 0), new Vec2(0, 0), new Vec2(10, 0));
        double[] p0Lengths = { 20.0 };
        var p0Projected = new List<RoutePieceResult>();
        area.Pattern = p0;
        area.NativePieceIds = new[] { 9001 };
        area.RoutePieceLengths = p0Lengths;
        area.ProjectedPieces = p0Projected;
        area.StartAnchor = new Vec2(-10, 0);
        area.EndAnchor = new Vec2(10, 0);
        area.RouteEdited = false;

        // 실제 편집 1: 가운데 꼭지점을 위로 이동한다.
        Commit(window, area, 1, new Vec2(0, 2));
        var p1 = area.Pattern;
        Check(!ReferenceEquals(p1, p0), "CommitVertex applies and records the first edit");
        Check(area.RouteEdited, "CommitVertex marks the area as edited");
        Check(area.ProjectedPieces.Count == 1 && area.RoutePieceLengths.Length == 1 && area.RoutePieceLengths[0] > 0,
            "CommitVertex refreshes projected pieces and lengths");
        Check(UndoCount(window, area) == 1, "first commit pushes exactly one undo snapshot");

        Invoke(window, "ApplyVertexHistory", true);
        Check(ReferenceEquals(area.Pattern, p0), "undo restores the pattern captured before the real edit");
        Check(area.NativePieceIds[0] == 9001, "undo restores the captured native piece ids");
        Check(area.RoutePieceLengths.Length == 1 && Math.Abs(area.RoutePieceLengths.Sum() - 20) < 1e-6 &&
              area.ProjectedPieces.Count == 1,
            "undo rebuilds the captured length and projected geometry");
        Check(area.StartAnchor.HasValue && area.EndAnchor.HasValue &&
              area.StartAnchor.Value.DistanceTo(new Vec2(-10, 0)) < 1e-9 &&
              area.EndAnchor.Value.DistanceTo(new Vec2(10, 0)) < 1e-9,
            "undo restores both exit anchors");
        Check(!area.RouteEdited, "undo restores the edited flag");

        Invoke(window, "ApplyVertexHistory", false);
        Check(ReferenceEquals(area.Pattern, p1), "redo restores the first edited pattern");
        Check(area.RouteEdited && area.RoutePieceLengths.Length == 1 && area.RoutePieceLengths[0] > 0,
            "redo restores the edited flag and lengths");

        // 실제 편집 2: 같은 꼭지점을 다시 이동하고 두 단계를 연속 Undo/Redo한다.
        Commit(window, area, 1, new Vec2(0, 4));
        var p2 = area.Pattern;
        Check(UndoCount(window, area) == 2, "second commit keeps both undo snapshots");
        Invoke(window, "ApplyVertexHistory", true);
        Invoke(window, "ApplyVertexHistory", true);
        Check(ReferenceEquals(area.Pattern, p0), "two consecutive undos return to the original pattern");
        Check(RedoCount(window, area) == 2, "two consecutive undos populate both redo snapshots");
        Invoke(window, "ApplyVertexHistory", false);
        Invoke(window, "ApplyVertexHistory", false);
        Check(ReferenceEquals(area.Pattern, p2), "two consecutive redos return to the newest pattern");

        // Undo 후 새 편집은 Redo 이력을 지워야 한다.
        Invoke(window, "ApplyVertexHistory", true);
        Commit(window, area, 1, new Vec2(0, 6));
        var p3 = area.Pattern;
        Check(RedoCount(window, area) == 0, "a new edit after undo clears the redo stack");

        // 다른 영역의 실패가 Undo의 원자성을 깨지 않아야 한다. 재생성 실패 시
        // 현재 패턴과 양쪽 이력이 그대로 남아야 한다.
        AreaHandle bad = Area(window, areaType, side, topFace, "VertexHistoryBad", 4302);
        bad.Pattern = Pattern(new Vec2(1000, 1000), new Vec2(1100, 1000));
        int undoBeforeFailure = UndoCount(window, area);
        int redoBeforeFailure = RedoCount(window, area);
        Invoke(window, "ApplyVertexHistory", true);
        Check(ReferenceEquals(area.Pattern, p3), "failed native rebuild leaves the current pattern untouched");
        Check(UndoCount(window, area) == undoBeforeFailure && RedoCount(window, area) == redoBeforeFailure,
            "failed native rebuild preserves both history stacks");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("실패"),
            "failed native rebuild reports the failure to the user");

        ((IList)Field<object>(window, "_areas")).Remove(bad.Value);
        Invoke(window, "ApplyVertexHistory", true);
        Check(ReferenceEquals(area.Pattern, p1), "undo succeeds again after removing the failing area");

        Bridge.ClearVertexHandles();
        Bridge.ClearRoutePieces();
        Invoke(window, "ClearAllAreas", false);
        Console.WriteLine($"{count} vertex history checks passed.");
    }
}
