using System.IO;
using System.Windows.Interop;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

// Phase 1 curved-surface routing repro: user case #79 (Cylinder heating face) + #11 (BSpline exit face).
// Builds the app's single-face region, generates the anchored serpentine, maps every piece by
// datum-point raycast onto the selected faces, and stages the 3D polylines without re-projection.
static class Program
{
    static int passed, failed;
    static readonly List<string> failures = new();

    // 폴백 계측: 직접 매핑 실패 시 전역 히트→스냅 경로가 실제로 쓰이는지 센다.
    static int directHits, snapHits, snapMisses, looseMisses, anchorShortCircuits;
    static readonly List<string> directFailureStatuses = new();
    static readonly List<string> snapAdopted = new();

    static string Counters() =>
        $"direct={directHits} anchor={anchorShortCircuits} snap={snapHits} snapReject={snapMisses} looseMiss={looseMisses}";

    static void Check(bool value, string label)
    {
        if (value) { Console.WriteLine("PASS " + label); ++passed; }
        else { Console.WriteLine("FAIL " + label); ++failed; failures.Add(label); }
    }

    // Step 6 survey: any single-face patch of the main curved types must route by
    // the same datum raycast and stage without re-projection. Faces that cannot
    // form a single loop are gates, not failures.
    static void Survey(WorkingSideDefinition side, int faceCount)
    {
        var parameters = new PatternParams { Margin = 2, Spacing = 8, Direction = PatternDirection.Auto };
        var types = new[] { "Cylinder", "Cone", "Sphere", "Torus", "BSplineSurface" };
        var counts = new Dictionary<string, int>();
        for (int id = 0; id < faceCount; id++)
        {
            var name = Bridge.GetFaceType(id) ?? "?";
            counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
        }
        Console.WriteLine("face types: " + string.Join(", ",
            counts.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}")));
        var candidates = types.ToDictionary(type => type, _ => new List<int>());
        for (int id = 0; id < faceCount; id++)
        {
            var type = Bridge.GetFaceType(id);
            if (type == null || !candidates.TryGetValue(type, out var list)) continue;
            int cap = type is "Cylinder" or "Cone" ? 60 : 20;
            if (list.Count < cap) list.Add(id);
        }

        foreach (var type in types)
        {
            bool? success = null;
            bool tested = false;
            string detail = "no candidate face";
            int loopsRejected = 0, filterRejected = 0, zeroPieces = 0;
            double bestArea = 0, bestZSpan = 0;
            foreach (int face in candidates[type])
            {
                try
                {
                    var loops = Bridge.GetSelectedRegionBoundary(new[] { face }, 0.05);
                    if (loops == null || loops.Length != 1 || !loops[0].Closed) { ++loopsRejected; continue; }
                    var points3D = new List<Vec3>();
                    for (int i = 0; i + 2 < loops[0].Points.Length; i += 3)
                        points3D.Add(new Vec3(loops[0].Points[i], loops[0].Points[i + 1], loops[0].Points[i + 2]));
                    var ring = points3D.Select(side.Frame.Project).ToList();
                    if (ring.Count < 3) { ++loopsRejected; continue; }
                    if (RegionOps.SignedArea(ring) < 0) ring.Reverse();
                    double area = Math.Abs(RegionOps.SignedArea(ring));
                    double zSpan = points3D.Max(p => p.Z) - points3D.Min(p => p.Z);
                    bestArea = Math.Max(bestArea, area);
                    bestZSpan = Math.Max(bestZSpan, zSpan);
                    if (area < 100 || zSpan < 0.5) { ++filterRejected; continue; }

                    var pattern = Serpentine.Generate(ring, null, parameters, constrainConnectors: true);
                    if (pattern.RoutePieces.Count == 0) { ++zeroPieces; continue; }
                    tested = true;
                    var stages = new List<int>();
                    var results = new List<RoutePieceResult>();
                    string? failure = null;
                    int[] faceSet = { face };
                    for (int i = 0; i < pattern.RoutePieces.Count && failure == null; i++)
                    {
                        var piece = pattern.RoutePieces[i];
                        if (!SurfacePathMapper.TryMapRoute(piece.Points,
                                uv => MapDatum(side, faceSet, uv, null, null, -1), out var samples, out var error))
                        {
                            failure = $"piece {i} map: {error}";
                            break;
                        }
                        var xyz = new double[samples.Count * 3];
                        for (int k = 0; k < samples.Count; k++)
                        {
                            xyz[k * 3] = samples[k].Point.X;
                            xyz[k * 3 + 1] = samples[k].Point.Y;
                            xyz[k * 3 + 2] = samples[k].Point.Z;
                        }
                        int stageId = int.MinValue + 200000 + i;
                        var shown = Bridge.StageSurfaceMappedRoutePiece(side.Id.Value, side.Revision.Value, stageId,
                            piece.Kind.ToString(), xyz, faceSet);
                        if (shown == null)
                        {
                            failure = $"piece {i} stage: {Bridge.GetWorkingStageDiagnostic()}";
                            break;
                        }
                        stages.Add(stageId);
                        results.Add(shown);
                    }
                    if (failure == null)
                        for (int i = 1; i < results.Count && failure == null; i++)
                            if (Distance(results[i - 1].End, results[i].Start) > 0.01) failure = $"connection at {i}";
                    Bridge.DiscardWorkingRouteGeometry(side.Id.Value, side.Revision.Value, stages.ToArray(), Array.Empty<int>());
                    if (failure == null)
                    {
                        success = true;
                        detail = $"face {face}: pieces={pattern.RoutePieces.Count} area2D={area:F1} zSpan={zSpan:F1}";
                        break;
                    }
                    detail = $"face {face} rejected: {failure}";
                }
                catch (Exception ex)
                {
                    detail = $"face {face} exception: {ex.Message}";
                }
            }
            if (success == true) { Console.WriteLine($"PASS {type} patch routes and stages ({detail})"); ++passed; }
            else if (tested) { Console.WriteLine($"FAIL {type} patch failed ({detail})"); ++failed; failures.Add($"{type} patch routing"); }
            else
            {
                Console.WriteLine($"SKIP {type} patch: candidates={candidates[type].Count} noSingleLoop={loopsRejected} " +
                    $"filtered={filterRejected} (bestArea={bestArea:F1} bestZSpan={bestZSpan:F1}) zeroPieces={zeroPieces} last={detail}");
            }
        }

        int closed = 0, closedWithoutLoop = 0;
        int axisFaces = 0, axisParallelZ = 0;
        foreach (var type in new[] { "Sphere", "Torus" })
        {
            int closedFlag = 0, singleLoop = 0;
            for (int id = 0; id < faceCount; id++)
            {
                if (Bridge.GetFaceType(id) != type) continue;
                if (Bridge.FaceRegionIsClosed(id)) ++closedFlag;
                var lp = Bridge.GetSelectedRegionBoundary(new[] { id }, 0.05);
                if (lp != null && lp.Length == 1 && lp[0].Closed) ++singleLoop;
            }
            Console.WriteLine($"{type} topology: closedFlag={closedFlag} singleLoop={singleLoop}");
        }
        for (int id = 0; id < faceCount && closed < 8; id++)
        {
            var type = Bridge.GetFaceType(id);
            if (type is "Cylinder" or "Cone")
            {
                var axis = Bridge.GetFaceAxis(id);
                if (axis != null)
                {
                    ++axisFaces;
                    if (Math.Abs(axis[2]) > 0.999) ++axisParallelZ;
                }
            }
            if (!Bridge.FaceRegionIsClosed(id)) continue;
            ++closed;
            var loops = Bridge.GetSelectedRegionBoundary(new[] { id }, 0.05);
            if (loops == null || loops.Length == 0 || loops.Any(loop => !loop.Closed)) ++closedWithoutLoop;
        }
        Console.WriteLine($"closed-surface gate: {closedWithoutLoop}/{closed} closed faces have no usable region loop");
        if (closed == 0) Console.WriteLine("SKIP closed-surface gate: this model has no face without a free boundary");
        else Check(closedWithoutLoop == closed, "closed surfaces are rejected before region construction");
        Console.WriteLine($"face axis API: {axisFaces} cylinder/cone faces, {axisParallelZ} parallel to +Z");
        Check(axisFaces > 0, "GetFaceAxis returns cylinder/cone axes");
    }

    static string ModelPath(string[] args)
    {
        if (args.Length > 0) return Path.GetFullPath(args[0]);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "resource", "Mold.stp");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("resource/Mold.stp not found; pass the STEP path as the first argument.");
    }

