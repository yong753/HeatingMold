using System.Collections;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 제외영역 경로 재생성의 실패 원자성을 검증한다.
/// 1) AreaRouteSnapshot이 제외 목록만 복사하고 나머지 상태는 참조로 보존하는지.
/// 2) 연속성 검사 실패 시 이미 스테이징한 모든 네이티브 임시 ID가 폐기되는지.
/// </summary>
static class GeometryRollbackChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(target, args);
    static object InvokeStatic(string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

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

    static SerpentineRoutePiece Piece(Vec2 a, Vec2 b) =>
        new(SerpentineRoutePieceKind.HeatingRun, 0, new List<Vec2> { a, b });

    static object NewArea(Type type, WorkingSideDefinition side, int topFace, string name, int id)
    {
        var area = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("Name")!.SetValue(area, name);
        type.GetProperty("Frame")!.SetValue(area, side.Frame);
        type.GetProperty("AreaId")!.SetValue(area, id);
        ((HashSet<int>)type.GetProperty("FaceIdSet")!.GetValue(area)!).Add(topFace);
        return area;
    }

    static void SnapshotOwnershipChecks(Type type, WorkingSideDefinition side)
    {
        var area = NewArea(type, side, 0, "SnapshotCheck", 4401);
        var exclusions = (IList)type.GetProperty("Exclusions")!.GetValue(area)!;
        exclusions.Add((0d, -2d, 4d, 2d));
        var pattern = Pattern(new Vec2(-8, 0), new Vec2(8, 0));
        var paths = new List<List<Vec2>> { new() { new(-8, 0), new(8, 0) } };
        var projected = new List<RoutePieceResult>();
        var lengths = new[] { 16.0 };
        var leads = new[] { 4.0 };
        var turnEnabled = new List<bool> { true };
        type.GetProperty("Pattern")!.SetValue(area, pattern);
        type.GetProperty("Paths")!.SetValue(area, paths);
        type.GetProperty("ProjectedPieces")!.SetValue(area, projected);
        type.GetProperty("NativePieceIds")!.SetValue(area, new[] { 9201 });
        type.GetProperty("RoutePieceLengths")!.SetValue(area, lengths);
        type.GetProperty("LeadLengths")!.SetValue(area, leads);
        type.GetProperty("TurnEnabled")!.SetValue(area, turnEnabled);
        type.GetProperty("PatternDisplayed")!.SetValue(area, true);
        type.GetProperty("StartAnchor")!.SetValue(area, new Vec2(-8, 0));
        type.GetProperty("EndAnchor")!.SetValue(area, new Vec2(8, 0));
        type.GetProperty("LastExitFailure")!.SetValue(area, "old failure");
        type.GetProperty("ExcludedPlanKey")!.SetValue(area, "key-1");

        object snapshot = InvokeStatic("CaptureAreaRouteState", area);
        var snapshotExclusions = (IList)snapshot.GetType().GetProperty("Exclusions")!.GetValue(snapshot)!;
        Check(snapshotExclusions.Count == 1 && !ReferenceEquals(snapshotExclusions, exclusions),
            "snapshot copies the exclusion list instead of aliasing it");

        // 캡처 후 원본을 바꿔도 스냅샷의 제외 목록은 변하지 않아야 한다.
        exclusions.Add((5d, 5d, 6d, 6d));
        type.GetProperty("Pattern")!.SetValue(area, null);
        type.GetProperty("Paths")!.SetValue(area, new List<List<Vec2>>());
        type.GetProperty("RoutePieceLengths")!.SetValue(area, Array.Empty<double>());
        type.GetProperty("ExcludedPlanKey")!.SetValue(area, "mutated");
        Check(snapshotExclusions.Count == 1, "snapshot exclusion copy is independent of later mutations");

        InvokeStatic("RestoreAreaRouteState", area, snapshot, false);
        Check(exclusions.Count == 2, "restore without exclusions leaves the current exclusion list untouched");
        Check(ReferenceEquals(type.GetProperty("Pattern")!.GetValue(area), pattern) &&
              ReferenceEquals(type.GetProperty("Paths")!.GetValue(area), paths) &&
              ReferenceEquals(type.GetProperty("RoutePieceLengths")!.GetValue(area), lengths),
            "restore reconnects pattern, paths and lengths by reference");
        Check((string)type.GetProperty("ExcludedPlanKey")!.GetValue(area)! == "key-1",
            "restore reconnects the excluded plan key");

        InvokeStatic("RestoreAreaRouteState", area, snapshot, true);
        Check(ReferenceEquals(type.GetProperty("Exclusions")!.GetValue(area), exclusions) && exclusions.Count == 1,
            "restore with exclusions preserves list identity and restores its contents");
    }

    static void StageFailureChecks(MainWindow window, Type areaType, WorkingSideDefinition side, int topFace)
    {
        Invoke(window, "ClearAllAreas", false);
        Bridge.ClearRoutePieces();
        var area = NewArea(areaType, side, topFace, "ExclusionStageFailure", 4402);
        // 같은 컴포넌트의 두 조각을 일부러 떨어뜨려 두 번째 스테이징 후 연속성 검사를 실패시킨다.
        var component = new List<SerpentineRoutePiece>
        {
            Piece(new Vec2(-10, -5), new Vec2(-5, -5)),
            Piece(new Vec2(5, 5), new Vec2(10, 5))
        };
        areaType.GetProperty("ExclusionPieces")!.SetValue(area,
            new List<List<SerpentineRoutePiece>> { component });
        ((IList)Field<object>(window, "_areas")).Add(area);
        Invoke(window, "ActivateArea", area);

        int priorId = 950;
        var prior = Bridge.DisplayWorkingRoutePiece(side.Id.Value, side.Revision.Value, priorId,
            "TurnConnector", new[] { -10.0, 0, 20, -5.0, 0, 20 });
        Check(prior != null, "a committed route exists before the failing rebuild");

        int stageBefore = (int)Field<object>(window, "_nextStageId");
        var rebuild = typeof(MainWindow).GetMethod("RebuildNativePatternGeometry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] args = { 0d, 0 };
        bool rebuilt = (bool)rebuild.Invoke(window, args)!;
        Check(!rebuilt, "discontinuous exclusion pieces fail the rebuild");
        Check((int)Field<object>(window, "_nextStageId") == stageBefore - 2,
            "both exclusion pieces consumed a native staging id before the failure");
        Check(((IList)areaType.GetProperty("ProjectedPieces")!.GetValue(area)!).Count == 0,
            "failed rebuild does not publish projected pieces");
        Check(Bridge.SetConnectorEnabled(priorId, true, .2, .8, .3, false),
            "failed rebuild leaves the previously committed route intact");

        // 실패 전에 사용한 두 임시 ID를 다시 스테이징할 수 있어야 한다. 하나라도
        // 소유권/핸들이 남아 있으면 이 호출이 실패한다.
        var reusedFirst = Bridge.DisplayWorkingRoutePiece(side.Id.Value, side.Revision.Value, stageBefore,
            "TurnConnector", new[] { -10.0, 0, 20, -5.0, 0, 20 });
        var reusedSecond = Bridge.DisplayWorkingRoutePiece(side.Id.Value, side.Revision.Value, stageBefore - 1,
            "TurnConnector", new[] { 5.0, 0, 20, 10.0, 0, 20 });
        Check(reusedFirst != null && reusedSecond != null,
            "failed exclusion staging releases every temporary id for reuse");

        Bridge.DiscardWorkingRouteGeometry(side.Id.Value, side.Revision.Value,
            new[] { stageBefore, stageBefore - 1 }, Array.Empty<int>());
        Bridge.ClearRoutePieces();
        Invoke(window, "ClearAllAreas", false);
    }

    public static void Run(MainWindow window)
    {
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for geometry rollback checks");
        if (side == null) return;
        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for geometry rollback checks");
        if (topFace < 0) return;

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        SnapshotOwnershipChecks(areaType, side);
        StageFailureChecks(window, areaType, side, topFace);
        Console.WriteLine($"{count} geometry rollback checks passed.");
    }
}
