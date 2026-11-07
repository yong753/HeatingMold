using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 경계 Edge 위 두 인출점을 직접 고르는 새 생성 경로 검사.
/// 같은 인출면을 요구하지 않고, 짧은 두 점을 골라도 첫/마지막 레인만 비스듬해지며
/// 조각 수가 늘지 않는지 실제 CAD 원본 경계로 확인한다.
/// </summary>
static class SelectedExitChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    static void Cleanup(MainWindow window, TextBox marginBox, TextBox spacingBox, string fixtureDir)
    {
        Invoke(window, "ClearAllAreas", false);
        marginBox.Text = "5";
        spacingBox.Text = "50";
        Bridge.LoadStep(Path.Combine(fixtureDir, "layers.stp"));
        Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1));
    }

    public static void Run(MainWindow window, string fixtureDir)
    {
        Check(Bridge.LoadStep(Path.Combine(fixtureDir, "exitSides.stp")),
            "load solid fixture for direct exit-point checks");
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1)),
            "confirm +Z side for direct exit-point checks");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed side is available for direct exit-point checks");
        if (side == null) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }

        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for direct exit-point checks");
        if (topFace < 0) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }

        var loops = Bridge.GetSelectedRegionBoundary(new[] { topFace }, 0.05);
        Check(loops != null && loops.Length == 1 && loops[0].Closed &&
              loops[0].EdgeReferences != null && loops[0].EdgeReferences.Length > 0,
            "region boundary exposes original CAD references");
        if (loops == null || loops.Length != 1) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(area, "SelectedExitCheck");
        areaType.GetProperty("Frame")!.SetValue(area, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(area, 4401);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(topFace);

        var regionBoundary = new ExactRegionContainment(
            new CadRegionBoundary2D(side.Id.Value, side.Revision.Value, loops[0].EdgeReferences));
        areaType.GetProperty("RegionBoundary")!.SetValue(area, regionBoundary);
        var exitBoundary = new CadExitBoundary2D(side.Id.Value, side.Revision.Value, loops[0].EdgeReferences);
        areaType.GetProperty("RegionExitBoundary")!.SetValue(area, exitBoundary);
        areaType.GetProperty("ExitBoundary")!.SetValue(area, exitBoundary);
        var refs = (List<ExitEdgeReference>)areaType.GetProperty("ExitEdgeRefs")!.GetValue(area)!;
        foreach (var reference in loops[0].EdgeReferences)
            refs.Add(CadExitBoundary2D.CopyReference(reference));

        var chain = (List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area)!;
        var chain3D = (List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(area)!;
        var ring = new List<Vec2>();
        for (int k = 0; k + 2 < loops[0].Points.Length; k += 3)
        {
            var point3 = new Vec3(loops[0].Points[k], loops[0].Points[k + 1], loops[0].Points[k + 2]);
            var projectedPoint = side.Frame.Project(point3);
            chain.Add(projectedPoint);
            chain3D.Add(point3);
            ring.Add(projectedPoint);
        }
        if (RegionOps.SignedArea(ring) < 0) ring.Reverse();
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area)!).Add(ring);
        areaType.GetProperty("Direction")!.SetValue(area, PatternDirection.FrameX);

        double minX = ring.Min(p => p.X), maxX = ring.Max(p => p.X);
        double minY = ring.Min(p => p.Y), maxY = ring.Max(p => p.Y);
        // 첫 레인은 왼쪽→오른쪽, 마지막 레인도 왼쪽→오른쪽(홀수 3레인)이므로
        // 시작점은 왼쪽 Edge, 종료점은 오른쪽 Edge에 둔다. 레인 y=-10/0/10과
        // 다른 y=-5/+5를 골라 첫/마지막 레인을 비스듬하게 만든다.
        var startResolution = exitBoundary.Project(new Vec2(minX, (minY + maxY) / 2 - 5), 1.0);
        var endResolution = exitBoundary.Project(new Vec2(maxX, (minY + maxY) / 2 + 5), 1.0);
        Check(startResolution.Succeeded && endResolution.Succeeded &&
              startResolution.Point!.Value.Binding.HasValue && endResolution.Point!.Value.Binding.HasValue,
            "two short exit points bind to different original boundary edges");
        if (!startResolution.Succeeded || !endResolution.Succeeded) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }
        areaType.GetProperty("SelectedStartExit")!.SetValue(area, startResolution.Point);
        areaType.GetProperty("SelectedEndExit")!.SetValue(area, endResolution.Point);

        ((IList)Field<object>(window, "_areas")).Add(area);
        Invoke(window, "ActivateArea", area);
        var marginBox = (TextBox)window.FindName("MarginBox");
        var spacingBox = (TextBox)window.FindName("SpacingBox");
        marginBox.Text = "5";
        spacingBox.Text = "10";
        areaType.GetProperty("MarginText")!.SetValue(area, "5");
        areaType.GetProperty("SpacingText")!.SetValue(area, "10");

        Invoke(window, "GenerateMultiFace");
        var pattern = areaType.GetProperty("Pattern")!.GetValue(area) as SerpentineResult;
        if (pattern == null)
            Console.WriteLine("GenerateMultiFace status: " + ((TextBlock)window.FindName("StatusText")).Text);
        Check(pattern != null, "direct exit-point generation produces a route plan");
        if (pattern == null) { Cleanup(window, marginBox, spacingBox, fixtureDir); return; }

        Check(pattern.StartExit != null && pattern.EndExit != null,
            "generated pattern records both direct exit endpoints");
        var selectedStart = startResolution.Point!.Value;
        var selectedEnd = endResolution.Point!.Value;
        Check(pattern.StartExit!.Point.DistanceTo(selectedStart.Point) < 1e-6 &&
              pattern.EndExit!.Point.DistanceTo(selectedEnd.Point) < 1e-6,
            "generated endpoints equal the two directly selected boundary points");
        Check(pattern.StartExit.Binding?.EdgeKey == selectedStart.Binding?.EdgeKey &&
              pattern.EndExit.Binding?.EdgeKey == selectedEnd.Binding?.EdgeKey,
            "generated endpoints retain the original edge identities");
        Check(pattern.StartExit.Binding?.EdgeKey != pattern.EndExit.Binding?.EdgeKey,
            "start and end points may live on different original edges");
        Check(pattern.LaneCount == 3 && !pattern.LaneCountAdjusted,
            "odd three-lane layout is kept without forced even-lane reduction");
        int heatingRuns = pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun);
        int turns = pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector);
        Check(heatingRuns == 3 && turns == 2,
            "endpoint replacement does not add heating runs or turn connectors");
        Check(pattern.RoutePieces[0].Points[0].DistanceTo(selectedStart.Point) < 1e-6 &&
              pattern.RoutePieces[^1].Points[^1].DistanceTo(selectedEnd.Point) < 1e-6,
            "first and last lane endpoints are the selected points");
        Check(pattern.RoutePieces[0].Points[0].Y > pattern.RoutePieces[0].Points[^1].Y &&
              pattern.RoutePieces[^1].Points[0].Y > pattern.RoutePieces[^1].Points[^1].Y,
            "first and last lanes are slanted rather than axis-aligned");

        var projected = (List<RoutePieceResult>)areaType.GetProperty("ProjectedPieces")!.GetValue(area)!;
        Check(projected.Count == pattern.RoutePieces.Count && projected.All(piece => piece != null),
            "every direct-exit route piece maps onto the working surface");
        Check((bool)areaType.GetProperty("PatternDisplayed")!.GetValue(area)!,
            "direct-exit route is displayed after commit");

        Cleanup(window, marginBox, spacingBox, fixtureDir);
        Console.WriteLine($"{count} direct exit-point checks passed.");
    }
}
