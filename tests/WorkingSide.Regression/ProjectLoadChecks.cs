using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 실제 STEP 한 개와 최소 확정 영역을 저장한 뒤, UI와 같은 경로로 다시 열어
/// 후보 검증/커밋, Face 재매핑, 작업평면 프레임, 영역 경계 복원을 확인한다.
/// </summary>
static class ProjectLoadChecks
{
    static int count;
    static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS " + label);
        ++count;
    }

    static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    static ProjectionFrame Frame(WorkingContext context) =>
        new(new Vec3(context.Frame[0], context.Frame[1], context.Frame[2]),
            new Vec3(context.Frame[9], context.Frame[10], context.Frame[11]),
            new Vec3(context.Frame[3], context.Frame[4], context.Frame[5]));

    public static void Run(MainWindow window, string fixtureDir)
    {
        string stepPath = Path.Combine(fixtureDir, "layers.stp");
        Check(Bridge.LoadStep(stepPath), "project load fixture loaded");

        WorkingContext context = Bridge.CreateWorkingContext(0, 0, 1, 1, 0, 0);
        Check(context != null, "project load working context created");
        ProjectionFrame frame = Frame(context);

        object session = Field(window, "_workingSide");
        session.GetType().GetMethod("RestoreConfirmed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(session, new object[]
            {
                new WorkingSideDefinition(new WorkingSideId(context.Id),
                    new ModelRevision(context.Revision), frame)
            });
        SetField(window, "_modelPath", stepPath);
        SetField(window, "_modelFileName", Path.GetFileNameWithoutExtension(stepPath));
        SetField(window, "_modelRevision", context.Revision);

        WorkingHit hit = Bridge.QueryWorkingSurface(context.Id, context.Revision, 0, 0);
        Check(hit.Status == WorkingQueryStatus.Hit && hit.FaceId >= 0,
            "project load finds a real top face");
        int faceId = hit.FaceId;
        RegionBoundaryLoop[] loops = Bridge.GetSelectedRegionBoundary(
            new[] { faceId }, GeometryTolerancePolicy.DistanceTolerance, out string regionFailure);
        Check(loops != null && loops.Length == 1 && loops[0] != null && loops[0].Closed,
            "project load region boundary available: " + (regionFailure ?? ""));
        Check(loops[0].EdgeReferences != null && loops[0].EdgeReferences.Length > 0,
            "project load region boundary retains original edge references");

        var sourcePoints = new List<Vec3>();
        double[] flat = loops[0].Points;
        for (int i = 0; i + 2 < flat.Length; i += 3)
            sourcePoints.Add(new Vec3(flat[i], flat[i + 1], flat[i + 2]));
        if (sourcePoints.Count > 1 && (sourcePoints[0] - sourcePoints[^1]).Length < 1e-9)
            sourcePoints.RemoveAt(sourcePoints.Count - 1);
        Check(sourcePoints.Count >= 3, "project load region has a non-degenerate loop");
        var polygon = new List<Vec2>();
        foreach (Vec3 point in sourcePoints) polygon.Add(frame.Project(point));
        if (RegionOps.SignedArea(polygon) < 0) polygon.Reverse();

        Type areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        object area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(area, "LoadCheck");
        areaType.GetProperty("AreaId")!.SetValue(area, 8801);
        areaType.GetProperty("Frame")!.SetValue(area, frame);
        areaType.GetProperty("MarginText")!.SetValue(area, "5");
        areaType.GetProperty("SpacingText")!.SetValue(area, "50");
        areaType.GetProperty("ExitFaceId")!.SetValue(area, -1);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(faceId);
        ((IList)areaType.GetProperty("Region")!.GetValue(area)!).Add(polygon);
        areaType.GetProperty("ExitChain")!.SetValue(area, polygon);
        areaType.GetProperty("ExitChain3D")!.SetValue(area, sourcePoints);
        areaType.GetProperty("ExitChainFaceIds")!.SetValue(area, new List<int>());
        areaType.GetProperty("ExitEdgeRefs")!.SetValue(area,
            new List<ExitEdgeReference>(loops[0].EdgeReferences));

        int stagedId = -8899;
        RoutePieceResult staged = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            context.Id, context.Revision, stagedId, "HeatingRun",
            new[] { -10.0, 0, 20, 10, 0, 20 }, new[] { faceId, faceId });
        Check(staged != null, "project load stages a committed route");
        Check(Bridge.CommitWorkingRouteGeometryDelta(context.Id, context.Revision,
                new[] { stagedId }, new[] { 8899 }, Array.Empty<int>(),
                Array.Empty<int>(), Array.Empty<int>()),
            "project load commits the route before saving");
        var routePoints = new List<Vec2> { new(-10, 0), new(10, 0) };
        var routePiece = new SerpentineRoutePiece(
            SerpentineRoutePieceKind.HeatingRun, 0, routePoints);
        areaType.GetProperty("Pattern")!.SetValue(area, new SerpentineResult
        {
            Path = routePoints,
            Segments = new[] { new SerpentineSegment(0, routePoints[0], routePoints[1]) },
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = 20,
            RoutePieces = new[] { routePiece }
        });
        areaType.GetProperty("ProjectedPieces")!.SetValue(area, new List<RoutePieceResult> { staged });
        areaType.GetProperty("NativePieceIds")!.SetValue(area, new[] { 8899 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(area, new[] { staged.Length });
        areaType.GetProperty("PatternDisplayed")!.SetValue(area, true);
        areaType.GetProperty("RouteEdited")!.SetValue(area, true);

        var areas = (IList)Field(window, "_areas");
        areas.Clear();
        areas.Add(area);
        SetField(window, "_activeArea", area);

        string projectPath = Path.Combine(fixtureDir, "project-load-check.json");
        if (File.Exists(projectPath)) File.Delete(projectPath);
        try
        {
            var document = (ProjectDocument)Invoke(window, "BuildProjectDocument", projectPath);
            ProjectJsonStore.Save(document, projectPath);
            SetField(window, "_projectPath", projectPath);
            SetField(window, "_savedProjectFingerprint", ProjectJsonStore.ComputeFingerprint(document));
            SetField(window, "_modelContentHash", document.Source.Sha256);

            bool loaded = (bool)Invoke(window, "LoadProject", projectPath);
            Check(loaded, "project load reports success");

            var restoredAreas = (IList)Field(window, "_areas");
            Check(restoredAreas.Count == 1, "project load restores one area");
            object restored = restoredAreas[0]!;
            var restoredFaces = (HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(restored)!;
            Check(restoredFaces.Count == 1, "project load restores the mapped face");
            var restoredRegion = (IList)areaType.GetProperty("Region")!.GetValue(restored)!;
            Check(restoredRegion.Count == 1 && ((IList)restoredRegion[0]!).Count >= 3,
                "project load restores the region polygon");

            object confirmed = session.GetType().GetProperty("Confirmed")!.GetValue(session);
            var restoredFrame = (ProjectionFrame)confirmed!.GetType().GetProperty("Frame")!.GetValue(confirmed)!;
            Check((restoredFrame.Origin - frame.Origin).Length < 1e-8 &&
                  (restoredFrame.AxisX - frame.AxisX).Length < 1e-8 &&
                  (restoredFrame.Normal - frame.Normal).Length < 1e-8,
                "project load restores the exact working frame");

            var restoredIds = (int[])areaType.GetProperty("NativePieceIds")!.GetValue(restored)!;
            Check(restoredIds.Length == 1, "project load restores one committed route id");
            object idValue = confirmed.GetType().GetProperty("Id")!.GetValue(confirmed)!;
            object revisionValue = confirmed.GetType().GetProperty("Revision")!.GetValue(confirmed)!;
            long restoredContextId = (long)idValue.GetType().GetProperty("Value")!.GetValue(idValue)!;
            long restoredRevision = (long)revisionValue.GetType().GetProperty("Value")!.GetValue(revisionValue)!;
            CommittedRouteChain[] chains = Bridge.GetCommittedRouteChainsDetailed(
                restoredContextId, restoredRevision, restoredIds);
            Check(chains != null && chains.Length == 1 && chains[0].Points != null &&
                  chains[0].Points.Length == 6 && chains[0].SupportFaceIds != null &&
                  chains[0].SupportFaceIds.Length == 2,
                "project load restores the committed 3D chain and support faces");
            Check((bool)areaType.GetProperty("RouteEdited")!.GetValue(restored)!,
                "project load preserves the manually edited route flag");
        }
        finally
        {
            try { File.Delete(projectPath); } catch { }
        }
        Console.WriteLine($"{count} project load checks passed.");
    }
}
