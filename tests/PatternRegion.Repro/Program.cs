using System.IO;
using System.Windows.Interop;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

// Repro for: "occt projected route-piece validation/staging failed at piece 87"
// Region = faces {18, 74, 597, 598} of resource/Mold.stp, default +Z working side,
// Serpentine margin 5 / spacing 10. See README.md for the analysis.
static class Program
{
    static int passed, failed;

    static void Check(bool value, string label)
    {
        if (value) { Console.WriteLine("PASS " + label); ++passed; }
        else { Console.WriteLine("FAIL " + label); ++failed; }
    }

    static ProjectionFrame Frame(WorkingContext c) => new(new(c.Frame[0], c.Frame[1], c.Frame[2]),
        new(c.Frame[9], c.Frame[10], c.Frame[11]), new(c.Frame[3], c.Frame[4], c.Frame[5]));

    static double Dist(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    static double[] Flatten(ProjectionFrame f, IEnumerable<Vec2> points) =>
        points.SelectMany(p => { var w = f.Unproject(p); return new[] { w.X, w.Y, w.Z }; }).ToArray();

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

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var model = ModelPath(args);
        Console.WriteLine($"model: {model}");

        if (!Bridge.LoadStep(model)) { Console.WriteLine("FAIL load STEP"); return 2; }
        var info = Bridge.GetShapeInfo();
        Console.WriteLine($"faces={info.Faces} solids={info.Solids} shells={info.Shells}");
        Check(info.Faces > 600, "Mold.stp loaded (664 faces)");

        // ---- same setup as MainWindow.ApplyWorkingSide / BuildSelectedRegion ----
        var side = Bridge.CreateWorkingContext(0, 0, 1, 1, 0, 0);
        if (side == null) { Console.WriteLine("FAIL create working context"); return 2; }
        var frame = Frame(side);
        int[] faces = { 18, 74, 597, 598 };

        // ---- face classification: the steep wall is not built from planes only ----
        foreach (var f in faces) Console.WriteLine($"face {f}: {Bridge.GetFaceType(f)}");
        const int cylinder = 598;
        double cu0 = double.MaxValue, cu1 = double.MinValue, cv0 = double.MaxValue, cv1 = double.MinValue;
        foreach (var w in Bridge.GetFaceUvBoundary(cylinder))
            for (int i = 0; i + 1 < w.Points.Length; i += 2)
            {
                cu0 = Math.Min(cu0, w.Points[i]); cu1 = Math.Max(cu1, w.Points[i]);
                cv0 = Math.Min(cv0, w.Points[i + 1]); cv1 = Math.Max(cv1, w.Points[i + 1]);
            }
        double uc = (cu0 + cu1) / 2, vc = (cv0 + cv1) / 2, du = (cu1 - cu0) * 1e-3;
        var q0 = Bridge.SampleUvTo3D(cylinder, uc, vc);
        var qu = Bridge.SampleUvTo3D(cylinder, uc + du, vc);
        double radius = Dist(q0, qu) / du;            // |dP/du| = R for Geom_CylindricalSurface
        double arcSpan = cu1 - cu0;                   // radians
        double sag10 = 10 * 10 / (8 * radius);        // sagitta over a 10 mm chord
        Console.WriteLine($"face 598: R={radius:F0} mm, angular span={arcSpan * 180 / Math.PI:F2} deg " +
            $"(patch width {radius * arcSpan:F0} mm), flatness over 10 mm={sag10:F4} mm");
        Check(Bridge.GetFaceType(cylinder) == "Cylinder" && radius > 1000,
            "face 598 is a huge-radius cylinder, i.e. visually flat but not a plane");
        Check(sag10 < 0.01,
            "over a 10 mm connector the cylinder stays within microns of a plane (below the 0.01 mm sewing tolerance)");

        var loops = Bridge.GetSelectedRegionBoundary(faces, 0.05);
        Check(loops != null && loops.Length == 1 && loops[0].Closed, "selected region is a single closed loop");
        var ring3D = new List<Vec3>();
        for (int i = 0; i + 2 < loops![0].Points.Length; i += 3)
            ring3D.Add(new Vec3(loops[0].Points[i], loops[0].Points[i + 1], loops[0].Points[i + 2]));
        var ring = ring3D.Select(frame.Project).ToList();
        double zSpan = ring3D.Max(p => p.Z) - ring3D.Min(p => p.Z);
        Console.WriteLine($"boundary points={ring3D.Count} zSpan={zSpan:F3} frameZ={frame.Origin.Z:F3}");
        if (RegionOps.SignedArea(ring) < 0) ring.Reverse();

        var pattern = Serpentine.Generate(ring, null, new PatternParams { Margin = 5, Spacing = 10 },
            constrainConnectors: true);
        Console.WriteLine($"lanes={pattern.LaneCount} pieces={pattern.RoutePieces.Count} length={pattern.TotalLength:F1}");

        // A single global -Z projection is only valid while every piece stays projectable; the
        // region boundary is not planar, so this is exactly the assumption under test.
        Check(zSpan > 200, "region boundary is strongly non-planar (222 mm), projected onto one datum plane");
        Check(pattern.RoutePieces.Count == 133, "serpentine produces 133 route pieces");

        // ---- Phase 1: goal. Every generated piece must survive the bridge's projection validation ----
        var failures = new List<(int Index, SerpentineRoutePiece Piece, string Status)>();
        for (int i = 0; i < pattern.RoutePieces.Count; i++)
        {
            var piece = pattern.RoutePieces[i];
            var d = Bridge.ProjectPathWireOntoSelectedFaces(faces, Flatten(frame, piece.Points),
                -frame.Normal.X, -frame.Normal.Y, -frame.Normal.Z, 0);
            bool ok = d != null && d[0] == 1;
            if (!ok) failures.Add((i, piece, d == null ? "null" : $"status={d[4]:F0} wires={d[3]:F0}"));
        }
        Console.WriteLine($"projection failures: {(failures.Count == 0 ? "none" : string.Join(", ", failures.Select(f => f.Index)))}");
        foreach (var f in failures)
            Console.WriteLine($"  piece {f.Index} {f.Piece.Kind} lane={f.Piece.LaneIndex} points={f.Piece.Points.Count} {f.Status}");
        Check(failures.Count == 0, "every route piece projects onto the selected faces");

        // ---- Phase 2: reproduce the reported error through the real staging call ----
        var firstFailure = failures.Count > 0 ? failures[0].Index : -1;
        Check(firstFailure == 87, "first failing piece is 87 (the reported symptom)");
        Check(failures.Count > 0 && failures.All(f => f.Piece.Kind == SerpentineRoutePieceKind.TurnConnector &&
            f.Piece.Points.Count == 2), "every failure is a 2-point turn connector (straight fallback)");

        HwndSource? source = null;
        try { source = new HwndSource(new HwndSourceParameters("pattern repro") { Width = 640, Height = 480 }); } catch { }
        bool viewer = source != null && Bridge.InitViewer(source.Handle, 640, 480);
        Console.WriteLine($"offscreen viewer: {viewer}");
        if (viewer)
        {
            int stageBase = int.MinValue;
            var staged = new List<int>();
            int stagedFailure = -1;
            for (int i = 0; i < pattern.RoutePieces.Count; i++)
            {
                var piece = pattern.RoutePieces[i];
                int stageId = stageBase + i;
                var shown = Bridge.StageProjectedWorkingRoutePiece(side.Id, side.Revision, stageId,
                    piece.Kind.ToString(), Flatten(frame, piece.Points), faces,
                    -frame.Normal.X, -frame.Normal.Y, -frame.Normal.Z);
                if (shown == null) { stagedFailure = i; break; }
                staged.Add(stageId);
            }
            Check(stagedFailure == 87, "StageProjectedWorkingRoutePiece fails at piece 87 (exact reported error)");
            Bridge.DiscardWorkingRouteGeometry(side.Id, side.Revision, staged.ToArray(), Array.Empty<int>());
        }

        // ---- Phase 3: characterization of the root cause ----
        // Face 598 is a cylinder (R = 4303 mm) whose axis a lies almost exactly in the plane swept
        // by a Y-parallel segment along -Z. The sweep plane normal is n = normalize(d x -Z) =
        // (-1, s, 0)/|..| for d = (s,1,0), and a.n = (0.0095 - 0.8231*s)/sqrt(1+s^2), which
        // vanishes at s = a_x/|a_y| = 0.01154. The generated connector slope matches it.
        var p87 = pattern.RoutePieces[87].Points;
        var a = frame.Unproject(p87[0]);
        var b = frame.Unproject(p87[^1]);
        double slope = (b.X - a.X) / (b.Y - a.Y);
        Console.WriteLine($"piece87 a=({a.X:F3},{a.Y:F3},{a.Z:F3}) b=({b.X:F3},{b.Y:F3},{b.Z:F3}) dx={b.X - a.X:F3} dy={b.Y - a.Y:F3} dx/dy={slope:F5} ({Math.Atan(slope) * 180 / Math.PI:F3} deg)");
        Console.WriteLine($"piece87 exact a=({a.X:R},{a.Y:R},{a.Z:R}) b=({b.X:R},{b.Y:R},{b.Z:R})");
        Check(Math.Abs(slope - 0.0095 / 0.8231) < 0.0005,
            "connector slope equals the degenerate slope a_x/|a_y| = 0.01154 (sweep plane parallel to the cylinder axis)");

        string Diag(int[] subset, double[][] xyz, double dx = 0, double dy = 0, double dz = -1)
        {
            var d = Bridge.ProjectPathWireOntoSelectedFaces(subset, xyz.SelectMany(v => v).ToArray(), dx, dy, dz, 0);
            return d == null ? "null" : $"ok={d[0]:F0} len={d[1]:F2} wires={d[3]:F0} status={d[4]:F0}";
        }
        double[][] Segment(double x0, double x1, double y0, double y1) =>
            new[] { new[] { x0, y0, frame.Origin.Z }, new[] { x1, y1, frame.Origin.Z } };

        var exact = Segment(a.X, b.X, a.Y, b.Y);
        Console.WriteLine("exact piece87 2D segment   : " + Diag(faces, exact));
        Check(Diag(faces, exact) == "ok=0 len=0.00 wires=0 status=4", "exact segment produces zero projected wires (status 4)");

        var vertical = Segment(a.X, a.X, a.Y, b.Y);
        Console.WriteLine("same segment, dx=0 (exact) : " + Diag(faces, vertical));
        Check(Diag(faces, vertical).StartsWith("ok=1"), "exactly Y-parallel segment projects (a.n = 9.5e-3, sweep plane not axis-parallel)");

        var wider = Segment(a.X, a.X + 0.120, a.Y, b.Y);
        Console.WriteLine("same segment, dx=0.120     : " + Diag(faces, wider));
        Check(Diag(faces, wider).StartsWith("ok=1"), "0.005 mm more X drift projects again -> the zero crossing is what breaks it");

        var shifted = new[] { 400.0, 440.0, 480.0 }
            .Select(x0 => Diag(new[] { 598 }, Segment(x0, x0 + 0.116, 300, 310))).ToArray();
        Console.WriteLine("same slope at x0=400/440/480: " + string.Join(" | ", shifted));
        Check(shifted.All(s => s.Contains("status=4")), "same slope fails anywhere on the face -> direction effect, not a trim-edge effect");

        var lengths = new[] { 1.0, 5.0, 40.0 }
            .Select(l => Diag(new[] { 598 }, Segment(498.481, 498.481 + 0.0116 * l, 223.24, 223.24 + l))).ToArray();
        Console.WriteLine("same slope, len 1/5/40 mm  : " + string.Join(" | ", lengths));
        Check(lengths.All(s => s.Contains("status=4")), "same slope fails at every length -> not a tolerance-on-length effect");

        Console.WriteLine("tilted direction (-.01,-.01,-1): " + Diag(faces, exact, -0.01, -0.01, -1));
        Check(Diag(faces, exact, -0.01, -0.01, -1).StartsWith("ok=1"), "tilting the projection direction removes the degeneracy");

        Console.WriteLine("face subset {598} only      : " + Diag(new[] { 598 }, exact));
        Check(Diag(new[] { 598 }, exact).StartsWith("ok=0"), "cylinder face 598 alone reproduces the failure");

        Bridge.ReleaseWorkingContext(side.Id);
        source?.Dispose();

        Console.WriteLine();
        Console.WriteLine($"checks passed={passed} failed={failed}");
        if (failures.Count > 0)
        {
            Console.WriteLine("KNOWN DEFECT: the -Z projection of turn connectors over the 4.3 m radius cylinder (face 598)");
            Console.WriteLine("degenerates; staging aborts at piece 87. See README.md.");
        }
        return failed == 0 ? 0 : 1;
    }
}
