using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
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

    static object StaticInvoke(Type type, string name, params object[] args) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

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
        areaType.GetProperty("MarginText")!.SetValue(area, "7");
        areaType.GetProperty("SpacingText")!.SetValue(area, "50");
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(faceId);
        ((IList)areaType.GetProperty("Region")!.GetValue(area)!).Add(polygon);
        areaType.GetProperty("ExitChain")!.SetValue(area, polygon);
        areaType.GetProperty("ExitChain3D")!.SetValue(area, sourcePoints);
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
        areaType.GetProperty("AppliedInputs")!.SetValue(area,
            Invoke(window, "CaptureAppliedInputs", area, 5.0, 50.0));

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

            // 프로젝트를 열면 저장된 작업면을 화면 정면으로 맞춘다. 카메라 상태는 native에서
            // 직접 읽을 수 없으므로, 복원된 컨텍스트 ID/리비전으로 뷰 준비 호출이 정확히
            // 한 번 실행되는지를 확인한다.
            var prepareFront = (Func<long, long, bool>)Field(window, "_prepareWorkingFront");
            int frontCalls = 0;
            long frontId = 0, frontRevision = 0;
            SetField(window, "_prepareWorkingFront",
                new Func<long, long, bool>((id, revision) =>
                {
                    ++frontCalls; frontId = id; frontRevision = revision; return true;
                }));
            bool loaded;
            try
            {
                loaded = (bool)Invoke(window, "LoadProject", projectPath);
            }
            finally
            {
                SetField(window, "_prepareWorkingFront", prepareFront);
            }
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
            Check(frontCalls == 1 && frontId == restoredContextId && frontRevision == restoredRevision,
                "project load aligns the view to the restored working front");
            CommittedRouteChain[] chains = Bridge.GetCommittedRouteChainsDetailed(
                restoredContextId, restoredRevision, restoredIds);
            Check(chains != null && chains.Length == 1 && chains[0].Points != null &&
                  chains[0].Points.Length == 6 && chains[0].SupportFaceIds != null &&
                  chains[0].SupportFaceIds.Length == 2,
                "project load restores the committed 3D chain and support faces");
            Check((bool)Invoke(window, "RefreshRouteColors"),
                "project load re-colors committed routes with the area color");
            Check((bool)areaType.GetProperty("RouteEdited")!.GetValue(restored)!,
                "project load preserves the manually edited route flag");
            Check((string)areaType.GetProperty("MarginText")!.GetValue(restored)! == "7",
                "project load preserves the unapplied draft text");
            Check((bool)Invoke(window, "IsPatternInputStale", restored),
                "project load preserves stale applied-input status");
        }
        finally
        {
            try { File.Delete(projectPath); } catch { }
        }
        Console.WriteLine($"{count} project load checks passed.");
        RunExclusionOutlineCheck(window, fixtureDir);
    }

    /// <summary>
    /// 제외영역이 적용된 영역을 실제 저장·로드 경로로 다시 열어, 표시용 외곽선과
    /// 확정 경로 매핑이 제외 후 경계를 쓰는지 실제 STEP으로 확인한다.
    /// 저장 파일은 JSON 왕복이 아니라 MainWindow.LoadProject를 직접 통과한다.
    /// </summary>
    static void RunExclusionOutlineCheck(MainWindow window, string fixtureDir)
    {
        string stepPath = Path.Combine(fixtureDir, "exitSides.stp");
        Check(Bridge.LoadStep(stepPath), "exclusion project fixture loaded");
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1)),
            "exclusion project +Z side confirmed");
        object session = Field(window, "_workingSide");
        var side = (WorkingSideDefinition)session.GetType().GetProperty("Confirmed")!.GetValue(session)!;
        Check(side != null, "exclusion project has a confirmed working side");
        if (side == null) return;
        SetField(window, "_modelPath", stepPath);
        SetField(window, "_modelFileName", Path.GetFileNameWithoutExtension(stepPath));
        SetField(window, "_modelRevision", side.Revision.Value);
        SetField(window, "_modelContentHash", ProjectJsonStore.TryComputeFileSha256(stepPath));

        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            double[] probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "exclusion project finds the box top face");
        if (topFace < 0) return;

        RegionBoundaryLoop[] loops = Bridge.GetSelectedRegionBoundary(
            new[] { topFace }, GeometryTolerancePolicy.DistanceTolerance, out string regionFailure);
        Check(loops != null && loops.Length == 1 && loops[0] != null && loops[0].Closed &&
              loops[0].EdgeReferences != null && loops[0].EdgeReferences.Length > 0,
            "exclusion project region boundary is available: " + (regionFailure ?? ""));
        if (loops == null || loops.Length != 1) return;

        Type areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        object area = Activator.CreateInstance(areaType, nonPublic: true)!;
        var ring = new List<Vec2>();
        var chain = (List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area)!;
        var chain3D = (List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(area)!;
        for (int k = 0; k + 2 < loops[0].Points.Length; k += 3)
        {
            var point = new Vec3(loops[0].Points[k], loops[0].Points[k + 1], loops[0].Points[k + 2]);
            Vec2 projected = side.Frame.Project(point);
            ring.Add(projected);
            chain.Add(projected);
            chain3D.Add(point);
        }
        if (RegionOps.SignedArea(ring) < 0) ring.Reverse();

        double minX = ring.Min(p => p.X), maxX = ring.Max(p => p.X);
        double minY = ring.Min(p => p.Y), maxY = ring.Max(p => p.Y);
        double cutWidth = (maxX - minX) * 0.3;
        double cutHeight = (maxY - minY) * 0.3;
        var exclusion = (maxX - cutWidth, minY - 1.0, maxX + 1.0, minY + cutHeight);

        areaType.GetProperty("Name")!.SetValue(area, "ExclusionProjectLoad");
        areaType.GetProperty("AreaId")!.SetValue(area, 9901);
        areaType.GetProperty("Frame")!.SetValue(area, side.Frame);
        areaType.GetProperty("Direction")!.SetValue(area, PatternDirection.FrameX);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(topFace);
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area)!).Add(ring);
        var exclusions = (List<(double MinX, double MinY, double MaxX, double MaxY)>)
            areaType.GetProperty("Exclusions")!.GetValue(area)!;
        exclusions.Add(exclusion);

        var refs = (List<ExitEdgeReference>)areaType.GetProperty("ExitEdgeRefs")!.GetValue(area)!;
        foreach (ExitEdgeReference reference in loops[0].EdgeReferences)
            refs.Add(CadExitBoundary2D.CopyReference(reference));

        var regionCad = new CadExitBoundary2D(side.Id.Value, side.Revision.Value, loops[0].EdgeReferences);
        areaType.GetProperty("RegionCadBoundary")!.SetValue(area, regionCad);
        areaType.GetProperty("RegionBoundary")!.SetValue(area, new ExactRegionContainment(
            new CadRegionBoundary2D(side.Id.Value, side.Revision.Value, loops[0].EdgeReferences)));
        Check(MainWindow.TryBuildRegionExitBoundary(regionCad, ring, exclusions,
                out IExitBoundary2D builtBoundary, out string buildFailure),
            "exclusion project builds the effective outline: " + buildFailure);
        var effective = builtBoundary as EffectiveRegionBoundary2D;
        Check(effective != null && effective.Segments.Count == 2,
            "exclusion project exposes two artificial cut sides");
        if (effective == null) return;
        areaType.GetProperty("RegionExitBoundary")!.SetValue(area, effective);
        areaType.GetProperty("ExitBoundary")!.SetValue(area, effective);

        ExitBoundaryResolution startResolution = effective.Project(
            new Vec2(maxX - cutWidth, minY + cutHeight * 0.5), 1.0);
        ExitBoundaryResolution endResolution = effective.Project(
            new Vec2((minX + maxX) * 0.5, maxY), 1.0);
        Check(startResolution.Succeeded && endResolution.Succeeded &&
              startResolution.Point!.Value.ArtificialBinding.HasValue &&
              endResolution.Point!.Value.Binding.HasValue,
            "exclusion project selects one artificial and one original CAD endpoint");
        if (!startResolution.Succeeded || !endResolution.Succeeded) return;
        ExitBoundaryPoint selectedStart = startResolution.Point!.Value;
        ExitBoundaryPoint selectedEnd = endResolution.Point!.Value;
        areaType.GetProperty("SelectedStartExit")!.SetValue(area, selectedStart);
        areaType.GetProperty("SelectedEndExit")!.SetValue(area, selectedEnd);

        var routePoints = new List<Vec2> { selectedStart.Point, selectedEnd.Point };
        var pattern = new SerpentineResult
        {
            Path = routePoints,
            Segments = new[] { new SerpentineSegment(0, routePoints[0], routePoints[1]) },
            LaneCount = 1,
            NominalLaneCount = 1,
            AngleDeg = 0,
            TotalLength = selectedStart.Point.DistanceTo(selectedEnd.Point),
            RoutePieces = new[] { new SerpentineRoutePiece(
                SerpentineRoutePieceKind.HeatingRun, 0, routePoints) },
            StartExit = new CircuitExitPoint(CircuitExitRole.Start, selectedStart.Point,
                selectedStart.SegmentIndex, selectedStart.SegmentParameter)
            {
                Binding = selectedStart.Binding,
                ArtificialBinding = selectedStart.ArtificialBinding
            },
            EndExit = new CircuitExitPoint(CircuitExitRole.End, selectedEnd.Point,
                selectedEnd.SegmentIndex, selectedEnd.SegmentParameter)
            {
                Binding = selectedEnd.Binding,
                ArtificialBinding = selectedEnd.ArtificialBinding
            }
        };
        areaType.GetProperty("Pattern")!.SetValue(area, pattern);
        ((IList)areaType.GetProperty("Circuits")!.GetValue(area)!).Add(CircuitRoute.FromResult(pattern));
        ((IList)areaType.GetProperty("CircuitPieceIndices")!.GetValue(area)!).Add(new List<int> { 0 });

        // 프레임 원점이 월드 원점과 다를 수 있으므로 2D 좌표를 그대로 쓰지 않고
        // 선택 면 위의 실제 3D 점으로 매핑한다.
        var startHit = Bridge.MapWorkingPointOntoFaces(side.Id.Value, side.Revision.Value,
            new[] { topFace }, selectedStart.Point.X, selectedStart.Point.Y);
        var endHit = Bridge.MapWorkingPointOntoFaces(side.Id.Value, side.Revision.Value,
            new[] { topFace }, selectedEnd.Point.X, selectedEnd.Point.Y);
        Check(startHit != null && startHit.Status == WorkingQueryStatus.Hit && startHit.Point != null,
            "exclusion project maps the artificial endpoint onto the face");
        Check(endHit != null && endHit.Status == WorkingQueryStatus.Hit && endHit.Point != null,
            "exclusion project maps the CAD endpoint onto the face");
        if (startHit?.Point == null || endHit?.Point == null) return;

        int stagedId = -9901;
        RoutePieceResult staged = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            side.Id.Value, side.Revision.Value, stagedId, "HeatingRun",
            new[]
            {
                startHit.Point[0], startHit.Point[1], startHit.Point[2],
                endHit.Point[0], endHit.Point[1], endHit.Point[2]
            },
            new[] { topFace, topFace });
        Check(staged != null, "exclusion project stages a committed route");
        Check(Bridge.CommitWorkingRouteGeometryDelta(side.Id.Value, side.Revision.Value,
                new[] { stagedId }.ToArray(), new[] { 9901 }.ToArray(), Array.Empty<int>(),
                Array.Empty<int>(), Array.Empty<int>()),
            "exclusion project commits the route before saving");
        areaType.GetProperty("ProjectedPieces")!.SetValue(area, new List<RoutePieceResult> { staged });
        areaType.GetProperty("NativePieceIds")!.SetValue(area, new[] { 9901 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(area, new[] { staged.Length });
        areaType.GetProperty("PatternDisplayed")!.SetValue(area, true);
        areaType.GetProperty("RouteEdited")!.SetValue(area, true);
        areaType.GetProperty("MarginText")!.SetValue(area, "5");
        areaType.GetProperty("SpacingText")!.SetValue(area, "10");
        areaType.GetProperty("AppliedInputs")!.SetValue(area, Invoke(window, "CaptureAppliedInputs", area, 5.0, 10.0));
        areaType.GetProperty("CommittedMapping")!.SetValue(area,
            CommittedRouteContext.Capture((WorkingArea)area));

        ((IList)Field(window, "_areas")).Add(area);
        SetField(window, "_activeArea", area);

        var savedOutline = effective.Outline.Select(p => new Vec2(p.X, p.Y)).ToList();
        var savedSegments = effective.Segments.Select(s =>
            (s.Binding.MinX, s.Binding.MinY, s.Binding.MaxX, s.Binding.MaxY,
             s.Binding.Side, s.Binding.FirstParameter, s.Binding.LastParameter)).ToList();
        ExitEdgeBinding savedEndBinding = selectedEnd.Binding!.Value;
        double savedLength = ((double[])areaType.GetProperty("RoutePieceLengths")!.GetValue(area)!).Sum();

        string projectPath = Path.Combine(fixtureDir, "project-exclusion-load-check.json");
        if (File.Exists(projectPath)) File.Delete(projectPath);
        try
        {
            var document = (ProjectDocument)Invoke(window, "BuildProjectDocument", projectPath);
            ProjectJsonStore.Save(document, projectPath);
            SetField(window, "_projectPath", projectPath);
            SetField(window, "_savedProjectFingerprint", ProjectJsonStore.ComputeFingerprint(document));
            SetField(window, "_modelContentHash", document.Source.Sha256);

            Check((bool)Invoke(window, "LoadProject", projectPath),
                "exclusion project load reports success");

            var restoredAreas = (IList)Field(window, "_areas");
            Check(restoredAreas.Count == 1, "exclusion project restores one area");
            object restored = restoredAreas[0]!;
            var restoredEffective = areaType.GetProperty("RegionExitBoundary")!.GetValue(restored)
                as EffectiveRegionBoundary2D;
            Check(restoredEffective != null,
                "exclusion project restores the effective boundary instead of the original outline");
            if (restoredEffective == null) return;
            Check(areaType.GetProperty("RegionCadBoundary")!.GetValue(restored) is CadExitBoundary2D,
                "exclusion project keeps the original CAD boundary separately");
            Check(ReferenceEquals(areaType.GetProperty("ExitBoundary")!.GetValue(restored), restoredEffective),
                "region-mode exit boundary uses the exclusion-aware outline");

            bool sameOutline = restoredEffective.Outline.Count == savedOutline.Count &&
                savedOutline.All(saved => restoredEffective.Outline.Any(
                    restoredPoint => restoredPoint.DistanceTo(saved) < 1e-6));
            Check(sameOutline, "restored outline keeps the saved exclusion cut");
            Check(restoredEffective.Outline.All(point =>
                    !(point.X > maxX - cutWidth + 1e-6 && point.Y < minY + cutHeight - 1e-6)),
                "restored outline no longer contains the removed corner");

            bool sameSegments = restoredEffective.Segments.Count == savedSegments.Count &&
                savedSegments.All(saved => restoredEffective.Segments.Any(segment =>
                    Math.Abs(segment.Binding.MinX - saved.MinX) < 1e-9 &&
                    Math.Abs(segment.Binding.MinY - saved.MinY) < 1e-9 &&
                    Math.Abs(segment.Binding.MaxX - saved.MaxX) < 1e-9 &&
                    Math.Abs(segment.Binding.MaxY - saved.MaxY) < 1e-9 &&
                    segment.Binding.Side == saved.Side &&
                    Math.Abs(segment.Binding.FirstParameter - saved.FirstParameter) < 1e-9 &&
                    Math.Abs(segment.Binding.LastParameter - saved.LastParameter) < 1e-9));
            Check(sameSegments, "restored artificial segments keep their identity intervals");

            object restoredStartValue = areaType.GetProperty("SelectedStartExit")!.GetValue(restored)!;
            object restoredEndValue = areaType.GetProperty("SelectedEndExit")!.GetValue(restored)!;
            Check(restoredStartValue != null && restoredEndValue != null,
                "exclusion project restores both selected exit points");
            if (restoredStartValue == null || restoredEndValue == null) return;
            var restoredStart = (ExitBoundaryPoint)restoredStartValue;
            var restoredEnd = (ExitBoundaryPoint)restoredEndValue;
            ArtificialEdgeBinding restoredStartBinding = restoredStart.ArtificialBinding!.Value;
            ExitEdgeBinding restoredEndBinding = restoredEnd.Binding!.Value;
            Check(Math.Abs(restoredStartBinding.MinX - selectedStart.ArtificialBinding!.Value.MinX) < 1e-9 &&
                  Math.Abs(restoredStartBinding.MinY - selectedStart.ArtificialBinding!.Value.MinY) < 1e-9 &&
                  Math.Abs(restoredStartBinding.MaxX - selectedStart.ArtificialBinding!.Value.MaxX) < 1e-9 &&
                  Math.Abs(restoredStartBinding.MaxY - selectedStart.ArtificialBinding!.Value.MaxY) < 1e-9 &&
                  restoredStartBinding.Side == selectedStart.ArtificialBinding!.Value.Side &&
                  Math.Abs(restoredStartBinding.Parameter -
                      selectedStart.ArtificialBinding!.Value.Parameter) < 1e-6 &&
                  restoredStart.Point.DistanceTo(selectedStart.Point) < 1e-6,
                "restored start point keeps the artificial exclusion provenance");
            Check(restoredEndBinding.EdgeKey == savedEndBinding.EdgeKey &&
                  Math.Abs(restoredEndBinding.Parameter - savedEndBinding.Parameter) < 1e-6 &&
                  restoredEnd.Point.DistanceTo(selectedEnd.Point) < 1e-6,
                "restored end point keeps the original CAD edge parameter");

            var restoredPattern = (SerpentineResult)areaType.GetProperty("Pattern")!.GetValue(restored)!;
            Check(restoredPattern != null && restoredPattern.StartExit != null &&
                  restoredPattern.StartExit.ArtificialBinding.HasValue &&
                  restoredPattern.RoutePieces.Count == pattern.RoutePieces.Count,
                "exclusion project preserves the pattern and its artificial start provenance");
            Check(((IList)areaType.GetProperty("Circuits")!.GetValue(restored)!).Count == 1,
                "exclusion project preserves the single circuit");

            object mapping = areaType.GetProperty("CommittedMapping")!.GetValue(restored)!;
            Check(mapping != null && ReferenceEquals(
                    mapping.GetType().GetProperty("RegionBoundary")!.GetValue(mapping), restoredEffective),
                "committed route mapping captures the exclusion-aware boundary");
            Check(mapping != null && ReferenceEquals(
                    mapping.GetType().GetProperty("Boundary")!.GetValue(mapping), restoredEffective),
                "committed route mapping keeps the selected-exit region boundary mode");

            object restoredConfirmed = session.GetType().GetProperty("Confirmed")!.GetValue(session)!;
            var restoredFrame = (ProjectionFrame)restoredConfirmed.GetType()
                .GetProperty("Frame")!.GetValue(restoredConfirmed)!;
            object idValue = restoredConfirmed.GetType().GetProperty("Id")!.GetValue(restoredConfirmed)!;
            object revisionValue = restoredConfirmed.GetType().GetProperty("Revision")!.GetValue(restoredConfirmed)!;
            long restoredContextId = (long)idValue.GetType().GetProperty("Value")!.GetValue(idValue)!;
            long restoredRevision = (long)revisionValue.GetType().GetProperty("Value")!.GetValue(revisionValue)!;
            var restoredIds = (int[])areaType.GetProperty("NativePieceIds")!.GetValue(restored)!;
            CommittedRouteChain[] chains = Bridge.GetCommittedRouteChainsDetailed(
                restoredContextId, restoredRevision, restoredIds);
            Check(restoredIds.Length == 1 && chains != null && chains.Length == 1 &&
                  chains[0].Points != null && chains[0].Points.Length == 6 &&
                  chains[0].SupportFaceIds != null && chains[0].SupportFaceIds.Length == 2,
                "exclusion project restores the committed 3D chain and support faces");
            double restoredLength =
                ((double[])areaType.GetProperty("RoutePieceLengths")!.GetValue(restored)!).Sum();
            Check(Math.Abs(restoredLength - savedLength) < 1e-5,
                "exclusion project keeps the committed route length");

            var display = (List<Vec3>)StaticInvoke(typeof(MainWindow), "BuildEffectiveOutline3D",
                restored, restoredEffective);
            Check(display != null && display.Count >= 4,
                "exclusion project produces a display outline");
            bool cutCornerAbsent = display != null && display.All(point =>
            {
                Vec2 projected = restoredFrame.Project(point);
                return !(projected.X > maxX - cutWidth + 0.1 &&
                         projected.Y < minY + cutHeight - 0.1);
            });
            Check(cutCornerAbsent, "display outline does not draw the removed corner");
        }
        finally
        {
            try { File.Delete(projectPath); } catch { }
        }
        Console.WriteLine($"{count} project load exclusion checks passed.");
    }
}