    static ProjectionFrame Frame(WorkingContext c) => new(new(c.Frame[0], c.Frame[1], c.Frame[2]),
        new(c.Frame[9], c.Frame[10], c.Frame[11]), new(c.Frame[3], c.Frame[4], c.Frame[5]));

    static double Distance(double[]? a, double[]? b) =>
        a == null || b == null ? double.MaxValue :
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    static Vec3 Lerp(Vec3 a, Vec3 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    static Vec3 PointOnChain3D(List<Vec2> chain, List<Vec3> chain3D, Vec2 datum)
    {
        double best = double.MaxValue; int bestSegment = 1; double bestT = 0;
        for (int i = 1; i < chain.Count; i++)
        {
            Vec2 a = chain[i - 1], b = chain[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-18 ? 0 : Math.Clamp(((datum.X - a.X) * dx + (datum.Y - a.Y) * dy) / lengthSquared, 0, 1);
            double px = a.X + dx * t - datum.X, py = a.Y + dy * t - datum.Y, distance = px * px + py * py;
            if (distance < best) { best = distance; bestSegment = i; bestT = t; }
        }
        return Lerp(chain3D[bestSegment - 1], chain3D[bestSegment], bestT);
    }

    static SurfaceMappedSample? MapDatum(WorkingSideDefinition side, int[] faces, Vec2 uv,
        Vec2? anchorDatum, Vec3? anchor3D, int exitFaceId)
    {
        if (anchorDatum.HasValue && anchor3D.HasValue && uv == anchorDatum.Value)
        {
            ++anchorShortCircuits;
            return new SurfaceMappedSample(uv, anchor3D.Value, exitFaceId);
        }
        var direct = Bridge.MapWorkingPointOntoFaces(side.Id.Value, side.Revision.Value, faces, uv.X, uv.Y);
        if (direct != null && direct.Status == WorkingQueryStatus.Hit && direct.Point != null)
        {
            ++directHits;
            return new SurfaceMappedSample(uv, new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]), direct.FaceId);
        }
        directFailureStatuses.Add($"({uv.X:F4},{uv.Y:F4}) direct={direct?.Status.ToString() ?? "null"}");
        var loose = Bridge.QueryWorkingSurface(side.Id.Value, side.Revision.Value, uv.X, uv.Y);
        if (loose != null && loose.Status == WorkingQueryStatus.Hit && loose.Point != null)
        {
            var snapped = Bridge.SnapWorkingPointToFaces(loose.Point[0], loose.Point[1], loose.Point[2], faces, 0.5);
            if (snapped != null && snapped.Status == WorkingQueryStatus.Hit && snapped.Point != null)
            {
                ++snapHits;
                double dx = snapped.Point[0] - loose.Point[0], dy = snapped.Point[1] - loose.Point[1], dz = snapped.Point[2] - loose.Point[2];
                snapAdopted.Add($"({uv.X:F4},{uv.Y:F4}) direct={direct?.Status.ToString() ?? "null"} " +
                    $"shift={Math.Sqrt(dx * dx + dy * dy + dz * dz):F6} face={snapped.FaceId}");
                return new SurfaceMappedSample(uv, new Vec3(snapped.Point[0], snapped.Point[1], snapped.Point[2]), snapped.FaceId);
            }
            ++snapMisses;
        }
        else ++looseMisses;
        return null;
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var model = ModelPath(args);
        Console.WriteLine($"model: {model}");
        if (!Bridge.LoadStep(model)) { Console.WriteLine("FAIL load STEP"); return 2; }
        var info = Bridge.GetShapeInfo();
        Console.WriteLine($"faces={info.Faces} solids={info.Solids} shells={info.Shells}");
        Check(info.Faces > 600, "Mold.stp loaded");

        const int heatingFace = 79, exitFace = 11;
        Check(Bridge.GetFaceType(heatingFace) == "Cylinder", $"face {heatingFace} is a Cylinder");
        Check(Bridge.GetFaceType(exitFace) == "BSplineSurface", $"face {exitFace} is a BSplineSurface");

        // ---- same working direction as the app: +Z, X=(1,0,0) ----
        var side = Bridge.CreateWorkingContext(0, 0, 1, 1, 0, 0);
        if (side == null) { Console.WriteLine("FAIL create working context"); return 2; }
        var frame = Frame(side);
        var sideDefinition = new WorkingSideDefinition(new WorkingSideId(side.Id), new ModelRevision(side.Revision), frame);
        int[] faces = { heatingFace };

        var loops = Bridge.GetSelectedRegionBoundary(faces, 0.05);
        Check(loops != null && loops.Length == 1 && loops[0].Closed, "selected region is a single closed loop");
        if (loops == null || loops.Length != 1) { Console.WriteLine("cannot continue without a region"); return 2; }
        var ring3D = new List<Vec3>();
        for (int i = 0; i + 2 < loops[0].Points.Length; i += 3)
            ring3D.Add(new Vec3(loops[0].Points[i], loops[0].Points[i + 1], loops[0].Points[i + 2]));
        var ring = ring3D.Select(frame.Project).ToList();
        if (RegionOps.SignedArea(ring) < 0) ring.Reverse();
        Console.WriteLine($"region: 3D points={ring3D.Count} z=[{ring3D.Min(p => p.Z):F2},{ring3D.Max(p => p.Z):F2}] area2D={RegionOps.SignedArea(ring):F1}");
        Check(ring3D.Max(p => p.Z) - ring3D.Min(p => p.Z) > 10, "region is curved along the working direction (>10 mm)");

        // ---- exit chain from the real shared CAD edge (app keeps both 2D and 3D) ----
        var edges = Bridge.GetSharedBoundaryEdges(exitFace, faces, 0.05);
        List<Vec2>? chain = null; List<Vec3>? chain3D = null;
        double bestLength = 0;
        if (edges != null)
            foreach (var flat in edges)
            {
                if (flat == null || flat.Length < 6) continue;
                var points = new List<Vec2>(); var points3D = new List<Vec3>();
                for (int i = 0; i + 2 < flat.Length; i += 3)
                {
                    var p3 = new Vec3(flat[i], flat[i + 1], flat[i + 2]);
                    var p = frame.Project(p3);
                    if (points.Count == 0 || points[^1].DistanceTo(p) > 1e-6) { points.Add(p); points3D.Add(p3); }
                }
                if (points.Count < 2) continue;
                double length = 0;
                for (int i = 1; i < points.Count; i++) length += points[i - 1].DistanceTo(points[i]);
                if (length > bestLength) { bestLength = length; chain = points; chain3D = points3D; }
            }
        Check(chain != null && chain3D != null && chain.Count == chain3D.Count && chain.Count >= 2,
            $"exit chain built from shared edge (points={chain?.Count})");
        if (chain == null || chain3D == null) { Console.WriteLine("cannot continue without an exit chain"); return 2; }

        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.Auto };
        var candidates = Serpentine.ComputeCandidates(ring, null, parameters, chain, PatternDirection.Auto);
        Console.WriteLine($"anchor candidates: {candidates.Count}");
        Check(candidates.Count >= 1, "at least one anchor pair fits the exit chain");
        if (candidates.Count < 1) { Console.WriteLine("cannot continue without anchors"); return 2; }
        var startAnchor = candidates[0].Start;
        var endAnchor = candidates[0].End;

