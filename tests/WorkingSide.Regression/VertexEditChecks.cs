using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 실제 CommitVertex의 성공 반영과 실패 롤백을 확인한다. Undo/Redo 이력은 제거됐으므로
/// 성공한 편집은 보관되지 않고, 실패한 커밋만 커밋 전 상태로 복구되어야 한다.
/// 새 계약에서는 인출 경계가 원본 CAD 참조를 가진 실제 인출면이어야 편집을 확정할 수
/// 있으므로, 옆면이 있는 상자(exitSides.stp)에서 우측 원본 모서리를 인출면으로 사용한다.
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

    /// <summary>실제 우측 인출 체인(u=-10) 위에서 시작/종료하는 단일 사행 조각.
    /// 시작/종료 인출점의 원본 binding은 호출자가 boundary에서 직접 얻어 지정한다.</summary>
    static SerpentineResult Pattern3(ExitEdgeBinding? startBinding, ExitEdgeBinding? endBinding)
    {
        var path = new List<Vec2> { new(-10, -10), new(-60, 0), new(-10, 10) };
        var segments = new List<SerpentineSegment>();
        for (int i = 1; i < path.Count; i++) segments.Add(new SerpentineSegment(0, path[i - 1], path[i]));
        return new SerpentineResult
        {
            Path = path,
            Segments = segments,
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = path.Zip(path.Skip(1), (a, b) => a.DistanceTo(b)).Sum(),
            RoutePieces = new[] { new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, path) },
            StartExit = new CircuitExitPoint(CircuitExitRole.Start, path[0], 0, 0) { Binding = startBinding },
            EndExit = new CircuitExitPoint(CircuitExitRole.End, path[^1], 0, 1) { Binding = endBinding },
        };
    }

    static void Commit(MainWindow window, AreaHandle area, int group, Vec2 datum)
    {
        SetField(window, "_vertexGroups", RouteVertexGroups.Build(area.Pattern!.RoutePieces).ToList());
        SetField(window, "_vertexDragGroup", group);
        Invoke(window, "CommitVertex", datum);
        SetField(window, "_vertexDragGroup", -1);
    }

    public static void Run(MainWindow window, string fixtureDir)
    {
        Check(Bridge.LoadStep(Path.Combine(fixtureDir, "exitSides.stp")),
            "load solid exit-side fixture for vertex edit checks");
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1)),
            "confirm +Z working side on the solid exit-side fixture");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for vertex edit checks");
        if (side == null) return;

        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for vertex edit checks");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        Check(window.FindName("VertexUndoButton") == null && window.FindName("VertexRedoButton") == null,
            "vertex undo/redo controls are removed from the UI");
        Check(typeof(MainWindow).GetMethod("ApplyVertexHistory", flags) == null &&
              typeof(MainWindow).GetMethod("VertexUndoCount", flags) == null &&
              typeof(MainWindow).GetField("_vertexUndo", flags) == null &&
              typeof(MainWindow).GetField("_vertexRedo", flags) == null,
            "vertex undo/redo commands and history storage are removed");
        if (topFace < 0) return;

        // 실제 우측 원본 모서리를 인출면 후보로 고른다.
        var exitSides = Bridge.GetBoundaryExitSides(new[] { topFace }, 0.05, 0.0, 0.0, 1.0);
        double maxX = -1e9;
        foreach (var candidate in exitSides ?? Array.Empty<ExitSideCandidate>())
        {
            if (candidate == null || candidate.Points == null) continue;
            for (int k = 0; k + 2 < candidate.Points.Length; k += 3)
                maxX = Math.Max(maxX, candidate.Points[k]);
        }
        ExitSideCandidate rightSide = null;
        foreach (var candidate in exitSides ?? Array.Empty<ExitSideCandidate>())
        {
            if (candidate == null || candidate.FaceId < 0 || candidate.Points == null || candidate.Points.Length < 6) continue;
            bool allRight = true;
            for (int k = 0; k + 2 < candidate.Points.Length; k += 3)
                if (Math.Abs(candidate.Points[k] - maxX) > 1e-6) { allRight = false; break; }
            if (allRight) { rightSide = candidate; break; }
        }
        Check(rightSide != null && rightSide.EdgeReferences != null && rightSide.EdgeReferences.Length > 0,
            "native right exit side carries original edge references for vertex edit checks");
        if (rightSide == null) { Cleanup(window, fixtureDir); return; }

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = new AreaHandle(Activator.CreateInstance(areaType, nonPublic: true)!, areaType);
        areaType.GetProperty("Name")!.SetValue(area.Value, "VertexEditCheck");
        areaType.GetProperty("Frame")!.SetValue(area.Value, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(area.Value, 4301);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area.Value)!).Add(topFace);
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area.Value)!).Add(new()
            { new(-70, -20), new(-10, -20), new(-10, 20), new(-70, 20) });
        var regionLoops = Bridge.GetSelectedRegionBoundary(new[] { topFace }, 0.05);
        Check(regionLoops != null && regionLoops.Length == 1 &&
              regionLoops[0].EdgeReferences != null && regionLoops[0].EdgeReferences.Length > 0,
            "vertex edit fixture exposes exact region boundary references");
        if (regionLoops == null) { Cleanup(window, fixtureDir); return; }
        areaType.GetProperty("RegionBoundary")!.SetValue(area.Value, new ExactRegionContainment(
            new CadRegionBoundary2D(side.Id.Value, side.Revision.Value, regionLoops[0].EdgeReferences)));
        ((IList)Field<object>(window, "_areas")).Add(area.Value);

        areaType.GetProperty("ExitFaceId")!.SetValue(area.Value, rightSide.FaceId);
        areaType.GetProperty("ExitFaceRevision")!.SetValue(area.Value, Bridge.GetModelRevision());
        ((List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area.Value)!).AddRange(
            new[] { new Vec2(-10, -20), new Vec2(-10, 20) });
        ((List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(area.Value)!).AddRange(
            new[] { new Vec3(70, -20, 20), new Vec3(70, 20, 20) });
        ((List<int>)areaType.GetProperty("ExitChainFaceIds")!.GetValue(area.Value)!).Add(rightSide.FaceId);
        var references = new List<ExitEdgeReference>();
        foreach (var reference in rightSide.EdgeReferences)
            references.Add(CadExitBoundary2D.CopyReference(reference));
        areaType.GetProperty("ExitEdgeRefs")!.SetValue(area.Value, references);
        var boundary = new CadExitBoundary2D(side.Id.Value, side.Revision.Value, references);
        areaType.GetProperty("ExitBoundary")!.SetValue(area.Value, boundary);

        // 실제 원본 모서리에서 두 인출점의 binding을 직접 얻는다.
        ExitBoundaryResolution startRes = boundary.Project(new Vec2(-10, -10), 0.05);
        ExitBoundaryResolution endRes = boundary.Project(new Vec2(-10, 10), 0.05);
        Check(startRes.Succeeded && endRes.Succeeded &&
              startRes.Point!.Value.Binding.HasValue && endRes.Point!.Value.Binding.HasValue,
            "both synthetic exits bind to the original right edge");

        var marginBox = (TextBox)window.FindName("MarginBox");
        var spacingBox = (TextBox)window.FindName("SpacingBox");
        marginBox.Text = "5";
        spacingBox.Text = "50";
        areaType.GetProperty("MarginText")!.SetValue(area.Value, "5");
        areaType.GetProperty("SpacingText")!.SetValue(area.Value, "50");

        var p0 = Pattern3(startRes.Point!.Value.Binding, endRes.Point!.Value.Binding);
        area.Pattern = p0;
        area.NativePieceIds = new[] { 9001 };
        area.RoutePieceLengths = new[] { 20.0 };
        area.ProjectedPieces = new List<RoutePieceResult>();
        area.StartAnchor = new Vec2(-10, -10);
        area.EndAnchor = new Vec2(-10, 10);
        area.RouteEdited = false;
        areaType.GetProperty("AppliedInputs")!.SetValue(area.Value,
            Invoke(window, "CaptureAppliedInputs", area.Value, 5.0, 50.0));

        Invoke(window, "ActivateArea", area.Value);

        // 시작/종료 꼭지점은 편집 모드에서 고정된다 — 전용 선택 버튼으로만 변경한다.
        Commit(window, area, 0, new Vec2(-10, -5));
        Check(ReferenceEquals(area.Pattern, p0), "vertex edit keeps the route start point fixed");
        Commit(window, area, 2, new Vec2(-10, 5));
        Check(ReferenceEquals(area.Pattern, p0), "vertex edit keeps the route end point fixed");
        Check(!area.RouteEdited, "rejected endpoint edits do not mark the area as edited");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("전용"),
            "rejected endpoint edits point to the dedicated start/end buttons");

        // 실패 단계: 인출점의 원본 binding을 제거한 불일치 상태에서 편집을 확정하면
        // 경계 검증이 명시 실패하고, 방금 준비한 편집을 포함해 커밋 전 상태로 되돌린다.
        var unbound = Pattern3(null, null);
        area.Pattern = unbound;
        Commit(window, area, 1, new Vec2(-60, 2));
        Check(ReferenceEquals(area.Pattern, unbound), "failed native rebuild restores the previous pattern");
        Check(area.NativePieceIds.Length == 1 && area.NativePieceIds[0] == 9001,
            "failed native rebuild restores native piece ids");
        Check(area.RoutePieceLengths.Length == 1 && Math.Abs(area.RoutePieceLengths[0] - 20) < 1e-6 &&
              area.ProjectedPieces.Count == 0,
            "failed native rebuild restores lengths and projected geometry");
        Check(area.StartAnchor.HasValue && area.EndAnchor.HasValue &&
              area.StartAnchor.Value.DistanceTo(new Vec2(-10, -10)) < 1e-9 &&
              area.EndAnchor.Value.DistanceTo(new Vec2(-10, 10)) < 1e-9,
            "failed native rebuild restores both exit anchors");
        Check(!area.RouteEdited, "failed native rebuild preserves the previous edited flag");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("유지"),
            "failed native rebuild reports that the previous route was retained");

        // 성공 단계: 원본 binding을 복원한 같은 편집은 실제로 확정된다.
        area.Pattern = p0;
        Commit(window, area, 1, new Vec2(-60, 2));
        Check(!ReferenceEquals(area.Pattern, p0), "successful commit applies the edited pattern");
        Check(area.RouteEdited, "successful commit marks the area as manually edited");
        Check(area.ProjectedPieces.Count == 1 &&
              area.RoutePieceLengths.Length == 1 && area.RoutePieceLengths[0] > 0,
            "successful commit refreshes projected pieces and lengths");

        Cleanup(window, fixtureDir);
        Console.WriteLine($"{count} vertex edit checks passed.");
    }

    static void Cleanup(MainWindow window, string fixtureDir)
    {
        Bridge.ClearVertexHandles();
        Bridge.ClearRoutePieces();
        Invoke(window, "ClearAllAreas", false);
        // 후속 검사 그룹이 기대하는 원래 모델과 작업 방향으로 복원한다.
        Bridge.LoadStep(Path.Combine(fixtureDir, "layers.stp"));
        Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1));
    }
}
