using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 제외영역 + 인출면 고정 경로의 관리측 배선 회귀 검사.
/// 평면 상면(30x16)에 두 레인을 절단하는 제외를 넣고, 인출면 앵커를 고른 뒤
/// 단일 사행 경로가 생성·매핑되고 리드 길이가 계산되는지 확인한다.
/// </summary>
static class ExclusionExitChecks
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
        // layers.stp: z=20 상면(40x20)을 30x16 영역으로 사용한다.
        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for exclusion+exit checks");

        // 이전 검사에서 -Z로 바뀐 작업 방향을 +Z로 되돌리고 의존 상태를 초기화한다.
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1)),
            "reapply +Z working side before exclusion+exit checks");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for exclusion+exit checks");
        if (topFace < 0 || side == null) return;

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(area, "ExclusionExitCheck");
        areaType.GetProperty("Frame")!.SetValue(area, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(area, 4201);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(topFace);
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area)!).Add(new()
            { new(-15, -8), new(15, -8), new(15, 8), new(-15, 8) });
        ((IList)areaType.GetProperty("Exclusions")!.GetValue(area)!).Add((0d, -4d, 6d, 4d));
        areaType.GetProperty("Direction")!.SetValue(area, PatternDirection.FrameX);
        areaType.GetProperty("ExitFaceId")!.SetValue(area, topFace);
        areaType.GetProperty("ExitFaceRevision")!.SetValue(area, Bridge.GetModelRevision());
        var chain = (List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area)!;
        chain.Add(new(15, -8));
        chain.Add(new(15, 8));
        var chainStart = Bridge.QueryWorkingSurface(side.Id.Value, side.Revision.Value, 15, -8);
        var chainEnd = Bridge.QueryWorkingSurface(side.Id.Value, side.Revision.Value, 15, 8);
        Check(chainStart.Status == WorkingQueryStatus.Hit && chainEnd.Status == WorkingQueryStatus.Hit,
            "exit chain endpoints land on the working surface");
        if (chainStart.Point == null || chainEnd.Point == null)
        {
            Invoke(window, "ClearAllAreas", false);
            return;
        }
        var chain3D = (List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(area)!;
        chain3D.Add(new Vec3(chainStart.Point[0], chainStart.Point[1], chainStart.Point[2]));
        chain3D.Add(new Vec3(chainEnd.Point[0], chainEnd.Point[1], chainEnd.Point[2]));
        ((List<int>)areaType.GetProperty("ExitChainFaceIds")!.GetValue(area)!).Add(topFace);

        ((IList)Field<object>(window, "_areas")).Add(area);
        Invoke(window, "ActivateArea", area);
        var marginBox = (TextBox)window.FindName("MarginBox");
        var spacingBox = (TextBox)window.FindName("SpacingBox");
        marginBox.Text = "2";
        spacingBox.Text = "5";
        Invoke(window, "RecomputeAnchorCandidates", area);

        var candidates = (List<ExcludedAnchorCandidate>)areaType.GetProperty("ExcludedCandidates")!.GetValue(area)!;
        Check(candidates.Count > 0, "exclusion+exit candidate plan is computed for the planar face");
        if (candidates.Count == 0)
        {
            Invoke(window, "ClearAllAreas", false);
            marginBox.Text = "5";
            spacingBox.Text = "50";
            return;
        }

        var plan = candidates[0];
        areaType.GetProperty("StartAnchor")!.SetValue(area, plan.Start);
        areaType.GetProperty("EndAnchor")!.SetValue(area, plan.End);
        areaType.GetProperty("ExcludedPlan")!.SetValue(area, plan);

        Invoke(window, "GenerateMultiFace");
        var pattern = areaType.GetProperty("Pattern")!.GetValue(area) as SerpentineResult;
        Check(pattern != null, "exclusion+exit generation produces a route plan");
        if (pattern == null)
        {
            Invoke(window, "ClearAllAreas", false);
            marginBox.Text = "5";
            spacingBox.Text = "50";
            return;
        }

        Check(areaType.GetProperty("ExclusionPieces")!.GetValue(area) == null,
            "anchored exclusion generation uses the single route-piece plan");
        Check(pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun) == 4,
            "both split lanes contribute exactly two heating runs");
        Check(pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector) == 3,
            "the four heating runs are joined by three turn connectors");
        Check(pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.ExitPiece) == 2,
            "start and end leads are emitted as exit pieces");
        Check(plan.Start.DistanceTo(pattern.Path[0]) < 1e-9 && plan.End.DistanceTo(pattern.Path[^1]) < 1e-9,
            "generated path endpoints match the selected exit-face anchors");
        bool runsOutside = pattern.RoutePieces
            .Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .SelectMany(piece => piece.Points)
            .All(p => !(p.X > 0.001 && p.X < 5.999 && p.Y > -3.999 && p.Y < 3.999));
        Check(runsOutside, "every heating run stays outside the excluded rectangle");

        var projected = (List<RoutePieceResult>)areaType.GetProperty("ProjectedPieces")!.GetValue(area)!;
        Check(projected.Count == pattern.RoutePieces.Count && projected.All(piece => piece != null),
            "every route piece maps onto the working surface");
        var leads = (double[])areaType.GetProperty("LeadLengths")!.GetValue(area)!;
        Check(leads.Length > 0 && Math.Abs(leads.Sum() - 4) < 0.1,
            "exit-face leads sum to the two margin-sized gaps");
        Check((bool)areaType.GetProperty("PatternDisplayed")!.GetValue(area)!,
            "generated anchored exclusion plan is displayed");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("리드"),
            "status reports the number of exit leads");

        Invoke(window, "ClearAllAreas", false);
        marginBox.Text = "5";
        spacingBox.Text = "50";
        Console.WriteLine($"{count} exclusion+exit checks passed.");
    }
}