        var anchored = Serpentine.GenerateAnchored(ring, null, parameters, chain, startAnchor, endAnchor, PatternDirection.Auto);
        Check(anchored.Succeeded, "GenerateAnchored succeeds");
        if (!anchored.Succeeded) { Console.WriteLine(anchored.Failure); return 2; }
        var pattern = anchored.Pattern;
        Console.WriteLine($"pattern: lanes={pattern.LaneCount} pieces={pattern.RoutePieces.Count} length={pattern.TotalLength:F1}");
        Check(pattern.RoutePieces.Count == 21, "user case generates 21 route pieces");

        HwndSource? source = null;
        try { source = new HwndSource(new HwndSourceParameters("phase1 curved repro") { Width = 640, Height = 480 }); } catch { }
        bool viewer = source != null && Bridge.InitViewer(source.Handle, 640, 480);
        Console.WriteLine($"offscreen viewer: {viewer}");
        Check(viewer, "offscreen viewer initialised");
        if (!viewer) { Bridge.ReleaseWorkingContext(side.Id); source?.Dispose(); return 2; }

        // ---- baseline: the old projected path on the same curved region ----
        int projectedFailures = 0;
        var projectedStages = new List<int>();
        for (int i = 0; i < pattern.RoutePieces.Count; i++)
        {
            var piece = pattern.RoutePieces[i];
            var flat = piece.Points.SelectMany(p => { var w = frame.Unproject(p); return new[] { w.X, w.Y, w.Z }; }).ToArray();
            int stageId = int.MinValue + i;
            var shown = Bridge.StageProjectedWorkingRoutePiece(side.Id, side.Revision, stageId, piece.Kind.ToString(),
                flat, faces, -frame.Normal.X, -frame.Normal.Y, -frame.Normal.Z);
            if (shown == null) ++projectedFailures; else projectedStages.Add(stageId);
        }
        Console.WriteLine($"old projected path: failures={projectedFailures}/{pattern.RoutePieces.Count}");
        Bridge.DiscardWorkingRouteGeometry(side.Id, side.Revision, projectedStages.ToArray(), Array.Empty<int>());

