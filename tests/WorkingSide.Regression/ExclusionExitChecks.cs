using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 제외영역 + 인출면 고정 경로의 관리측 배선 회귀 검사.
/// layers.stp는 옆면이 없는 나대지 시트라 원본 edge 참조를 가진 인출 후보가
/// 존재하지 않으므로, 옆면이 있는 상자(exitSides.stp)로 교체해 검사하고
/// 끝나면 원래 모델로 복원한다. 60x40 상면을 실제 원본 경계로 영역을 잡고,
/// 실제 우측 원본 모서리를 인출면으로 선택한 뒤 단일 사행 경로가
/// 생성·매핑되고 길이가 계산되는지 확인한다.
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
    static object StaticInvoke(Type type, string name, object[] args) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    static void Cleanup(MainWindow window, TextBox marginBox, TextBox spacingBox, string fixtureDir)
    {
        Invoke(window, "ClearAllAreas", false);
        marginBox.Text = "5";
        spacingBox.Text = "50";
        // 후속 검사 그룹이 기대하는 원래 모델과 작업 방향으로 복원한다.
        Bridge.LoadStep(Path.Combine(fixtureDir, "layers.stp"));
        Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1));
    }

    public static void Run(MainWindow window, string fixtureDir)
    {
        // 옆면이 있는 상자(60x40x20): z=20 상면의 네 모서리마다 인접 면이 있어
        // 원본 edge 참조가 채워진 실제 인출 측면 후보가 존재한다.
        Check(Bridge.LoadStep(Path.Combine(fixtureDir, "exitSides.stp")),
            "load solid exit-side fixture for exclusion+exit checks");
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1)),
            "confirm +Z working side on the solid exit-side fixture");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for exclusion+exit checks");
        if (side == null) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }

        int topFace = -1;
        for (int id = 0; ; ++id)
        {
            var probe = Bridge.GetFaceNormal(id);
            if (probe == null) break;
            if (probe[2] > 0.9 && Math.Abs(probe[5] - 20) < 1e-6) { topFace = id; break; }
        }
        Check(topFace >= 0, "planar top face exists for exclusion+exit checks");
        if (topFace < 0) { Cleanup(window, (TextBox)window.FindName("MarginBox"), (TextBox)window.FindName("SpacingBox"), fixtureDir); return; }

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        var marginBox = (TextBox)window.FindName("MarginBox");
        var spacingBox = (TextBox)window.FindName("SpacingBox");

        // 네이티브 인출 측면 후보에서 실제 우측 모서리를 고른다. 인접 면이 있고 원본
        // edge 참조가 채워진 후보여야 엄격한 경계 질의가 가능하다.
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
        if (rightSide == null || rightSide.EdgeReferences == null || rightSide.EdgeReferences.Length == 0)
            Console.WriteLine(Bridge.GetExitSideDiagnostic());
        Check(rightSide != null && rightSide.EdgeReferences != null && rightSide.EdgeReferences.Length > 0,
            "native right exit side carries original edge references for the planar face");
        if (rightSide == null) { Cleanup(window, marginBox, spacingBox, fixtureDir); return; }

        areaType.GetProperty("Name")!.SetValue(area, "ExclusionExitCheck");
        areaType.GetProperty("Frame")!.SetValue(area, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(area, 4201);
        ((HashSet<int>)areaType.GetProperty("FaceIdSet")!.GetValue(area)!).Add(topFace);

        // 영역은 선택 면의 실제 원본 통합 경계(60x40)를 작업평면에 투영해 구성한다.
        var loops = Bridge.GetSelectedRegionBoundary(new[] { topFace }, 0.05);
        Check(loops != null && loops.Length == 1 && loops[0].Closed && loops[0].Points != null,
            "planar face exposes one closed region boundary loop");
        var region = (List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(area)!;
        var ring = new List<Vec2>();
        for (int k = 0; k + 2 < loops[0].Points.Length; k += 3)
            ring.Add(side.Frame.Project(new Vec3(loops[0].Points[k], loops[0].Points[k + 1], loops[0].Points[k + 2])));
        if (RegionOps.SignedArea(ring) < 0) ring.Reverse();
        region.Add(ring);

        // 제외 사각형은 프레임 좌표: u=-50..-44(세계 x 84..90), v=-10..-8.
        // spacing 6 → 레인 y=-15,-9,-3,3,9,15 중 y=-9 하나를 분할한다.
        ((IList)areaType.GetProperty("Exclusions")!.GetValue(area)!).Add((-50d, -10d, -44d, -8d));
        areaType.GetProperty("Direction")!.SetValue(area, PatternDirection.FrameX);
        areaType.GetProperty("ExitFaceId")!.SetValue(area, rightSide.FaceId);
        areaType.GetProperty("ExitFaceRevision")!.SetValue(area, Bridge.GetModelRevision());

        // 인출 체인은 네이티브 후보의 원본 점에서 온다. 표시용 중복점 제거나
        // 구간 remap을 하지 않으므로 원본 참조의 StartIndex/EndIndex와 1:1로 대응한다.
        var chain = (List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(area)!;
        var chain3D = (List<Vec3>)areaType.GetProperty("ExitChain3D")!.GetValue(area)!;
        var chainFaceIds = (List<int>)areaType.GetProperty("ExitChainFaceIds")!.GetValue(area)!;
        for (int k = 0; k + 2 < rightSide.Points.Length; k += 3)
        {
            var point = new Vec3(rightSide.Points[k], rightSide.Points[k + 1], rightSide.Points[k + 2]);
            chain.Add(side.Frame.Project(point));
            chain3D.Add(point);
        }
        chainFaceIds.AddRange(rightSide.SegmentFaceIds);
        var references = new List<ExitEdgeReference>();
        foreach (var reference in rightSide.EdgeReferences)
            references.Add(CadExitBoundary2D.CopyReference(reference));
        areaType.GetProperty("ExitEdgeRefs")!.SetValue(area, references);
        areaType.GetProperty("ExitBoundary")!.SetValue(area,
            new CadExitBoundary2D(side.Id.Value, side.Revision.Value, references));

        ((IList)Field<object>(window, "_areas")).Add(area);
        Invoke(window, "ActivateArea", area);
        // 패턴 영역 높이 36(=40-2*2)을 spacing 6으로 나누면 레인 6개(짝수).
        // 짝수 레인이면 단일 사행 경로의 양 끝이 모두 우측 체인에 닿는 해가 안정적으로 존재한다.
        marginBox.Text = "2";
        spacingBox.Text = "6";
        Invoke(window, "RecomputeAnchorCandidates", area);

        var candidates = (List<ExcludedAnchorCandidate>)areaType.GetProperty("ExcludedCandidates")!.GetValue(area)!;
        if (candidates.Count == 0)
            Console.WriteLine("LastExitFailure: " + (areaType.GetProperty("LastExitFailure")!.GetValue(area) ?? "<null>"));
        Check(candidates.Count > 0, "exclusion+exit candidate plan is computed for the planar face");
        if (candidates.Count == 0) { Cleanup(window, marginBox, spacingBox, fixtureDir); return; }

        // 후보가 여럿이면 실제 선택 경로로 첫 후보를 확정한다(원본 binding 검증 포함).
        if (areaType.GetProperty("SelectedAnchorCandidate")!.GetValue(area) == null)
        {
            var anchors = (List<AnchoredCandidate>)areaType.GetProperty("AnchorCandidates")!.GetValue(area)!;
            var selectArgs = new object[] { area, anchors[0], null };
            bool selected = (bool)StaticInvoke(typeof(MainWindow), "TrySelectAnchorCandidate", selectArgs);
            Check(selected && areaType.GetProperty("SelectedAnchorCandidate")!.GetValue(area) != null,
                "a real exit candidate pair is selectable by identity");
            if (!selected) { Cleanup(window, marginBox, spacingBox, fixtureDir); return; }
        }
        // 생성 진입에서 계획 지문이 달라 후보가 재계산되며 선택을 잃지 않게 현재 입력 지문을 고정한다.
        areaType.GetProperty("ExcludedPlanKey")!.SetValue(area,
            (string)Invoke(window, "BuildExcludedPlanKey", area, 2.0, 6.0));

        Invoke(window, "GenerateMultiFace");
        var pattern = areaType.GetProperty("Pattern")!.GetValue(area) as SerpentineResult;
        Check(pattern != null, "exclusion+exit generation produces a route plan");
        if (pattern == null) { Cleanup(window, marginBox, spacingBox, fixtureDir); return; }

        var plan = candidates[0];
        Check(areaType.GetProperty("ExclusionPieces")!.GetValue(area) == null,
            "anchored exclusion generation uses the single route-piece plan");
        Console.WriteLine($"PATTERN lanes={pattern.LaneCount} nominal={pattern.NominalLaneCount}" +
            $" adjusted={pattern.LaneCountAdjusted}" +
            $" runs={pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)}" +
            $" connectors={pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector)}" +
            $" start={plan.Start} end={plan.End}");
        // 60x40 영역, margin 2, spacing 6 → 패턴 높이 36/6 = 레인 6개(짝수) y=-15,-9,-3,3,9,15.
        // 제외 (-50,-10)-(-44,-8)은 y=-9 레인 하나를 분할 → 6+1=7 발열 런, 7-1=6 커넥터.
        Check(pattern.LaneCount == 6 && !pattern.LaneCountAdjusted,
            "the even six-lane layout needs no lane adjustment");
        Check(pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun) == 7,
            "one split lane contributes two heating runs plus five whole lanes");
        Check(pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector) == 6,
            "the seven heating runs are joined by six turn connectors");
        Check(plan.Start.DistanceTo(pattern.Path[0]) < 1e-9 && plan.End.DistanceTo(pattern.Path[^1]) < 1e-9,
            "generated path endpoints match the selected exit-face anchors");
        bool runsOutside = pattern.RoutePieces
            .Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .SelectMany(piece => piece.Points)
            .All(p => !(p.X > -49.999 && p.X < -44.001 && p.Y > -9.999 && p.Y < -8.001));
        Check(runsOutside, "every heating run stays outside the excluded rectangle");

        // 분할 레인(y=-9)의 두 조각이 실제로 존재하고 제외 사각형 양쪽에 닿는지 구조로 확인한다.
        // 조각 끝점은 제외 경계 x=-50/-44 위에 정확히 놓인다.
        var runs = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();
        Check(runs.Any(piece => piece.Points.Any(p => p.Y > -9.999 && p.Y < -8.001) && piece.Points.All(p => p.X <= -49.999))
            && runs.Any(piece => piece.Points.Any(p => p.Y > -9.999 && p.Y < -8.001) && piece.Points.All(p => p.X >= -44.001)),
            "the split lane yields one run left of the exclusion and one right of it");
        Check(pattern.RoutePieces.Zip(pattern.RoutePieces.Skip(1),
            (first, second) => first.Points[^1].DistanceTo(second.Points[0]) < 1e-6).All(ok => ok),
            "consecutive route pieces are geometrically connected");

        var projected = (List<RoutePieceResult>)areaType.GetProperty("ProjectedPieces")!.GetValue(area)!;
        Check(projected.Count == pattern.RoutePieces.Count && projected.All(piece => piece != null),
            "every route piece maps onto the working surface");
        Check((bool)areaType.GetProperty("PatternDisplayed")!.GetValue(area)!,
            "generated anchored exclusion plan is displayed");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("회로"),
            "status reports the number of circuits");

        Cleanup(window, marginBox, spacingBox, fixtureDir);
        Console.WriteLine($"{count} exclusion+exit checks passed.");
    }
}
