using System.Collections;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 분할 회로의 파생 인출점 이동값이 회로 정본에 반영되는지 확인한다.
/// WPF 창 없이 앱 상태와 Core 기하만 사용한다.
/// </summary>
static class CircuitExitOverrideChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    public static void Run(MainWindow window)
    {
        var assembly = typeof(MainWindow).Assembly;
        var areaType = assembly.GetType("HeatingMold.App.WorkingArea")!;
        var overrideType = assembly.GetType("HeatingMold.App.CircuitExitOverride")!;
        var refresh = typeof(MainWindow).GetMethod(
            "TryRefreshCircuits", BindingFlags.Static | BindingFlags.NonPublic)!;

        var outer = new List<Vec2>
        {
            new(0, 0), new(100, 0), new(100, 60), new(0, 60)
        };
        var chain = new List<Vec2> { new(100, 0), new(100, 60) };
        var parameters = new PatternParams { Margin = 5, Spacing = 10 };
        var candidate = Serpentine.ComputeCandidates(
            outer, null, parameters, chain, PatternDirection.FrameX).Single();
        AnchoredResult generated = Serpentine.GenerateAnchored(
            outer, null, parameters, chain, candidate.Start, candidate.End, PatternDirection.FrameX);
        Check(generated.Succeeded, "circuit endpoint fixture route is generated");

        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Pattern")!.SetValue(area, generated.Pattern);
        areaType.GetProperty("ExitFaceId")!.SetValue(area, 1);
        ((List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area)!).AddRange(chain);
        ((IList)areaType.GetProperty("Region")!.GetValue(area)!).Add(outer);
        areaType.GetProperty("RegionBoundary")!.SetValue(area, new ExactRegionContainment(
            new MockExactRegionBoundary(outer.ToArray())));
        int turns = generated.Pattern!.RoutePieces.Count(
            piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector);
        var enabled = new List<bool>();
        for (int i = 0; i < turns; i++) enabled.Add(i != 1);
        areaType.GetProperty("TurnEnabled")!.SetValue(area, enabled);

        object?[] refreshArgs = { area, null };
        Check((bool)refresh.Invoke(null, refreshArgs)!,
            "split circuits refresh with the generated route");
        var pieces = (List<List<int>>)areaType.GetProperty("CircuitPieceIndices")!.GetValue(area)!;
        var circuits = (List<CircuitRoute>)areaType.GetProperty("Circuits")!.GetValue(area)!;
        Check(circuits.Count == 2 && pieces.Count == 2,
            "disabled right U-turn creates two circuit routes");

        // 분할 회로의 파생 인출점도 발열경로 편집에서 고정된다 — 전용 버튼으로만 변경한다.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var activeField = typeof(MainWindow).GetField("_activeArea", flags)!;
        object previousActive = activeField.GetValue(window);
        activeField.SetValue(window, area);
        var groups = typeof(MainWindow).GetMethod("BuildVertexGroups", flags)!.Invoke(window, new[] { area })!;
        typeof(MainWindow).GetField("_vertexGroups", flags)!.SetValue(window, groups);
        var anchors = (IDictionary)typeof(MainWindow).GetField("_vertexCircuitAnchors", flags)!.GetValue(window)!;
        Check(anchors.Count > 0, "split circuits expose derived endpoint groups");
        int anchorGroup = (int)anchors.Keys.Cast<object>().First();
        typeof(MainWindow).GetField("_vertexDragGroup", flags)!.SetValue(window, anchorGroup);
        try
        {
            typeof(MainWindow).GetMethod("CommitVertex", flags)!
                .Invoke(window, new object[] { new Vec2(100, 0) });
        }
        finally
        {
            typeof(MainWindow).GetField("_vertexDragGroup", flags)!.SetValue(window, -1);
            typeof(MainWindow).GetField("_vertexGroups", flags)!.SetValue(window, new List<RouteVertexGroup>());
            activeField.SetValue(window, previousActive);
        }
        Check(ReferenceEquals(areaType.GetProperty("Pattern")!.GetValue(area), generated.Pattern),
            "vertex edit keeps a split-circuit derived endpoint fixed");

        int pieceIndex = pieces[0][^1];
        Vec2 original = circuits[0].EndExit.Point;
        Vec2 moved = new(100, original.Y - 2);
        var overrides = (IList)areaType.GetProperty("CircuitExitOverrides")!.GetValue(area)!;
        overrides.Add(Activator.CreateInstance(overrideType,
            new object[] { pieceIndex, CircuitExitRole.End, moved })!);
        Check((bool)refresh.Invoke(null, refreshArgs)!,
            "circuit endpoint override refresh succeeds");
        circuits = (List<CircuitRoute>)areaType.GetProperty("Circuits")!.GetValue(area)!;

        Check(circuits[0].EndExit.Point.DistanceTo(moved) <= 1e-6,
            "overridden circuit end lands on the requested exit point");
        Check(circuits[0].Pieces[^1].Points[^1].DistanceTo(moved) <= 1e-6,
            "overridden circuit path ends at the requested exit point");
        Check(circuits[1].EndExit.Point.DistanceTo(generated.Pattern.EndExit.Point) <= 1e-6,
            "the other circuit endpoint is unchanged");

        Console.WriteLine($"{count} circuit endpoint override checks passed.");
    }
}