        // ---- Phase 1: map every piece by datum raycast and stage the 3D polyline directly ----
        Vec2? startDatum = startAnchor, endDatum = endAnchor;
        Vec3 start3D = PointOnChain3D(chain, chain3D, startAnchor);
        Vec3 end3D = PointOnChain3D(chain, chain3D, endAnchor);
        Console.WriteLine($"anchors: 2D {startAnchor}/{endAnchor} 3D {start3D}/{end3D}");

        var staged = new List<int>();
        var results = new List<RoutePieceResult>();
        int mapFailure = -1, stageFailure = -1, seamFailure = -1;
        for (int i = 0; i < pattern.RoutePieces.Count; i++)
        {
            var piece = pattern.RoutePieces[i];
            Vec2? anchorDatum = null; Vec3? anchor3D = null;
            if (piece.Kind == SerpentineRoutePieceKind.ExitPiece && piece.Points.Count > 0)
            {
                if (piece.Points[0] == startAnchor || piece.Points[^1] == startAnchor) { anchorDatum = startDatum; anchor3D = start3D; }
                else if (piece.Points[0] == endAnchor || piece.Points[^1] == endAnchor) { anchorDatum = endDatum; anchor3D = end3D; }
            }
            if (!SurfacePathMapper.TryMapRoute(piece.Points,
                    uv => MapDatum(sideDefinition, faces, uv, anchorDatum, anchor3D, exitFace),
                    out var samples, out string error))
            {
                Console.WriteLine($"  map failure piece {i} {piece.Kind}: {error}");
                mapFailure = i; break;
            }
            for (int k = 1; k < samples.Count; k++)
            {
                int previous = samples[k - 1].FaceId, current = samples[k].FaceId;
                if (previous < 0 || current < 0 || previous == current) continue;
                if (!Bridge.AreWorkingFacesAdjacent(side.Id, side.Revision, previous, current))
                { Console.WriteLine($"  seam failure piece {i}: {previous} -> {current}"); seamFailure = i; break; }
            }
            if (seamFailure >= 0) break;
            var xyz = new double[samples.Count * 3];
            for (int k = 0; k < samples.Count; k++)
            {
                xyz[k * 3] = samples[k].Point.X; xyz[k * 3 + 1] = samples[k].Point.Y; xyz[k * 3 + 2] = samples[k].Point.Z;
            }
            int stageId = int.MinValue + 100000 + i;
            var shown = Bridge.StageSurfaceMappedRoutePiece(side.Id, side.Revision, stageId, piece.Kind.ToString(), xyz, faces);
            if (shown == null)
            {
                Console.WriteLine($"  stage failure piece {i} {piece.Kind}: {Bridge.GetWorkingStageDiagnostic()}");
                stageFailure = i; break;
            }
            staged.Add(stageId); results.Add(shown);
        }
        Check(mapFailure < 0, "every route piece maps onto the selected cylinder face");
        Check(stageFailure < 0, "every mapped route piece stages as a 3D polyline");
        Check(seamFailure < 0, "no route piece jumps to a non-adjacent face");
        Check(staged.Count == pattern.RoutePieces.Count, "staged piece count matches the generated pattern");

