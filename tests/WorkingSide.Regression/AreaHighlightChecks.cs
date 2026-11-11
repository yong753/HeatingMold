using System.Collections;
using System.Reflection;
using System.Windows;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 제외영역 차감 발열면 채움 표시(AreaHighlight) 회귀 검사.
/// 확정 면의 표시 삼각분할을 작업 프레임 2D에서 잘라낸 채움 메시가 표시되고,
/// 제외를 모두 지우면 원래의 Face 색칠로 돌아가는지 확인한다.
/// </summary>
static class AreaHighlightChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    public static void Run(MainWindow window)
    {
        // layers.stp: z=20 상면(40x20)을 클리핑 대상으로 쓴다.
        int topFace = -1, anyFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (anyFace < 0) anyFace = id;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        if (topFace < 0) topFace = anyFace;
        Check(topFace >= 0, "model exposes a face for area highlight clipping");

        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed working side is available for area highlight staging");
        if (side == null || topFace < 0) return;
        long revision = side.Revision.Value, workingId = side.Id.Value;
        double[] frame = { 0, 0, 100, 1, 0, 0, 0, 1, 0 };
        double[] color = { 0.9, 0.16, 0.16 };
        int[] faces = { topFace };
        var covered = new[] { -1000.0, -1000.0, 1000.0, 1000.0 };
        var hole = new[] { -5.0, -5.0, 5.0, 5.0 };
        var outside = new[] { 1000.0, 1000.0, 2000.0, 2000.0 };

        Check(Bridge.GetAreaHighlightTriangleCount(-1) == -1 &&
              Bridge.GetAreaHighlightTriangleCount(4001) == -1,
            "unknown area ids report no area highlight");
        Check(!Bridge.StageAreaHighlightMesh(0, revision, workingId, faces, hole, frame, color) &&
              !Bridge.StageAreaHighlightMesh(4001, revision + 1, workingId, faces, hole, frame, color) &&
              !Bridge.StageAreaHighlightMesh(4001, revision, workingId, Array.Empty<int>(), hole, frame, color) &&
              !Bridge.StageAreaHighlightMesh(4001, revision, workingId, faces,
                  new[] { double.NaN, -5.0, 5.0, 5.0 }, frame, color),
            "invalid area id, stale revision, empty faces and non-finite rects are rejected without throwing");

        // 전부 제외 — 빈 결과도 정상이다.
        Check(Bridge.StageAreaHighlightMesh(4001, revision, workingId, faces, covered, frame, color),
            "staging a rect that covers the whole face succeeds");
        Check(Bridge.GetAreaHighlightTriangleCount(4001) == 0,
            "a fully excluded face stages an empty (valid) clipped mesh");
        Check(Bridge.CommitAreaHighlight(4001) && Bridge.GetAreaHighlightTriangleCount(4001) == 0,
            "committing an empty clipped mesh displays nothing");
        Check(Bridge.GetAreaHighlightTriangleCount(4001) == 0,
            "a committed empty clipped result keeps reporting zero triangles");
        Bridge.ClearAreaHighlight(4001);
        Check(Bridge.GetAreaHighlightTriangleCount(4001) == -1,
            "clearing a fully excluded area removes its entry");

        // 일부만 제외 — 남은 영역은 채움 메시로 표시된다.
        Check(Bridge.StageAreaHighlightMesh(4002, revision, workingId, faces, hole, frame, color),
            "staging a partial exclusion succeeds");
        int clipped = Bridge.GetAreaHighlightTriangleCount(4002);
        Check(clipped > 0, "partial exclusion keeps a filled remaining mesh");
        Check(Bridge.CommitAreaHighlight(4002) && Bridge.GetAreaHighlightTriangleCount(4002) == clipped,
            "commit displays the staged clipped triangulation");
        Bridge.ClearAreaHighlight(4002);
        Check(Bridge.GetAreaHighlightTriangleCount(4002) == -1,
            "clearing the area removes the committed filled mesh");

        // 사각형이 삼각형과 겹치지 않으면 원본 삼각분할이 그대로 남는다.
        Check(Bridge.StageAreaHighlightMesh(4003, revision, workingId, faces, outside, frame, color),
            "staging a rect far outside the face succeeds");
        int intact = Bridge.GetAreaHighlightTriangleCount(4003);
        Check(intact > 0 && intact != clipped,
            "a non-intersecting rect keeps the original triangles while clipping rebuilds fragments");
        Bridge.DiscardAreaHighlight(4003);
        Check(Bridge.GetAreaHighlightTriangleCount(4003) == -1,
            "discarding a staged mesh leaves no committed or staged entry");

        // ---- 반평면(분할 절단선) 마스크: 제외 사각형이 없어도 자식이 가진 쪽만 채운다 ----
        var upperOnly = new[] { 1.0, 0.0, 0.0 }; // axis 1(y), limit 0, keep y >= 0
        Check(!Bridge.StageAreaHighlightMeshClipped(4004, revision, workingId, faces,
                  Array.Empty<double>(), new[] { 1.0, 0.0 }, frame, color),
            "a clipped stage with a malformed half-plane list is rejected");
        Check(Bridge.StageAreaHighlightMeshClipped(4005, revision, workingId, faces,
                  Array.Empty<double>(), upperOnly, frame, color),
            "staging with only a half-plane mask (no exclusion rects) succeeds");
        int splitHalf = Bridge.GetAreaHighlightTriangleCount(4005);
        Check(splitHalf > 0, "the retained side of a half-plane mask keeps a filled mesh");
        Check(Bridge.CommitAreaHighlight(4005) && Bridge.GetAreaHighlightTriangleCount(4005) == splitHalf,
            "committing a half-plane clipped mesh displays it");
        Bridge.ClearAreaHighlight(4005);
        // 절단선 위/아래 어느 쪽이든 채움은 남고, 면 밖만 남기는 마스크는 빈 결과가 된다.
        Check(Bridge.StageAreaHighlightMeshClipped(4006, revision, workingId, faces,
                  Array.Empty<double>(), new[] { 1.0, 0.0, 1.0 }, frame, color) &&
              Bridge.GetAreaHighlightTriangleCount(4006) > 0,
            "the opposite half-plane mask also keeps its own side");
        Bridge.ClearAreaHighlight(4006);
        Check(Bridge.StageAreaHighlightMeshClipped(4007, revision, workingId, faces,
                  Array.Empty<double>(), new[] { 1.0, -100.0, 1.0 }, frame, color) &&
              Bridge.GetAreaHighlightTriangleCount(4007) == 0,
            "a retained side outside the face has no triangles");
        Bridge.ClearAreaHighlight(4007);
        Check(Bridge.StageAreaHighlightMeshClipped(4008, revision, workingId, faces,
                  Array.Empty<double>(), new[] { 1.0, 1000.0, 1.0 }, frame, color) &&
              Bridge.GetAreaHighlightTriangleCount(4008) == intact,
            "a mask that contains the whole face leaves the source triangles intact");
        Bridge.ClearAreaHighlight(4008);

        // ---- 관리측 배선: 제외가 있는 영역 활성 → 채움 메시, 제외 해제 → Face 색칠 복귀 ----
        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Frame")!.SetValue(area,
            new ProjectionFrame(new Vec3(0, 0, 100), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        areaType.GetProperty("AreaId")!.SetValue(area, 4101);
        var areaFaces = (HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!;
        areaFaces.Add(topFace);
        var areaRegion = (List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area)!;
        areaRegion.Add(new() { new(-20, -10), new(20, -10), new(20, 10), new(-20, 10) });
        var areaExclusions = (IList)areaType.GetProperty("Exclusions")!.GetValue(area)!;
        areaExclusions.Add((-5d, -5d, 5d, 5d));
        // 합성 영역이므로 원본 CAD 경계 추출 없이 동일 계약(비어 있는 참조 집합)을 부여한다.
        areaType.GetProperty("RegionCadBoundary")!.SetValue(area,
            new CadExitBoundary2D(side.Id.Value, side.Revision.Value, Array.Empty<ExitEdgeReference>()));
        var areas = (IList)Field<object>(window, "_areas");
        areas.Add(area);
        Invoke(window, "ActivateArea", area);
        Check(Bridge.GetAreaHighlightTriangleCount(4101) > 0,
            "activating an area with exclusions displays the clipped filled mesh");

        Invoke(window, "ClearExclusions_Click", null!, null!);
        Check(Bridge.GetAreaHighlightTriangleCount(4101) == -1 &&
              ((IList)areaType.GetProperty("Exclusions")!.GetValue(area)!).Count == 0,
            "clearing exclusions drops the clipped mesh and returns to face coloring");

        // ---- 분할 영역: 절단선으로 잘린 자기 몫만 채움 표시한다 ----
        var splitType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.SplitRegionBoundary2D")!;
        var parentRing = new List<Vec2> { new(-20, -10), new(20, -10), new(20, 10), new(-20, 10) };
        var upperRing = new List<Vec2> { new(-20, 0), new(20, 0), new(20, 10), new(-20, 10) };
        var cuts = new List<(Vec2 A, Vec2 B)> { (new Vec2(-100, 0), new Vec2(100, 0)) };
        var splitCad = new CadExitBoundary2D(side.Id.Value, side.Revision.Value,
            Array.Empty<ExitEdgeReference>());
        object?[] createArgs =
        {
            splitCad, parentRing, upperRing,
            new List<(double MinX, double MinY, double MaxX, double MaxY)>(), cuts, null, null
        };
        bool created = (bool)splitType.GetMethod("TryCreate", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, createArgs)!;
        Check(created && createArgs[5] != null,
            "split child boundary is created for the highlight check");
        if (created && createArgs[5] is IExitBoundary2D splitBoundary)
        {
            // 마스크가 자식의 유지 측면을 정확히 가리키는지 먼저 확인한다.
            object?[] planeArgs = { null, null };
            bool hasPlanes = (bool)splitType
                .GetMethod("TryGetRetainedHalfPlanes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(splitBoundary, planeArgs)!;
            int planeAxis = -1;
            double planeLimit = double.NaN;
            bool planeKeepLess = true;
            if (planeArgs[0] is IEnumerable planeList)
                foreach (object item in planeList)
                {
                    var itemType = item.GetType();
                    planeAxis = (int)itemType.GetProperty("Axis")!.GetValue(item)!;
                    planeLimit = (double)itemType.GetProperty("Limit")!.GetValue(item)!;
                    planeKeepLess = (bool)itemType.GetProperty("KeepLess")!.GetValue(item)!;
                    break;
                }
            Check(hasPlanes && planeAxis == 1 && Math.Abs(planeLimit) < 1e-9 && !planeKeepLess,
                "split highlight mask keeps the child's own side of the cut line");

            var splitArea = Activator.CreateInstance(areaType, nonPublic: true)!;
            areaType.GetProperty("Frame")!.SetValue(splitArea,
                new ProjectionFrame(new Vec3(0, 0, 100), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
            areaType.GetProperty("AreaId")!.SetValue(splitArea, 4103);
            ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(splitArea)!).Add(topFace);
            ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(splitArea)!).Add(upperRing);
            // 실제 분할 자식은 부모의 인출 체인을 그대로 물려받는다 — 외곽 표시가 그 체인을 쓴다.
            ((List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(splitArea)!).AddRange(upperRing);
            ((List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(splitArea)!).AddRange(new[]
            {
                new Vec3(-20, 0, 20), new Vec3(20, 0, 20), new Vec3(20, 10, 20), new Vec3(-20, 10, 20)
            });
            areaType.GetProperty("RegionCadBoundary")!.SetValue(splitArea, splitCad);
            areaType.GetProperty("RegionExitBoundary")!.SetValue(splitArea, splitBoundary);
            areaType.GetProperty("ExitBoundary")!.SetValue(splitArea, splitBoundary);
            ((List<(Vec2 A, Vec2 B)>)areaType.GetProperty("SplitCutLines")!.GetValue(splitArea)!)
                .AddRange(cuts);
            areas.Add(splitArea);
            Invoke(window, "ActivateArea", splitArea);
            int splitFill = Bridge.GetAreaHighlightTriangleCount(4103);
            Check(splitFill > 0,
                "activating a split area displays a filled mesh clipped by its cut line");
            areas.Remove(splitArea);
            Bridge.ClearAreaHighlight(4103);
        }

        // 빈 Region/FaceIdSet 영역 활성은 예외 없이 통과해야 한다(하네스 방어).
        var emptyArea = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Frame")!.SetValue(emptyArea,
            new ProjectionFrame(new Vec3(0, 0, 100), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        areaType.GetProperty("AreaId")!.SetValue(emptyArea, 4102);
        areas.Add(emptyArea);
        bool activated = true;
        try { Invoke(window, "ActivateArea", emptyArea); }
        catch (Exception) { activated = false; }
        Check(activated && Bridge.GetAreaHighlightTriangleCount(4102) == -1,
            "activating an area without faces or region neither throws nor draws a highlight");

        // 테스트가 심은 상태를 되돌린다.
        Invoke(window, "ClearAllAreas", false);
        Invoke(window, "UpdateActionStates");
        Check(areas.Count == 0, "area highlight checks leave no synthetic working areas");

        Console.WriteLine($"{count} area highlight checks passed.");
    }
}
