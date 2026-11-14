using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 평면 턴 외곽 돌출 5mm 검증기. 고정 표본 최댓값을 그대로 성공으로 삼지 않고,
/// 전 구간 상계가 한도 이하임을 증명할 때만 승인해야 한다.
/// </summary>
public class PlanarExteriorTurnTests
{
    private static readonly Vec2 EdgeA = new(-10, 0);
    private static readonly Vec2 EdgeB = new(10, 0);

    /// <summary>경계 = 직선 (-10,0)-(10,0), 내부 = y &lt; 0.</summary>
    private static PlanarExteriorTurnProbe StraightEdgeProbe(bool outerBoundary = true, bool straight = true) =>
        StraightEdgeProbe(EdgeA, EdgeB, outerBoundary, straight);

    private static PlanarExteriorTurnProbe StraightEdgeProbe(
        Vec2 edgeA, Vec2 edgeB, bool outerBoundary = true, bool straight = true)
    {
        return p =>
        {
            if (p.Y < 0)
            {
                return PlanarExteriorProbeSample.Inside();
            }

            double distance = PlanarExteriorTurnValidator.DistanceToSegment(p, edgeA, edgeB);
            return PlanarExteriorProbeSample.Outside(distance, edgeA, edgeB, straight, outerBoundary, distance);
        };
    }

    [Fact]
    public void PolicyAppliesOnlyToTurnConnectors()
    {
        Assert.Equal(5.0, PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm);
        Assert.True(PlanarExteriorTurnPolicy.AppliesTo(SerpentineRoutePieceKind.TurnConnector));
        Assert.False(PlanarExteriorTurnPolicy.AppliesTo(SerpentineRoutePieceKind.HeatingRun));
        Assert.False(PlanarExteriorTurnPolicy.AppliesTo(SerpentineRoutePieceKind.FixedConnector));
    }