        double worstConnection = 0; int worstIndex = -1;
        for (int i = 1; i < results.Count; i++)
        {
            double distance = Distance(results[i - 1].End, results[i].Start);
            if (distance > worstConnection) { worstConnection = distance; worstIndex = i; }
        }
        Console.WriteLine($"worst piece connection: {worstConnection:F6} mm at piece {worstIndex}");
        Check(worstConnection <= 0.01, "adjacent piece endpoints agree within 0.01 mm");

        // ---- 폴백 사용 계측: 21조각 사용자 사례 구간만 집계 ----
        Console.WriteLine($"mapping fallback [user case 21 pieces]: {Counters()}");
        foreach (var entry in directFailureStatuses) Console.WriteLine($"  direct-miss {entry}");
        foreach (var entry in snapAdopted) Console.WriteLine($"  snap-adopted {entry}");
        directHits = snapHits = snapMisses = looseMisses = anchorShortCircuits = 0;
        directFailureStatuses.Clear(); snapAdopted.Clear();

        // ---- accepted Phase 1 error: 2D spacing is not 3D surface spacing ----
        var lanes = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Select(piece => (Piece: piece,
                A: MapDatum(sideDefinition, faces, piece.Points[0], null, null, exitFace),
                B: MapDatum(sideDefinition, faces, piece.Points[^1], null, null, exitFace)))
            .Where(lane => lane.A.HasValue && lane.B.HasValue)
            .ToList();
        double worstRatio = 0;
        foreach (var lane in lanes)
        {
            double length2D = lane.Piece.Points[0].DistanceTo(lane.Piece.Points[^1]);
            double length3D = (lane.A!.Value.Point - lane.B!.Value.Point).Length;
            if (length2D > 1e-9) worstRatio = Math.Max(worstRatio, length3D / length2D);
        }
        Console.WriteLine($"HeatingRun lanes={lanes.Count} worst 3D/2D chord ratio={worstRatio:F4}");
        Check(worstRatio < 3.0, "mapped lane lengths stay within a plausible curvature factor");
        double worstAdjacent = 0;
        for (int i = 1; i < lanes.Count; i++)
        {
            var previous = (lanes[i - 1].A!.Value.Point + lanes[i - 1].B!.Value.Point) * 0.5;
            var current = (lanes[i].A!.Value.Point + lanes[i].B!.Value.Point) * 0.5;
            worstAdjacent = Math.Max(worstAdjacent, Math.Abs((current - previous).Length - parameters.Spacing));
        }
        Console.WriteLine($"adjacent-lane 3D midpoint spacing deviation from {parameters.Spacing} mm: up to {worstAdjacent:F2} mm");

        double total = results.Sum(r => r.Length);
        Console.WriteLine($"staged total length={total:F1} mm (pattern 2D={pattern.TotalLength:F1} mm)");

        // ---- 폴백 실패/성공 내역을 지우고 다른 곡면 조사 구간을 따로 집계 ----
        directHits = snapHits = snapMisses = looseMisses = anchorShortCircuits = 0;
        directFailureStatuses.Clear(); snapAdopted.Clear();

        Bridge.DiscardWorkingRouteGeometry(side.Id, side.Revision, staged.ToArray(), Array.Empty<int>());
        Survey(sideDefinition, info.Faces);
        Console.WriteLine($"mapping fallback [survey of other patches]: {Counters()}");
        foreach (var entry in directFailureStatuses.Take(20)) Console.WriteLine($"  survey direct-miss {entry}");
        foreach (var entry in snapAdopted.Take(20)) Console.WriteLine($"  survey snap-adopted {entry}");
        Bridge.ReleaseWorkingContext(side.Id);
        source?.Dispose();

        Console.WriteLine();
        foreach (var failure in failures) Console.WriteLine("FAILED: " + failure);
        Console.WriteLine($"checks passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}



