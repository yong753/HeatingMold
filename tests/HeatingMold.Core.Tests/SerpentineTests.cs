using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class SerpentineTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    // T1: 직사각형 + 마진 ???�인 ?�·길?�·간�??�치
    [Fact]
    public void T1_Rectangle_Margin_LaneCount_Length_Spacing()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(0.0, r.AngleDeg, 9);
        Assert.Equal(5, r.LaneCount);
        Assert.Equal(5, r.Segments.Count);

        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            Assert.Equal(10.0 + 10.0 * i, s.Start.Y, 9);   // ?�인 y = 10,20,30,40,50
            if (i > 0) Assert.Equal(10.0, s.Start.Y - r.Segments[i - 1].Start.Y, 9);
            Assert.Equal(90.0, s.Length, 9);               // 마진 5 ??100 ??2×5
        }
    }

    // T2: ?� ?�함 ?�리�????�인 분절 (MVP ?�용)
    [Fact]
    public void T2_HoleSplitsLaneIntoSegments()
    {
        var outer = Rect(0, 0, 100, 60);
        var holes = new List<IReadOnlyList<Vec2>> { Rect(40, 18, 60, 22) };
        var r = Serpentine.Generate(outer, holes, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(5, r.LaneCount);
        Assert.Equal(6, r.Segments.Count);   // ?�인 5�?�?y=20 ?�나가 ?�???�해 2분절

        var split = r.Segments.Where(s => Math.Abs(s.Start.Y - 20.0) < 1e-9).ToList();
        Assert.Equal(2, split.Count);

        var spans = split
            .Select(s => (Min: Math.Min(s.Start.X, s.End.X), Max: Math.Max(s.Start.X, s.End.X)))
            .OrderBy(t => t.Min)
            .ToArray();
        Assert.Equal(5.0, spans[0].Min, 9);    // ?�역 좌측 (마진 ?�프??
        Assert.Equal(35.0, spans[0].Max, 9);   // ?� x=40 ??마진 5
        Assert.Equal(65.0, spans[1].Min, 9);   // ?� x=60 + 마진 5
        Assert.Equal(95.0, spans[1].Max, 9);   // ?�역 ?�측
    }

    // T3: ?�???�인 U??반전 ???�일 ?�속 ?�?�어
    [Fact]
    public void T3_OddLaneCount_AlternatesDirection_SingleContinuousPath()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(5, r.LaneCount);   // ?�???�인

        // 진행 방향 교�?: ?�인 0 LTR, 1 RTL, 2 LTR, ...
        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            bool ltr = s.Start.X < s.End.X;
            Assert.Equal(i % 2 == 0, ltr);
        }

        // ?�일 ?�속 ?�리?�인: ?�그먼트?�이 경로???�서?��??�함
        Assert.Equal(r.Segments.First().Start, r.Path[0]);
        Assert.Equal(r.Segments.Last().End, r.Path[^1]);

        int idx = 0;
        foreach (SerpentineSegment s in r.Segments)
        {
            while (idx < r.Path.Count && r.Path[idx] != s.Start) idx++;
            Assert.True(idx < r.Path.Count, $"?�그먼트 ?�작???�락 (lane {s.LaneIndex})");
            Assert.Equal(s.End, r.Path[idx + 1]);
            idx += 2;
        }
    }

    // T4: 방향�??�전 ??UV ?�축 ?�동 ?�출 + ?�전 불�???
    [Fact]
    public void T4_AutoLongAxisRotation()
    {
        var wide = Serpentine.Generate(Rect(0, 0, 100, 60), null, new PatternParams { Margin = 5, Spacing = 10 });
        var tall = Serpentine.Generate(Rect(0, 0, 60, 100), null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(0.0, wide.AngleDeg, 9);    // ?�축 = X
        Assert.Equal(90.0, tall.AngleDeg, 9);   // ?�축 = Y

        // ?�일 ?�역(90×50)?�로 ?�규?????�인 ?�·총 길이 ?�일
        Assert.Equal(wide.LaneCount, tall.LaneCount);
        Assert.Equal(wide.TotalLength, tall.TotalLength, 6);

        // tall???�인?� ?�드 Y�?방향
        SerpentineSegment s0 = tall.Segments[0];
        Assert.True(Math.Abs(s0.End.Y - s0.Start.Y) > 80.0);
        Assert.True(Math.Abs(s0.End.X - s0.Start.X) < 1e-6);
    }

    // T5: �?길이 sanity ???�인 ??+ U??반원 길이
    [Fact]
    public void T5_TotalLengthSanity()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        double straight = 5 * 90.0;           // ?�인 5�?× 길이 90
        double turns = 4 * Math.PI * 5.0;     // U??반원 4�?(r = Spacing/2 = 5)

        Assert.True(r.TotalLength > straight);
        Assert.InRange(r.TotalLength, straight + turns * 0.98, straight + turns * 1.02);
    }

    // T6: 비볼�??�기) �????�접 ?�인 극단?�이 ?�게 ?�긋?�도 커넥?��? �?밖으�??�탈?��? ?�아????(#597 ?��?)
    [Fact]
    public void T6_NonConvexWedge_ConnectorStaysInsideFace()
    {
        // ?�단???�만?�게 기울?�진 ?�기 ???�인 극단?�이 ?�인??~130mm???�동
        var outer = new List<Vec2> { new(0, 0), new(400, 0), new(400, 120), new(0, 90) };
        double margin = 5, spacing = 10;
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = margin, Spacing = spacing });

        // ?�리�?존재 ?�인: ?�접 ?�인 ?�↔?�작 직선거리가 간격보다 ?�씬 ??
        double maxJump = 0;
        for (int i = 0; i + 1 < r.Segments.Count; i++)
        {
            if (r.Segments[i].LaneIndex == r.Segments[i + 1].LaneIndex) continue;
            maxJump = Math.Max(maxJump, r.Segments[i].End.DistanceTo(r.Segments[i + 1].Start));
        }
        Assert.True(maxJump > 3 * spacing, $"?�리�??�음 (maxJump={maxJump:F1})");

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"�?�?경로??{p}");
    }

    // T7: 볼록 직사각형?�라??margin < Spacing/2 ?�면 반원??�?밖으�??��?므�?경계 ?�우?�되?�야 ??
    [Fact]
    public void T7_ConvexButTightMargin_StaysInsideFace()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 2, Spacing = 10 });

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"�?�?경로??{p}");
    }

    [Fact]
    public void RoutePieces_ReconstructPathAndClassifyConnectors()
    {
        var result = Serpentine.Generate(Rect(0, 0, 100, 60),
            new List<IReadOnlyList<Vec2>> { Rect(40, 18, 60, 22) },
            new PatternParams { Margin = 5, Spacing = 10 });
        Assert.Equal(result.LaneCount - 1, result.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Single(result.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        var rebuilt = result.RoutePieces.SelectMany((p, i) => i == 0 ? p.Points : p.Points.Skip(1)).ToList();
        Assert.Equal(result.Path, rebuilt);
        Assert.Equal(result.TotalLength, result.RoutePieces.Sum(p => p.Length), 8);
    }

    [Fact]
    public void DisabledTurnSplitsTopologyButFixedConnectorIsRetained()
    {
        var pieces = new[]
        {
            new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, new[] { new Vec2(0,0), new Vec2(1,0) }),
            new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, 1, new[] { new Vec2(1,0), new Vec2(1,1) }),
            new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 1, new[] { new Vec2(1,1), new Vec2(2,1) }),
            new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector, 1, new[] { new Vec2(2,1), new Vec2(3,1) }),
            new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 1, new[] { new Vec2(3,1), new Vec2(4,1) }),
        };
        var components = SerpentineTopology.Compute(pieces, new[] { false });
        Assert.True(components.Count == 2);
        Assert.Equal(new[] { new Vec2(0,0), new Vec2(1,0) }, components[0]);
        Assert.Equal(new[] { new Vec2(1,1), new Vec2(2,1), new Vec2(3,1), new Vec2(4,1) }, components[1]);
        Assert.Equal(1, SerpentineTopology.Compute(pieces).Count);
    }

    // T10: 배치 방향 강제 ??FrameY???�축�?무�??�게 90°
    [Fact]
    public void T10_DirectionOverride_ForcesAngle()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null,
            new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameY });

        Assert.Equal(90.0, r.AngleDeg, 9);
        Assert.Equal(9, r.LaneCount);                        // 로컬 ?�이 100 ??2×5 = 90
        Assert.All(r.Segments, s => Assert.Equal(50.0, s.Length, 9));
        Assert.All(r.Segments, s => Assert.Equal(s.Start.X, s.End.X, 9));   // ?�드?�서 ?�로 ?�인

        var tall = Rect(0, 0, 60, 100);
        var rx = Serpentine.Generate(tall, null,
            new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameX });
        Assert.Equal(0.0, rx.AngleDeg, 9);
    }

    private static readonly List<Vec2> RightExit = new() { new(95, 5), new(95, 65) };
    private static readonly List<Vec2> LeftExit = new() { new(5, 5), new(5, 65) };

    // T11: 짝수 ?�인 ??같�? ?�출면에 ???�이 ?�는 ?�보 1?? 그�?�??�성 ?�공
    [Fact]
    public void T11_EvenLanes_AnchoredSucceeds()
    {
        var outer = Rect(0, 0, 100, 70);   // 마진 ???�이 60 ??6?�인
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var candidates = Serpentine.ComputeCandidates(outer, null, p, RightExit, PatternDirection.FrameX);
        Assert.Single(candidates);
        AnchoredCandidate c = candidates[0];
        Assert.Equal(0.0, c.AngleDeg, 9);
        Assert.Equal(95.0, c.Start.X, 9);
        Assert.Equal(10.0, c.Start.Y, 9);
        Assert.Equal(95.0, c.End.X, 9);
        Assert.Equal(60.0, c.End.Y, 9);

        var r = Serpentine.GenerateAnchored(outer, null, p, RightExit, c.Start, c.End, PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        SerpentineResult pattern = r.Pattern!;
        Assert.Equal(6, pattern.LaneCount);
        Assert.Equal(c.Start, pattern.Path[0]);
        Assert.Equal(c.End, pattern.Path[^1]);

        var left = Serpentine.ComputeCandidates(outer, null, p, LeftExit, PatternDirection.FrameX);
        Assert.Single(left);
        Assert.Equal(5.0, left[0].Start.X, 9);
        Assert.Equal(60.0, left[0].End.Y, 9);
    }

    // T12: ?�???�인 ??같�? 면에 ???�이 ?????�음 (모서�?반�?)
    [Fact]
    public void T12_OddNominalLanes_ReducedToEven()
    {
        var outer = Rect(0, 0, 100, 60);   // 5?�인
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var exit = new List<Vec2> { new(95, 5), new(95, 55) };

        var candidates = Serpentine.ComputeCandidates(outer, null, p, exit, PatternDirection.FrameX);
        AnchoredCandidate c = Assert.Single(candidates);
        Assert.Equal(new Vec2(95, 15), c.Start);
        Assert.Equal(new Vec2(95, 45), c.End);

        var r = Serpentine.GenerateAnchored(outer, null, p, exit, c.Start, c.End, PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        Assert.Equal(4, r.Pattern!.LaneCount);
        Assert.Equal(5, r.Pattern!.NominalLaneCount);
        Assert.True(r.Pattern!.LaneCountAdjusted);
        Assert.Equal(new Vec2(95, 15), r.Pattern!.Path[0]);
        Assert.Equal(new Vec2(95, 45), r.Pattern!.Path[^1]);

        // 줄이기 전 레인 위치(10 / 50)를 고집하면 임의 보정 없이 실패해야 한다.
        var old = Serpentine.GenerateAnchored(outer, null, p, exit,
            new Vec2(95, 10), new Vec2(95, 50), PatternDirection.FrameX);
        Assert.False(old.Succeeded);
        Assert.Equal(AnchoredFailureReason.AnchorNotAtLaneEnd, old.Failure);
        Assert.Null(old.Pattern);
    }

    // T13: ?�작?�이 ?�출�??�에 ?�음
    [Fact]
    public void T13_AnchorOffExitChain_Fails()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var r = Serpentine.GenerateAnchored(outer, null, p, RightExit,
            new Vec2(50, 10), new Vec2(95, 60), PatternDirection.FrameX);
        Assert.Equal(AnchoredFailureReason.AnchorNotOnExitChain, r.Failure);
    }

    // T14: ?�출�??��?�?경로 ?�점???�닌 ?�치
    [Fact]
    public void T14_AnchorNotAtLaneEnd_Fails()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var r = Serpentine.GenerateAnchored(outer, null, p, RightExit,
            new Vec2(95, 10), new Vec2(95, 20), PatternDirection.FrameX);
        Assert.Equal(AnchoredFailureReason.AnchorNotAtLaneEnd, r.Failure);
    }

    // T15: ?�작/종료�?바꾸�??�패?�고, ?�바�??�서�??�공
    [Fact]
    public void T15_SwappedAnchors_ReportParityConflict()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var swapped = Serpentine.GenerateAnchored(outer, null, p, RightExit,
            new Vec2(95, 60), new Vec2(95, 10), PatternDirection.FrameX);
        Assert.Equal(AnchoredFailureReason.ParityConflict, swapped.Failure);

        var ok = Serpentine.GenerateAnchored(outer, null, p, RightExit,
            new Vec2(95, 10), new Vec2(95, 60), PatternDirection.FrameX);
        Assert.True(ok.Succeeded);
    }

    // T16: Auto 방향?� ?�축???�르�??�보???�나�??�온??
    [Fact]
    public void T16_AutoDirection_UsesLongAxis()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var candidates = Serpentine.ComputeCandidates(outer, null, p, RightExit, PatternDirection.Auto);
        Assert.Single(candidates);
        Assert.Equal(0.0, candidates[0].AngleDeg, 9);
    }

    // T17: ?�출면이 극단 ?�인 ?�과 ?�어???�음
    [Fact]
    public void T17_ExitChainNotSpanning_Fails()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var topExit = new List<Vec2> { new(0, 65), new(100, 65) };

        var r = Serpentine.GenerateAnchored(outer, null, p, topExit,
            new Vec2(5, 65), new Vec2(95, 65), PatternDirection.FrameX);
        Assert.Equal(AnchoredFailureReason.ExitChainNotSpanning, r.Failure);
    }

    // T18: 결과 경로/조각?????�이 ?�커?� ?�치?�고 길이 ?�이 ?��???
    [Fact]
    public void T18_AnchoredRoutePiecesMatchPathEnds()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };

        var r = Serpentine.GenerateAnchored(outer, null, p, RightExit,
            new Vec2(95, 10), new Vec2(95, 60), PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        SerpentineResult pattern = r.Pattern!;

        Assert.Equal(pattern.Path[0], pattern.RoutePieces[0].Points[0]);
        Assert.Equal(pattern.Path[^1], pattern.RoutePieces[^1].Points[^1]);
        Assert.Equal(pattern.TotalLength, pattern.RoutePieces.Sum(x => x.Length), 6);
    }

    // T19: ?�제 ?�출�?공유 모서�??�역 경계, ?�셋 ??�?chain?�로 줘도 마진 ?�셋 ?�점???�영???�커가 ?�다.
    [Fact]
    public void T19_BoundaryExitChainProjectsInsetLaneEnds()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var boundaryExit = new List<Vec2> { new(100, 0), new(100, 70) };

        var candidates = Serpentine.ComputeCandidates(outer, null, p, boundaryExit, PatternDirection.FrameX);
        AnchoredCandidate c = Assert.Single(candidates);
        Assert.Equal(100.0, c.Start.X, 6);
        Assert.Equal(10.0, c.Start.Y, 6);
        Assert.Equal(100.0, c.End.X, 6);
        Assert.Equal(60.0, c.End.Y, 6);

        var r = Serpentine.GenerateAnchored(outer, null, p, boundaryExit, c.Start, c.End, PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        SerpentineResult pattern = r.Pattern!;

        Assert.Equal(new Vec2(100, 10), pattern.Path[0]);
        Assert.Equal(new Vec2(100, 60), pattern.Path[^1]);
        Assert.Equal(SerpentineRoutePieceKind.ExitPiece, pattern.RoutePieces[0].Kind);
        Assert.Equal(new Vec2(95, 10), pattern.RoutePieces[0].Points[^1]);
        Assert.Equal(SerpentineRoutePieceKind.ExitPiece, pattern.RoutePieces[^1].Kind);
        Assert.Equal(new Vec2(100, 60), pattern.RoutePieces[^1].Points[^1]);
        Assert.Equal(pattern.TotalLength, pattern.RoutePieces.Sum(x => x.Length), 6);
        Assert.Equal(2 * 5.0, pattern.TotalLength - pattern.RoutePieces
            .Where(x => x.Kind != SerpentineRoutePieceKind.ExitPiece).Sum(x => x.Length), 6);
    }

    // T20: 경계 chain + ?�???�인 ??같�? �??�출 불�?, ?�유 명시.
    [Fact]
    public void T20_BoundaryExitChainOddLanesFitsEven()
    {
        var outer = Rect(0, 0, 100, 60);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var boundaryExit = new List<Vec2> { new(100, 0), new(100, 60) };

        var c = Assert.Single(Serpentine.ComputeCandidates(outer, null, p, boundaryExit, PatternDirection.FrameX));
        Assert.Equal(new Vec2(100, 15), c.Start);
        Assert.Equal(new Vec2(100, 45), c.End);
        var r = Serpentine.GenerateAnchored(outer, null, p, boundaryExit, c.Start, c.End, PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        Assert.Equal(4, r.Pattern!.LaneCount);
        Assert.Equal(5, r.Pattern!.NominalLaneCount);
        Assert.Equal(new Vec2(100, 15), r.Pattern!.Path[0]);
        Assert.Equal(new Vec2(100, 45), r.Pattern!.Path[^1]);
        Assert.Equal(new Vec2(95, 15), r.Pattern!.RoutePieces[0].Points[^1]);
    }

    // T21: 반�???경계 chain?�면 ?�작 ?�인 방향???�집?�??같�? �????�을 만족?�다.
    [Fact]
    public void T21_LeftBoundaryExitChainFlipsStartSide()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var boundaryExit = new List<Vec2> { new(0, 0), new(0, 70) };

        var c = Assert.Single(Serpentine.ComputeCandidates(outer, null, p, boundaryExit, PatternDirection.FrameX));
        Assert.Equal(new Vec2(0, 10), c.Start);
        var r = Serpentine.GenerateAnchored(outer, null, p, boundaryExit, c.Start, c.End, PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        Assert.Equal(new Vec2(0, 10), r.Pattern!.Path[0]);
        Assert.Equal(new Vec2(0, 60), r.Pattern!.Path[^1]);
        Assert.Equal(new Vec2(5, 10), r.Pattern!.RoutePieces[0].Points[^1]);
    }

    // T22: 짝수로 줄일 여지가 없으면(레인 1개) 임의 보정 없이 ParityConflict로 실패한다.
    [Fact]
    public void T22_TooFewLanesForEvenFails()
    {
        var outer = Rect(0, 0, 100, 60);
        var p = new PatternParams { Margin = 5, Spacing = 30 };
        var exit = new List<Vec2> { new(95, 5), new(95, 55) };

        Assert.Empty(Serpentine.ComputeCandidates(outer, null, p, exit, PatternDirection.FrameX));
        var r = Serpentine.GenerateAnchored(outer, null, p, exit,
            new Vec2(95, 20), new Vec2(95, 40), PatternDirection.FrameX);
        Assert.False(r.Succeeded);
        Assert.Null(r.Pattern);
        Assert.Equal(AnchoredFailureReason.ParityConflict, r.Failure);
    }

    // T23: 인출면을 쓰지 않는 일반 생성은 홀수 레인을 그대로 유지하고 조정 플래그를 세우지 않는다.
    [Fact]
    public void T23_UnanchoredGenerateKeepsOddLaneCount()
    {
        var pattern = Serpentine.Generate(Rect(0, 0, 100, 60), null,
            new PatternParams { Margin = 5, Spacing = 10 }, constrainConnectors: true);

        Assert.Equal(5, pattern.LaneCount);
        Assert.Equal(0, pattern.NominalLaneCount);
        Assert.False(pattern.LaneCountAdjusted);
    }

    // T24: 인출면이 두 면으로 쪼개져 긴 변의 절반만 덮으면 경로 양 끝을 동시에 지나지 못해 후보가 없다.
    [Fact]
    public void T24_SplitExitChainCoversHalfSide_NoCandidate()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var halfExit = new List<Vec2> { new(95, 5), new(95, 35) };

        Assert.Empty(Serpentine.ComputeCandidates(outer, null, p, halfExit, PatternDirection.FrameX));
    }

    // T25: 반대편 면이 달라 중간에서 끊긴 두 반쪽을 이어붙인 체인(중간 꼭짓점 포함)은 후보를 만든다.
    [Fact]
    public void T25_MergedSplitExitChainSpansBothEnds()
    {
        var outer = Rect(0, 0, 100, 70);
        var p = new PatternParams { Margin = 5, Spacing = 10 };
        var mergedExit = new List<Vec2> { new(95, 5), new(95, 35), new(95, 65) };

        var candidate = Assert.Single(
            Serpentine.ComputeCandidates(outer, null, p, mergedExit, PatternDirection.FrameX));
        Assert.Equal(new Vec2(95, 10), candidate.Start);
        Assert.Equal(new Vec2(95, 60), candidate.End);

        var r = Serpentine.GenerateAnchored(outer, null, p, mergedExit, candidate.Start, candidate.End,
            PatternDirection.FrameX);
        Assert.True(r.Succeeded);
        Assert.Equal(candidate.Start, r.Pattern!.RoutePieces[0].Points[0]);
        Assert.Equal(candidate.End, r.Pattern!.RoutePieces[^1].Points[^1]);
    }

    private static bool InsideOrOn(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        for (int i = 0; i < ring.Count; i++)
            if (DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]) <= 1e-6) return true;

        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (((ring[i].Y > p.Y) != (ring[j].Y > p.Y)) &&
                (p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X))
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, len2 = abx * abx + aby * aby;
        if (len2 < 1e-18) return p.DistanceTo(a);
        double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return p.DistanceTo(new Vec2(a.X + t * abx, a.Y + t * aby));
    }
}
