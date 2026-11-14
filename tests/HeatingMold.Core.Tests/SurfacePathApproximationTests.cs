using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 표면을 찾지 못한 짧은 내부 표본만 앞뒤 정상점으로 연결하는 제한 근사 정책의 계약.
/// 정상 fast path는 strict와 완전히 같아야 하고, 한도(2D 진행/3D 거리 ≤ 10 mm)를 넘으면
/// 부분 결과 없이 명시적으로 거부해야 한다.
/// </summary>
public class SurfacePathApproximationTests
{
    private static SurfaceQueryResult Hit(Vec2 p, int face = 7) =>
        SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, new Vec3(p.X, p.Y, 0), face));

    private static SurfaceQueryResult Miss(Vec2 _) => SurfaceQueryResult.FromMiss("hole");

    private static SurfaceQueryResult HitOrMiss(Vec2 p, Func<Vec2, bool> missing) =>
        missing(p) ? Miss(p) : Hit(p);

    private static bool TryMap(Vec2[] path, Func<Vec2, SurfaceQueryResult> query,
        out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure,
        double gapSpacing = RouteSurfaceValidationPolicy.GapSpacing) =>
        SurfacePathApproximation.TryMapRoute(path, query, null, out mapped, out report, out failure, gapSpacing);

    /// <summary>±0 비트 차이까지 구분하는 표본 동일성 비교.</summary>
    private static void AssertBitIdentical(SurfaceMappedSample expected, SurfaceMappedSample actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.DatumPoint.X),
            BitConverter.DoubleToInt64Bits(actual.DatumPoint.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.DatumPoint.Y),
            BitConverter.DoubleToInt64Bits(actual.DatumPoint.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Point.X),
            BitConverter.DoubleToInt64Bits(actual.Point.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Point.Y),
            BitConverter.DoubleToInt64Bits(actual.Point.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Point.Z),
            BitConverter.DoubleToInt64Bits(actual.Point.Z));
        Assert.Equal(expected.FaceId, actual.FaceId);
    }

    private static void AssertBitIdentical(IReadOnlyList<SurfaceMappedSample> expected,
        IReadOnlyList<SurfaceMappedSample> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) AssertBitIdentical(expected[i], actual[i]);
    }

    // ---------- 정상 fast path ----------

    [Fact]
    public void StrictSuccess_ReturnsIdenticalSamplesIncludingGapProbes()
    {
        Vec2[] path = { new(0, 0), new(30, 0), new(30, 12) };
        SurfaceMappedSample? StrictHit(Vec2 p) => new(p, new Vec3(p.X, p.Y, 0), 4);

        Assert.True(SurfacePathMapper.TryMapRouteStrict(path, StrictHit,
            out List<SurfaceMappedSample> strict, out string strictFailure), strictFailure);
        Assert.True(TryMap(path, p => Hit(p, 4), out List<SurfaceMappedSample> approximated,
            out SurfaceApproximationReport report, out string failure), failure);

        Assert.False(report.Used);
        Assert.Equal(0, report.BridgeCount);
        // ±0 비트 차이까지 동일해야 한다.
        AssertBitIdentical(strict, approximated);
    }

    [Fact]
    public void StrictSuccessWithOverrides_IsBitIdentical()
    {
        Vec2[] path = { new(0, 0), new(12, 0), new(12, 9) };
        // 두 호출이 같은 조회 결과를 쓰도록 같은 delegate를 공유한다(-0.0 보존 포함).
        Vec3 Surface(Vec2 p) => new(p.X, p.Y, -0.0);
        SurfaceMappedSample? StrictHit(Vec2 p) => new(p, Surface(p), 4);
        SurfaceQueryResult TypedHit(Vec2 p) =>
            SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, Surface(p), 4));
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [0] = new(path[0], new Vec3(0, -0.0, 0.01), 99),
            [2] = new(path[2], new Vec3(12, 9, 0.02), 98),
        };

        Assert.True(SurfacePathMapper.TryMapRouteStrict(path, StrictHit, overrides,
            out List<SurfaceMappedSample> strict, out string strictFailure), strictFailure);
        Assert.True(SurfacePathApproximation.TryMapRoute(path, TypedHit, overrides,
            out List<SurfaceMappedSample> approximated, out SurfaceApproximationReport report,
            out string failure), failure);

        Assert.False(report.Used);
        AssertBitIdentical(strict, approximated);
        AssertBitIdentical(overrides[0], approximated[0]);
    }

    [Fact]
    public void StrictFailureWithoutHole_StillMatchesStrictSampleSequence()
    {
        // strict가 실패해도 순수 Hit만 있으면 성공 결과는 같아야 한다.
        Vec2[] path = { new(0, 0), new(4, 0) };
        SurfaceMappedSample? StrictHit(Vec2 p) => new(p, new Vec3(p.X, p.Y, 0), 4);
        Assert.True(SurfacePathMapper.TryMapRouteStrict(path, StrictHit,
            out List<SurfaceMappedSample> strict, out string strictFailure), strictFailure);

        Assert.True(TryMap(path, p => Hit(p, 4), out List<SurfaceMappedSample> mapped,
            out _, out string failure), failure);
        AssertBitIdentical(strict, mapped);
    }

    // ---------- 고립/짧은 누락 구간 ----------

    [Fact]
    public void IsolatedMiss_BridgesWithRealSurroundingHits()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        // x=20 주변 ±0.4 mm만 표면 없음 → 고립된 짧은 구간.
        Assert.True(TryMap(path, p => HitOrMiss(p, q => Math.Abs(q.X - 20) < 0.4),
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure),
            failure);

        Assert.True(report.Used);
        Assert.Equal(1, report.BridgeCount);
        SurfaceApproximationBridge bridge = Assert.Single(report.Bridges);
        Assert.InRange(bridge.AdvanceMm, 0, RouteApproximationPolicy.MaxBridgeAdvanceMm);
        Assert.InRange(bridge.Gap3DMm, 0, RouteApproximationPolicy.MaxBridge3DGapMm);
        // 연결 양끝은 실제 Hit 좌표여야 하고, 그 사이에 조작된 중간점이 없어야 한다.
        Assert.Contains(mapped, s => s.DatumPoint == bridge.Left);
        Assert.Contains(mapped, s => s.DatumPoint == bridge.Right);
        Assert.DoesNotContain(mapped, s => Math.Abs(s.DatumPoint.X - 20) < 1e-9);
        for (int i = 1; i < mapped.Count; i++)
            Assert.True(mapped[i - 1].DatumPoint.X <= mapped[i].DatumPoint.X);
    }

    [Fact]
    public void SeveralConsecutiveMissesWithinTenMillimetresAreBridged()
    {
        Vec2[] path = { new(0, 0), new(60, 0) };
        Assert.True(TryMap(path, p => HitOrMiss(p, q => q.X > 30 && q.X < 35),
            out _, out SurfaceApproximationReport report, out string failure), failure);

        Assert.Equal(1, report.BridgeCount);
        Assert.InRange(report.MaxAdvanceMm, 4.5, 6.0);
        Assert.InRange(report.TotalAdvanceMm, 4.5, 6.0);
    }

    [Fact]
    public void ManyMissesWithShortTotalAdvanceAreAcceptedWithoutCountLimit()
    {
        Vec2[] path = { new(0, 0), new(200, 0) };
        // 10 mm 간격으로 1 mm 폭 홈이 19곳 — 개수는 많지만 각 연결은 1 mm 남짓.
        Assert.True(TryMap(path, p => HitOrMiss(p, q =>
                Math.Abs(q.X - (10 * (int)(q.X / 10) + 5)) < 0.6 && q.X > 1),
            out _, out SurfaceApproximationReport report, out string failure), failure);

        Assert.True(report.BridgeCount >= 15);
        Assert.InRange(report.MaxAdvanceMm, 0, RouteApproximationPolicy.MaxBridgeAdvanceMm);
    }

    [Fact]
    public void TwoSeparatedGapsAreJudgedIndependentlyAndBothReported()
    {
        Vec2[] path = { new(0, 0), new(120, 0) };
        Assert.True(TryMap(path, p => HitOrMiss(p, q =>
                (Math.Abs(q.X - 30) < 0.5) || (Math.Abs(q.X - 90) < 0.5)),
            out _, out SurfaceApproximationReport report, out string failure), failure);

        Assert.Equal(2, report.BridgeCount);
        Assert.All(report.Bridges, bridge => Assert.InRange(bridge.AdvanceMm, 0, 2.5));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AdaptiveProbesThatAllMissAreSplitIntoIndependentGaps(bool vertical, bool reverse)
    {
        // 초기 적응 표본(25/50/75%)이 모두 Miss인 경우에도 실제로는 서로 떨어진 좁은
        // 누락 구간일 수 있다. 하나의 넓은 누락으로 합쳐 거부하면 정상 경로를 잃는다.
        Vec2 a = new(0, 0);
        Vec2 b = vertical ? new(0, 100) : new(100, 0);
        Vec2[] path = reverse ? new[] { b, a } : new[] { a, b };
        double Along(Vec2 p) => vertical ? p.Y : p.X;
        double Travel(Vec2 p) => reverse ? 100 - Along(p) : Along(p);
        bool Narrow(Vec2 p)
        {
            double t = Travel(p);
            return Math.Abs(t - 25) < 0.1 || Math.Abs(t - 50) < 0.1 || Math.Abs(t - 75) < 0.1;
        }

        Assert.True(TryMap(path, p => HitOrMiss(p, Narrow),
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure), failure);

        Assert.Equal(3, report.BridgeCount);
        Assert.All(report.Bridges, bridge => Assert.InRange(bridge.AdvanceMm, 0, 2.5));
        Assert.Equal(path[0], mapped[0].DatumPoint);
        Assert.Equal(path[^1], mapped[^1].DatumPoint);
    }

    [Fact]
    public void WideSingleGapIsStillRejectedEvenIfAdaptiveProbesAllMiss()
    {
        Vec2[] path = { new(0, 0), new(100, 0) };
        // 3 mm~97 mm가 실제로 비어 있는 하나의 넓은 누락.
        Assert.False(TryMap(path, p => HitOrMiss(p, q => q.X > 3 && q.X < 97),
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("근사 한도", failure);
    }

    // ---------- 한도 ----------

    [Fact]
    public void FewMissesWithAdvanceOverTenMillimetresAreRejected()
    {
        Vec2[] path = { new(0, 0), new(100, 0) };
        Assert.False(TryMap(path, p => HitOrMiss(p, q => q.X > 40 && q.X < 52.5),
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure, gapSpacing: 0));

        Assert.Empty(mapped);
        Assert.False(report.Used);
        Assert.Contains("근사 한도", failure);
    }

    [Fact]
    public void ThreeDimensionalJumpOverTenMillimetresIsRejected()
    {
        Vec2[] path = { new(0, 0), new(100, 0) };
        SurfaceQueryResult Query(Vec2 p)
        {
            if (p.X > 48 && p.X < 52) return Miss(p);
            double z = p.X <= 48 ? 0 : 20;      // 2D는 4 mm 간격, 3D는 20 mm 점프
            return SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, new Vec3(p.X, p.Y, z), 3));
        }

        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped,
            out _, out string failure, gapSpacing: 0));
        Assert.Empty(mapped);
        Assert.Contains("3D 거리", failure);
    }

    [Fact]
    public void ExactTenMillimetresIsAcceptedAndJustOverIsRejected()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        bool Hole(Vec2 p, double halfWidth) => p.X >= 20 - halfWidth && p.X <= 20 + halfWidth;

        // 경계 탐색 간격 1 mm이므로 실제 Hit 경계는 홈 바깥 1 mm 이내에서 잡힌다.
        // 홈 폭 8 mm → 측정 연결 10.0 mm(정확히 한도) → 허용.
        Assert.True(TryMap(path, p => HitOrMiss(p, q => Hole(q, 4.0)),
            out _, out SurfaceApproximationReport accepted, out string acceptedFailure), acceptedFailure);
        Assert.InRange(accepted.MaxAdvanceMm, 9.99, RouteApproximationPolicy.MaxBridgeAdvanceMm);

        // 홈 폭 10 mm → 측정 연결 12 mm → 한도 초과 거부.
        Assert.False(TryMap(path, p => HitOrMiss(p, q => Hole(q, 5.0)),
            out List<SurfaceMappedSample> rejected, out _, out string rejectedFailure));
        Assert.Empty(rejected);
        Assert.Contains("근사 한도", rejectedFailure);
    }

    [Fact]
    public void BridgeLimitIsTheSharedPolicyConstant()
    {
        Assert.Equal(10.0, RouteApproximationPolicy.MaxBridgeAdvanceMm);
        Assert.Equal(10.0, RouteApproximationPolicy.MaxBridge3DGapMm);
        Assert.Equal(1.0, RouteApproximationPolicy.ProbeSpacingMm);
        Assert.Equal("route-approx-v1", RouteApproximationPolicy.Version);
    }

    // ---------- 생략 금지 대상 ----------

    [Fact]
    public void PathVertexInsideGapIsNeverSkipped()
    {
        Vec2[] path = { new(0, 0), new(20, 0), new(40, 0) };
        Assert.False(TryMap(path, p => HitOrMiss(p, q => Math.Abs(q.X - 20) < 3),
            out List<SurfaceMappedSample> mapped, out _, out string failure, gapSpacing: 0));
        Assert.Empty(mapped);
        Assert.Contains("꼭짓점", failure);
    }

    [Fact]
    public void AnchorVertexOverrideSurvivesApproximation()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [0] = new(new Vec2(0, 0), new Vec3(0, 0, 0.01), 99),
        };
        Assert.True(SurfacePathApproximation.TryMapRoute(path,
            p => HitOrMiss(p, q => Math.Abs(q.X - 20) < 0.4), overrides,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure), failure);

        Assert.True(report.Used);
        Assert.Equal(99, mapped[0].FaceId);
        Assert.Equal(new Vec2(0, 0), mapped[0].DatumPoint);
        Assert.Equal(new Vec3(0, 0, 0.01), mapped[0].Point);
    }

    // ---------- 거부 조건 ----------

    [Theory]
    [InlineData(SurfaceQueryOutcome.Failure)]
    [InlineData(SurfaceQueryOutcome.Miss)]
    public void VertexQueryFailureOrMissRejectsWithoutPartialResult(SurfaceQueryOutcome outcome)
    {
        Vec2[] path = { new(0, 0), new(10, 0), new(20, 0) };
        SurfaceQueryResult Query(Vec2 p) => p == path[1]
            ? (outcome == SurfaceQueryOutcome.Failure
                ? SurfaceQueryResult.FromFailure("kernel")
                : SurfaceQueryResult.FromMiss("hole"))
            : Hit(p);
        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void InteriorKernelFailureIsNotBridged()
    {
        Vec2[] path = { new(0, 0), new(100, 0) };
        SurfaceQueryResult Query(Vec2 p) =>
            Math.Abs(p.X - 40) < 0.2 ? SurfaceQueryResult.FromFailure("kernel") : Hit(p);

        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        // strict가 하드 오류를 관측했으므로 근사 재시도를 하지 않는다.
        Assert.Contains("재시도 불가", failure);
    }

    [Fact]
    public void DuplicateCoordinateWithDifferentDepthIsRejected()
    {
        Vec2[] path = { new(0, 0), new(0, 0), new(5, 0) };
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [0] = new(new Vec2(0, 0), new Vec3(0, 0, 0), 9),
            [1] = new(new Vec2(0, 0), new Vec3(0, 0, 8), 9),
        };
        Assert.False(SurfacePathApproximation.TryValidateDuplicateRequiredPoints(path, overrides,
            out string identityFailure));
        Assert.Contains("3D 깊이", identityFailure);

        // 새 API도 fast path에서 이 필수점을 생략하지 않고 거부한다.
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p => Hit(p, 9), overrides,
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("3D 깊이", failure);
    }

    [Fact]
    public void DuplicateCoordinateWithInvalidSupportIsRejected()
    {
        Vec2[] path = { new(0, 0), new(0, 0), new(5, 0) };
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [0] = new(new Vec2(0, 0), new Vec3(0, 0, 0), 9),
            [1] = new(new Vec2(0, 0), new Vec3(0, 0, 0), -1),
        };
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p => Hit(p, 9), overrides,
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("지지 Face", failure);
    }

    [Fact]
    public void DuplicateCoordinateWithSameDepthAndSupportIsPreserved()
    {
        Vec2[] path = { new(0, 0), new(0, 0), new(5, 0) };
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [0] = new(new Vec2(0, 0), new Vec3(0, 0, 0), 9),
            [1] = new(new Vec2(0, 0), new Vec3(0, 0, 0), 9),
        };
        Assert.True(SurfacePathApproximation.TryValidateDuplicateRequiredPoints(path, overrides, out _));
        // 두 필수점이 같은 깊이/지지 면이면 정상 중복으로 보고 strict fast path 결과를 그대로 쓴다.
        Assert.True(SurfacePathApproximation.TryMapRoute(path, p => Hit(p, 9), overrides,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure), failure);
        Assert.False(report.Used);
        Assert.Equal(0, mapped[0].DatumPoint.X);
    }

    [Fact]
    public void DuplicateCoordinateWithOnlyLaterOverrideIsRejected()
    {
        // 같은 좌표의 뒤쪽 점만 필수점(인출점)이면 앞 점의 깊이/지지 면을 확인할 수 없으므로
        // 조용히 버리지 않고 거부한다.
        Vec2[] path = { new(0, 0), new(0, 0), new(20, 0), new(20, 0) };
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [3] = new(new Vec2(20, 0), new Vec3(20, 0, 7), 5),
        };
        Assert.False(SurfacePathApproximation.TryMapRoute(path,
            p => HitOrMiss(p, q => Math.Abs(q.X - 10) < 0.4), overrides,
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("생략할 수 없습니다", failure);
    }

    [Fact]
    public void HugeGapGridIsRejectedWithoutSampling()
    {
        // gapSpacing을 극단적으로 작게 주면 ceil(length/spacing)이 int 범위를 넘을 수 있다.
        // 그때 표본 검사를 생략한 성공으로 넘기면 안 된다.
        Vec2[] path = { new(0, 0), new(0.5, 0) };
        int calls = 0;
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            calls++;
            return Hit(p);
        }, null, out List<SurfaceMappedSample> mapped, out _, out string failure, 1e-12));

        Assert.Empty(mapped);
        Assert.Contains("재시도 불가", failure);
        Assert.True(calls < 100, "사전 거부되어 대량 루프가 실행되지 않아야 합니다: " + calls);
    }

    [Fact]
    public void ApproximationWalkHugeGapGridIsRejectedWithoutSampling()
    {
        // 앞 구간의 Miss로 strict가 중단된 뒤, 뒷 구간(모두 Hit)의 근사 격자가 int 범위를
        // 넘으면 표본 검사를 생략한 성공이 아니라 명시적 실패여야 한다.
        Vec2[] path = { new(0, 0), new(1, 0), new(1, 0.6) };
        var recorder = new SurfaceProbeRecorder(p => Math.Abs(p.X - 0.5) < 1e-9
            ? SurfaceQueryResult.FromMiss("hole")
            : Hit(p));
        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, recorder.HitProbe, null, out _, out _));
        Assert.True(recorder.SawMiss);
        Assert.False(recorder.SawFailure);

        int before = recorder.QueryCount;
        Assert.False(SurfacePathApproximation.TryMapRouteAfterStrictFailure(path, recorder, null,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure, 1e-12));
        Assert.Empty(mapped);
        Assert.False(report.Used);
        Assert.Contains("단계 수가 너무 많", failure);
        Assert.True(recorder.QueryCount - before < 100, "대량 루프가 실행되지 않아야 합니다.");
    }

    [Fact]
    public void StrictHugeGapGridIsRejectedInsteadOfSilentlySucceeding()
    {
        Vec2[] path = { new(0, 0), new(0.5, 0) };
        int calls = 0;
        SurfaceMappedSample? Hit(Vec2 p)
        {
            calls++;
            return new SurfaceMappedSample(p, new Vec3(p.X, p.Y, 0), 3);
        }
        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, Hit, out List<SurfaceMappedSample> mapped,
            out string failure, 1e-12));
        Assert.Empty(mapped);
        Assert.Contains("probe limit", failure);
        Assert.True(calls < 100, "사전 거부되어 대량 루프가 실행되지 않아야 합니다: " + calls);
    }

    [Fact]
    public void NonfiniteHitIsRejected()
    {
        Vec2[] path = { new(0, 0), new(20, 0) };
        SurfaceQueryResult Query(Vec2 p) => SurfaceQueryResult.FromHit(
            new SurfaceMappedSample(p, new Vec3(p.X, p.Y, double.NaN), 1));
        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("nonfinite", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HitWithoutSupportFaceIsRejected()
    {
        // FaceId 없는 Hit는 strict fast path에서는 기존과 같이 통과한다(계약 유지).
        // 근사 경로로 진입하면 지지 Face 없는 표본을 근거로 연결하지 않는다.
        Vec2[] path = { new(0, 0), new(20, 0) };
        SurfaceQueryResult Query(Vec2 p) => Math.Abs(p.X - 10) < 0.2
            ? SurfaceQueryResult.FromMiss("hole")
            : SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, new Vec3(p.X, p.Y, 0), -1));
        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("support face", failure);
    }

    [Fact]
    public void DatumResidualMismatchIsRejected()
    {
        Vec2[] path = { new(0, 0), new(20, 0) };
        SurfaceQueryResult Query(Vec2 p) => SurfaceQueryResult.FromHit(
            new SurfaceMappedSample(new Vec2(p.X + 0.5, p.Y), new Vec3(p.X, p.Y, 0), 1));
        Assert.False(TryMap(path, Query, out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("does not match", failure);
    }

    [Fact]
    public void ProbeBudgetExhaustionRejectsWithoutPartialPath()
    {
        Vec2[] path = { new(0, 0), new(5000, 0) };
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p => HitOrMiss(p, q => Math.Abs(q.X - 200) < 2),
            null, out List<SurfaceMappedSample> mapped, out _, out string failure, maxProbes: 60));
        Assert.Empty(mapped);
        Assert.Contains("probe limit", failure);
    }

    [Fact]
    public void NonfinitePathIsRejected()
    {
        Vec2[] path = { new(0, 0), new(double.NaN, 0) };
        Assert.False(TryMap(path, p => Hit(p), out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void InvalidVertexOverrideIsRejectedWithoutProbing()
    {
        Vec2[] path = { new(0, 0), new(20, 0) };
        bool called = false;
        SurfaceQueryResult Query(Vec2 p) { called = true; return Hit(p); }
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [5] = new(path[0], new Vec3(0, 0, 0), 1),
        };

        Assert.False(SurfacePathApproximation.TryMapRoute(path, Query, overrides,
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("outside the planar path", failure);
        Assert.False(called);
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SurfacePathApproximation.TryMapRoute(
            null!, p => Hit(p), null, out _, out _, out _));
        Assert.Throws<ArgumentNullException>(() => SurfacePathApproximation.TryMapRoute(
            Array.Empty<Vec2>(), null!, null, out _, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfacePathApproximation.TryMapRoute(
            Array.Empty<Vec2>(), p => Hit(p), null, out _, out _, out _, double.NaN));
    }

    // ---------- 순서/재방문 ----------

    [Fact]
    public void ProbeOrderDiffersFromPathOrderButOutputStaysPathOrdered()
    {
        Vec2[] path = { new(0, 0), new(8, 0), new(8, 8), new(0, 8) };
        Assert.True(TryMap(path, p => HitOrMiss(p, q => Math.Abs(q.X - 8) < 0.3 && q.Y > 3 && q.Y < 5),
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure), failure);

        Assert.True(report.Used);
        // 경로를 따라 전진하는 순서만 허용한다: 둘째 구간은 X 고정, 마지막 구간은 X 감소.
        int turn = mapped.FindIndex(s => s.DatumPoint == path[1]);
        Assert.True(turn > 0);
        for (int i = 1; i <= turn; i++)
            Assert.True(mapped[i - 1].DatumPoint.X <= mapped[i].DatumPoint.X);
        for (int i = turn + 1; i < mapped.Count; i++)
            Assert.True(mapped[i - 1].DatumPoint.X >= mapped[i].DatumPoint.X);
        Assert.Equal(new Vec2(0, 8), mapped[^1].DatumPoint);
    }

    [Fact]
    public void SameTwoDimensionalCoordinatesAtDifferentDepthKeepSeparateSamples()
    {
        // 같은 uv를 두 번 방문하는 왕복 경로. 각 방문은 개별 Hit로 남아야 한다.
        Vec2[] path = { new(0, 0), new(10, 0), new(0, 0) };
        SurfaceQueryResult Query(Vec2 p) => p.X > 4.5 && p.X < 5.5
            ? SurfaceQueryResult.FromMiss("hole")
            : SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, new Vec3(p.X, p.Y, p.X), 3));

        Assert.True(SurfacePathApproximation.TryMapRoute(path, Query, null,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure), failure);

        Assert.Equal(2, report.BridgeCount);
        Assert.Equal(new Vec2(0, 0), mapped[0].DatumPoint);
        Assert.Equal(new Vec2(0, 0), mapped[^1].DatumPoint);
        Assert.Contains(mapped, s => s.DatumPoint == new Vec2(10, 0));
        Assert.True(mapped.Count >= 4);
    }

    // ---------- strict는 그대로 거부 ----------

    [Fact]
    public void StrictApiStillRejectsTheSameHole()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        SurfaceMappedSample? Strict(Vec2 p) => Math.Abs(p.X - 20) < 0.4
            ? null
            : new SurfaceMappedSample(p, new Vec3(p.X, p.Y, 0), 7);

        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, Strict,
            out List<SurfaceMappedSample> strict, out string strictFailure));
        Assert.Empty(strict);
        Assert.NotEmpty(strictFailure);

        Assert.True(TryMap(path, p => HitOrMiss(p, q => Math.Abs(q.X - 20) < 0.4),
            out _, out SurfaceApproximationReport report, out _));
        Assert.True(report.Used);
    }

    // ---------- strict를 이미 실행한 호출자용 진입점 ----------

    [Fact]
    public void AfterStrictFailureDoesNotRepeatTheStrictPass()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        bool Missing(Vec2 q) => Math.Abs(q.X - 20) < 0.4;

        int combinedCalls = 0;
        Assert.True(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            combinedCalls++;
            return HitOrMiss(p, Missing);
        }, null, out _, out SurfaceApproximationReport combinedReport, out string combinedFailure),
            combinedFailure);

        int calls = 0;
        var recorder = new SurfaceProbeRecorder(p =>
        {
            calls++;
            return HitOrMiss(p, Missing);
        });
        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, recorder.HitProbe, null,
            out _, out _));
        int strictPhase = calls;
        Assert.True(recorder.SawMiss);
        Assert.False(recorder.SawFailure);
        Assert.True(recorder.AllowsApproximationRetry);

        Assert.True(SurfacePathApproximation.TryMapRouteAfterStrictFailure(path, recorder, null,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport skippedReport,
            out string skippedFailure), skippedFailure);

        int walkPhase = calls - strictPhase;
        Assert.True(combinedReport.Used);
        Assert.Equal(combinedReport.BridgeCount, skippedReport.BridgeCount);
        Assert.True(strictPhase > 0);
        Assert.True(walkPhase > 0);
        // 합성 호출 1회(strict+근사)보다 적다 = strict를 다시 실행하지 않았다.
        Assert.True(walkPhase < combinedCalls,
            "strict 재실행 금지 (combined=" + combinedCalls + ", strict=" + strictPhase
            + ", walk=" + walkPhase + ")");
        Assert.Equal(new Vec2(40, 0), mapped[^1].DatumPoint);
    }

    [Fact]
    public void AfterStrictFailureStillRejectsAMissingVertex()
    {
        Vec2[] path = { new(0, 0), new(20, 0), new(40, 0) };
        var recorder = new SurfaceProbeRecorder(p => HitOrMiss(p, q => Math.Abs(q.X - 20) < 3));
        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, recorder.HitProbe, null, out _, out _));
        Assert.False(SurfacePathApproximation.TryMapRouteAfterStrictFailure(path, recorder, null,
            out List<SurfaceMappedSample> mapped, out _, out string failure));
        Assert.Empty(mapped);
        Assert.Contains("꼭짓점", failure);
    }

    // ---------- strict가 관측한 하드 오류는 재시도로 지우지 않는다 ----------

    [Fact]
    public void TransientFailureThenHitIsNotRetried()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        int calls = 0;
        bool failedOnce = false;
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            calls++;
            if (!failedOnce) { failedOnce = true; return SurfaceQueryResult.FromFailure("kernel"); }
            return Hit(p);
        }, null, out List<SurfaceMappedSample> mapped, out _, out string failure));

        Assert.Empty(mapped);
        Assert.Contains("재시도 불가", failure);
        // 하드 실패를 관측했으므로 근사 재조회가 추가로 실행되지 않는다.
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvalidSampleThenHitIsNotRetried()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        int calls = 0;
        bool first = true;
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            calls++;
            if (first)
            {
                first = false;
                return SurfaceQueryResult.FromHit(new SurfaceMappedSample(p, new Vec3(double.NaN, 0, 0), 4));
            }
            return Hit(p);
        }, null, out List<SurfaceMappedSample> mapped, out _, out string failure));

        Assert.Empty(mapped);
        Assert.Contains("재시도 불가", failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DepthFailureWithoutAnyMissIsNotRetried()
    {
        Vec2[] path = { new(-2, 0), new(-1, 0), new(2, 0) };
        int calls = 0;
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            calls++;
            return SurfaceQueryResult.FromHit(new SurfaceMappedSample(p,
                new Vec3(p.X, p.Y, p.X < 0 ? 0 : 10), 5));
        }, null, out List<SurfaceMappedSample> mapped, out _, out string failure));

        Assert.Empty(mapped);
        Assert.Contains("재시도 불가", failure);
        int depthCalls = calls;
        Assert.True(depthCalls > 1);
        // 깊이 한도 실패는 근사 재조회 대상이 아니다(조회가 더 늘지 않는다).
        Assert.DoesNotContain("근사 연결", failure);
    }

    [Fact]
    public void ProbeBudgetWithoutAnyMissIsNotRetried()
    {
        Vec2[] path = { new(0, 0), new(5000, 0) };
        int calls = 0;
        Assert.False(SurfacePathApproximation.TryMapRoute(path, p =>
        {
            calls++;
            return Hit(p);
        }, null, out List<SurfaceMappedSample> mapped, out _, out string failure, maxProbes: 20));

        Assert.Empty(mapped);
        Assert.Contains("재시도 불가", failure);
        // 깊이/예산 실패는 근사 재조회 대상이 아니므로 조회가 예산을 넘지 않는다.
        Assert.True(calls <= 20, "예산을 넘는 조회가 실행되면 안 됩니다: " + calls);
    }

    [Fact]
    public void RecorderReportsObservedOutcomes()
    {
        var recorder = new SurfaceProbeRecorder(p => p.X < 0
            ? SurfaceQueryResult.FromMiss("hole")
            : p.X > 0 ? SurfaceQueryResult.FromFailure("kernel") : Hit(p));
        Assert.False(recorder.SawMiss);
        Assert.False(recorder.AllowsApproximationRetry);

        recorder.Query(new Vec2(-1, 0));
        Assert.True(recorder.SawMiss);
        Assert.False(recorder.SawFailure);
        Assert.True(recorder.AllowsApproximationRetry);

        recorder.Query(new Vec2(1, 0));
        Assert.True(recorder.SawFailure);
        Assert.Equal("kernel", recorder.FailureReason);
        Assert.False(recorder.AllowsApproximationRetry);
        Assert.Equal(2, recorder.QueryCount);
        Assert.Contains("하드 오류", recorder.Describe());
    }

    [Fact]
    public void RecorderWithoutMissDoesNotAllowRetry()
    {
        var recorder = new SurfaceProbeRecorder(p => Hit(p));
        Assert.False(recorder.AllowsApproximationRetry);
        Assert.Contains("단순 Miss 미관측", recorder.Describe());
    }

    [Fact]
    public void AfterStrictFailureRefusesWhenTheRecorderSawAHardFailure()
    {
        Vec2[] path = { new(0, 0), new(40, 0) };
        var recorder = new SurfaceProbeRecorder(p => SurfaceQueryResult.FromFailure("stale"));
        Assert.False(SurfacePathApproximation.TryMapRouteAfterStrictFailure(path, recorder, null,
            out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure));
        Assert.Empty(mapped);
        Assert.False(report.Used);
        Assert.Contains("재시도 불가", failure);
    }
}