    [Fact]
    public void SegmentOneMillimetreOutsideTheEdge_IsValid()
    {
        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), StraightEdgeProbe());

        Assert.Equal(PlanarExteriorTurnStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.Equal(1.0, result.ObservedMaximumMm, 6);
        Assert.Equal(1.0, result.CertifiedUpperBoundMm, 6);
        Assert.True(result.ProbeCount > 2);
    }

    [Fact]
    public void ExactlyAtTheLimit_IsValidBecauseTheStraightEdgeBoundIsConvex()
    {
        // 5.000000 mm는 '이하'이므로 승인해야 한다. 표본 Lipschitz 상계만 쓰면
        // 5 + 구간길이/2 라서 영원히 증명할 수 없지만, 직선 외곽 요소까지의 거리는
        // 볼록이므로 구간 최댓값이 양 끝점에 있다.
        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 5), new Vec2(9, 5), StraightEdgeProbe());

        Assert.Equal(PlanarExteriorTurnStatus.Valid, result.Status);
        Assert.Equal(5.0, result.CertifiedUpperBoundMm, 9);
        Assert.True(result.CertifiedUpperBoundMm <= PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm);
    }

    [Fact]
    public void JustOverTheLimit_IsRejected()
    {
        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 5.0001), new Vec2(9, 5.0001), StraightEdgeProbe());

        Assert.Equal(PlanarExteriorTurnStatus.OverLimit, result.Status);
        Assert.False(result.IsValid);
        Assert.True(result.ObservedMaximumMm > PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm);
    }

    [Fact]
    public void CurvedBoundaryAtTheLimit_CannotBeCertified()
    {
        // 곡선 외곽은 볼록 상계를 쓸 수 없다. 고정 표본이 모두 한도 이내여도
        // 전 구간 증명이 불가능하면 '초과'가 아니라 '확인 불가'로 거부한다.
        PlanarExteriorTurnProbe probe = p =>
            PlanarExteriorProbeSample.Outside(5.0, EdgeA, EdgeB, false, true, 5.0);

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 5), new Vec2(9, 5), probe);

        Assert.Equal(PlanarExteriorTurnStatus.Indeterminate, result.Status);
        Assert.False(result.IsValid);
        Assert.True(result.ProbeCount <= PlanarExteriorTurnPolicy.MaxProbeCount);
        Assert.Equal(double.PositiveInfinity, result.CertifiedUpperBoundMm);
    }

    [Fact]
    public void NonOuterWitness_IsNotUsedForCertification()
    {
        // 내부 구멍/제외변은 '외곽까지의 거리'의 상계가 아니므로 증거로 쓰면 안 된다.
        PlanarExteriorTurnProbe probe = p =>
            PlanarExteriorProbeSample.Outside(5.0, EdgeA, EdgeB, true, false, 5.0);

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 5), new Vec2(9, 5), probe);

        Assert.Equal(PlanarExteriorTurnStatus.Indeterminate, result.Status);
    }

    [Fact]
    public void PeakOnlyInTheMiddleIsDetected()
    {
        // 양 끝은 한도 이내지만 중간만 7 mm 돌출한다. 표본이 그 구간을 지나가면
        // 관측 최댓값으로 초과를 판정해야 한다.
        PlanarExteriorTurnProbe probe = p =>
        {
            double distance = Math.Abs(p.X - 5) < 1.5 ? 7.0 : 1.0;
            return PlanarExteriorProbeSample.Outside(distance, EdgeA, EdgeB, false, true, distance);
        };

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe);

        Assert.Equal(PlanarExteriorTurnStatus.OverLimit, result.Status);
        Assert.Equal(7.0, result.ObservedMaximumMm, 6);
    }

    [Fact]
    public void InteriorGapAnywhereRejectsTheTurn()
    {
        PlanarExteriorTurnProbe probe = p =>
            Math.Abs(p.X - 5) < 0.2
                ? PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.InteriorGap)
                : PlanarExteriorProbeSample.Outside(1.0, EdgeA, EdgeB, true, true, 1.0);

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe);

        Assert.Equal(PlanarExteriorTurnStatus.InteriorGap, result.Status);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void QueryFailureAnywhereRejectsTheTurn()
    {
        PlanarExteriorTurnProbe probe = p =>
            Math.Abs(p.X - 3) < 0.2
                ? PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed)
                : PlanarExteriorProbeSample.Outside(1.0, EdgeA, EdgeB, true, true, 1.0);

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe);

        Assert.Equal(PlanarExteriorTurnStatus.QueryFailed, result.Status);
    }

    [Fact]
    public void AmbiguousBoundaryRejectsTheTurn()
    {
        PlanarExteriorTurnProbe probe = p =>
            Math.Abs(p.X - 7) < 0.2
                ? PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.Ambiguous)
                : PlanarExteriorProbeSample.Outside(1.0, EdgeA, EdgeB, true, true, 1.0);

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe);

        Assert.Equal(PlanarExteriorTurnStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void ThrowingProbeIsReportedAsQueryFailure()
    {
        PlanarExteriorTurnProbe probe = _ => throw new InvalidOperationException("native failure");

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe);

        Assert.Equal(PlanarExteriorTurnStatus.QueryFailed, result.Status);
        Assert.Contains("native failure", result.Detail);
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        var probe = StraightEdgeProbe();

        Assert.Equal(PlanarExteriorTurnStatus.InvalidInput,
            PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(1, 1), probe).Status);
        Assert.Equal(PlanarExteriorTurnStatus.InvalidInput,
            PlanarExteriorTurnValidator.Validate(new Vec2(double.NaN, 1), new Vec2(9, 1), probe).Status);
        Assert.Equal(PlanarExteriorTurnStatus.InvalidInput,
            PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), (PlanarExteriorTurnProbe)null).Status);
        Assert.Equal(PlanarExteriorTurnStatus.InvalidInput,
            PlanarExteriorTurnValidator.Validate(new Vec2(1, 1), new Vec2(9, 1), probe, 0).Status);
    }

    [Fact]
    public void InsideSamplesContributeZeroDistance()
    {
        var result = PlanarExteriorTurnValidator.Validate(new Vec2(1, -1), new Vec2(9, 1), StraightEdgeProbe());

        Assert.Equal(PlanarExteriorTurnStatus.Valid, result.Status);
        Assert.Equal(1.0, result.ObservedMaximumMm, 6);
    }

    [Fact]
    public void CornerShortcutFromTheReportedTurn_IsCertifiedWithinTheLimit()
    {
        // 사용자 보고 값: 2 mm 간격에서 zero-based 103번 TurnConnector.
        // 원본 외곽은 같은 평면(face 65)의 두 직선 edge다.
        Vec2 a = new(-508.90905754511823, 69.82782294098945);
        Vec2 b = new(-505.02278625211625, 69.8830067234694);
        Vec2 c = new(-491.78440197801024, 73.75614341718381);
        Vec2 p = new(-506.513917148924, 69.8618331519777);
        Vec2 q = new(-504.213917148924, 70.119656494251);

        Vec2 witnessStart = default;
        Vec2 witnessEnd = default;

        PlanarExteriorTurnProbe probe = point =>
        {
            double d1 = PlanarExteriorTurnValidator.DistanceToSegment(point, a, b);
            double d2 = PlanarExteriorTurnValidator.DistanceToSegment(point, b, c);
            double distance = Math.Min(d1, d2);
            witnessStart = d1 <= d2 ? a : b;
            witnessEnd = d1 <= d2 ? b : c;
            return PlanarExteriorProbeSample.Outside(distance, witnessStart, witnessEnd, true, true, distance);
        };

        var result = PlanarExteriorTurnValidator.Validate(p, q, probe);

        Assert.True(result.IsValid, result.Status + ": " + result.Detail);
        Assert.True(result.CertifiedUpperBoundMm <= PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm);
        Assert.True(result.ObservedMaximumMm <= result.CertifiedUpperBoundMm + 1e-9);
        // 두 끝점은 각각 원본 직선 위에 있으므로 돌출은 0에서 시작한다.
        Assert.True(result.ObservedMaximumMm < 1.0, "관측 돌출: " + result.ObservedMaximumMm);
    }

    [Fact]
    public void LongConnectorDoesNotExhaustTheProbeBudget()
    {
        // 4 mm 떨어진 평행 구간은 양 끝점만으로 증명되므로 표본을 늘리지 않는다.
        var longEdge = StraightEdgeProbe(new Vec2(-500, 0), new Vec2(500, 0));

        var result = PlanarExteriorTurnValidator.Validate(new Vec2(-400, 4), new Vec2(400, 4), longEdge);

        Assert.Equal(PlanarExteriorTurnStatus.Valid, result.Status);
        Assert.True(result.ProbeCount <= 4096, "표본 수: " + result.ProbeCount);
        Assert.Equal(4.0, result.CertifiedUpperBoundMm, 6);
    }
}
