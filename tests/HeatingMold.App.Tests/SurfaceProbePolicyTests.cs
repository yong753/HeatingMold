using HeatingMold.App;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>
/// 선택 면 → 전역 표면 → 정확 경계 후보의 typed 판정. 과거 nullable 규칙처럼 "유효한 Hit가
/// 하나라도 있으면 성공"을 보존하고, Hit가 전혀 없을 때만 하드 상태를 Failure로 판정한다.
/// 불명확한 비-Miss는 단순 Miss(근사 허용)로 취급하지 않는다.
/// </summary>
public class SurfaceProbePolicyTests
{
    private static SurfaceMappedSample Sample(int face, double z = 0, double x = 1, double y = 2) =>
        new(new Vec2(x, y), new Vec3(x, y, z), face);

    private static readonly SurfaceProbeOutcome Miss = SurfaceProbeOutcome.Miss("miss");
    private static readonly SurfaceProbeOutcome Hard = SurfaceProbeOutcome.Hard("GeometryFailure");

    [Fact]
    public void SelectedHardWithGlobalHit_PreservesThePreviousSuccess()
    {
        var global = SurfaceMappedSampleAt(20);
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Hard,
            SurfaceProbeOutcome.FromHit(global), SurfaceProbePolicy.NotRun);

        Assert.Equal(SurfaceQueryOutcome.Hit, result.Outcome);
        Assert.Equal(global, result.Sample);
    }

    [Fact]
    public void SelectedMissGlobalHardBoundaryHit_ReturnsTheBoundaryHit()
    {
        var boundary = SurfaceMappedSampleAt(30);
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Miss, Hard,
            SurfaceProbeOutcome.FromHit(boundary));

        Assert.Equal(SurfaceQueryOutcome.Hit, result.Outcome);
        Assert.Equal(boundary, result.Sample);
    }

    [Fact]
    public void PriorityIsSelectedThenGlobalThenBoundary()
    {
        var selected = SurfaceMappedSampleAt(10);
        var global = SurfaceMappedSampleAt(20);
        var boundary = SurfaceMappedSampleAt(30);
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(
            SurfaceProbeOutcome.FromHit(selected),
            SurfaceProbeOutcome.FromHit(global),
            SurfaceProbeOutcome.FromHit(boundary));

        Assert.Equal(selected, result.Sample);
    }

    [Fact]
    public void HardWithoutAnyHit_IsFailureAndNotApproximable()
    {
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Hard, Hard, Miss);

        Assert.Equal(SurfaceQueryOutcome.Failure, result.Outcome);
        Assert.Contains("선택 면", result.Reason);
        Assert.Contains("전역 표면", result.Reason);
    }

    [Fact]
    public void AmbiguousNonMissIsNotTreatedAsPlainMiss()
    {
        // Hit인데 3D 점이 없는 상태는 단순 Miss가 아니다.
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(
            SurfaceProbeOutcome.Hard("Hit인데 3D 점이 없습니다"), Miss, Miss);

        Assert.Equal(SurfaceQueryOutcome.Failure, result.Outcome);
    }

    [Fact]
    public void BoundaryAmbiguityIsHard()
    {
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Miss, Miss,
            SurfaceProbeOutcome.Hard(nameof(ExitBoundaryFailureReason.MultipleIntersections)));

        Assert.Equal(SurfaceQueryOutcome.Failure, result.Outcome);
        Assert.Contains("경계", result.Reason);
    }

    [Fact]
    public void PureMissRemainsApproximable()
    {
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Miss, Miss,
            SurfaceProbeOutcome.Miss(nameof(ExitBoundaryFailureReason.NoIntersection)));

        Assert.Equal(SurfaceQueryOutcome.Miss, result.Outcome);
    }

    [Fact]
    public void NotRunPlaceholderNeitherHitsNorHardens()
    {
        Assert.Equal(SurfaceCandidateState.Miss, SurfaceProbePolicy.NotRun.State);
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Miss, SurfaceProbePolicy.NotRun, Miss);
        Assert.Equal(SurfaceQueryOutcome.Miss, result.Outcome);
    }

    private static SurfaceMappedSample SurfaceMappedSampleAt(int face) => Sample(face);

    // ---------- 공통 factory의 malformed 판정 ----------

    private static readonly Vec2 Datum = new(1, 2);

    private static SurfaceMappedSample Valid(double z = 0, int face = 7) =>
        new(Datum, new Vec3(Datum.X, Datum.Y, z), face);

    [Fact]
    public void FactoryAcceptsValidSampleBitwiseIncludingNegativeZero()
    {
        var sample = new SurfaceMappedSample(new Vec2(-0.0, 0.0), new Vec3(-0.0, 0.0, -0.0), 7);
        SurfaceProbeOutcome outcome = SurfaceProbeOutcome.FromHit(sample);

        Assert.Equal(SurfaceCandidateState.Hit, outcome.State);
        Assert.Equal(BitConverter.DoubleToInt64Bits(sample.DatumPoint.X),
            BitConverter.DoubleToInt64Bits(outcome.Sample.DatumPoint.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sample.DatumPoint.Y),
            BitConverter.DoubleToInt64Bits(outcome.Sample.DatumPoint.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sample.Point.X),
            BitConverter.DoubleToInt64Bits(outcome.Sample.Point.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sample.Point.Y),
            BitConverter.DoubleToInt64Bits(outcome.Sample.Point.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sample.Point.Z),
            BitConverter.DoubleToInt64Bits(outcome.Sample.Point.Z));
        Assert.Equal(7, outcome.Sample.FaceId);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FactoryRejectsNonfiniteDatumComponent(double invalid)
    {
        Assert.Equal(SurfaceCandidateState.Hard,
            SurfaceProbeOutcome.FromHit(new SurfaceMappedSample(new Vec2(invalid, 2),
                new Vec3(1, 2, 0), 7)).State);
        Assert.Equal(SurfaceCandidateState.Hard,
            SurfaceProbeOutcome.FromHit(new SurfaceMappedSample(new Vec2(1, invalid),
                new Vec3(1, 2, 0), 7)).State);
    }

    [Theory]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(1, double.NegativeInfinity)]
    [InlineData(2, double.NaN)]
    [InlineData(2, double.PositiveInfinity)]
    [InlineData(2, double.NegativeInfinity)]
    public void FactoryRejectsNonfinite3DComponent(int axis, double invalid)
    {
        var point = new Vec3(axis == 0 ? invalid : 1,
            axis == 1 ? invalid : 2,
            axis == 2 ? invalid : 0);
        Assert.Equal(SurfaceCandidateState.Hard,
            SurfaceProbeOutcome.FromHit(new SurfaceMappedSample(Datum, point, 7)).State);
    }

    [Fact]
    public void FactoryRejectsMissingSupportFace()
    {
        Assert.Equal(SurfaceCandidateState.Hard, SurfaceProbeOutcome.FromHit(Valid(0, -1)).State);
        Assert.Equal(SurfaceCandidateState.Hit, SurfaceProbeOutcome.FromHit(Valid(0, 0)).State);
    }

    [Fact]
    public void FactoryRejectsDatumDifferentFromTheRequestedPoint()
    {
        var sample = new SurfaceMappedSample(new Vec2(5, 5), new Vec3(1, 2, 0), 7);
        Assert.Equal(SurfaceCandidateState.Hit, SurfaceProbeOutcome.FromHit(sample).State);
        Assert.Equal(SurfaceCandidateState.Hard, SurfaceProbeOutcome.FromHit(sample, Datum).State);

        var onDatum = Valid();
        SurfaceProbeOutcome accepted = SurfaceProbeOutcome.FromHit(onDatum, Datum);
        Assert.Equal(SurfaceCandidateState.Hit, accepted.State);
        Assert.Equal(onDatum, accepted.Sample);
    }

    [Fact]
    public void MalformedSelectedCandidateFallsBackToAValidGlobalHit()
    {
        var malformed = new SurfaceMappedSample(Datum, new Vec3(double.NaN, Datum.Y, 0), 7);
        var global = new SurfaceMappedSample(Datum, new Vec3(Datum.X, Datum.Y, 3), 21);
        SurfaceProbeOutcome selected = SurfaceProbeOutcome.FromHit(malformed, Datum);
        SurfaceProbeOutcome globalOutcome = SurfaceProbeOutcome.FromHit(global, Datum);
        Assert.Equal(SurfaceCandidateState.Hard, selected.State);

        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(selected, globalOutcome,
            SurfaceProbePolicy.NotRun);

        Assert.Equal(SurfaceQueryOutcome.Hit, result.Outcome);
        Assert.Equal(global, result.Sample);
        Assert.Equal(21, result.Sample.FaceId);
    }

    [Fact]
    public void MalformedGlobalCandidateFallsBackToAValidBoundaryHit()
    {
        var global = new SurfaceMappedSample(Datum, new Vec3(Datum.X, Datum.Y, 0), -1);
        var boundary = new SurfaceMappedSample(Datum, new Vec3(Datum.X, Datum.Y, 1), 33);
        SurfaceProbeOutcome globalOutcome = SurfaceProbeOutcome.FromHit(global, Datum);
        SurfaceProbeOutcome boundaryOutcome = SurfaceProbeOutcome.FromHit(boundary, Datum);
        Assert.Equal(SurfaceCandidateState.Hard, globalOutcome.State);

        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(Miss, globalOutcome, boundaryOutcome);

        Assert.Equal(SurfaceQueryOutcome.Hit, result.Outcome);
        Assert.Equal(boundary, result.Sample);
    }

    [Fact]
    public void AllCandidatesMalformedOrMissingIsFailureNotApproximable()
    {
        SurfaceProbeOutcome malformed = SurfaceProbeOutcome.FromHit(
            new SurfaceMappedSample(Datum, new Vec3(double.PositiveInfinity, Datum.Y, 0), 7), Datum);
        SurfaceProbeOutcome noFace = SurfaceProbeOutcome.FromHit(Valid(0, -1), Datum);

        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(malformed, noFace, Miss);

        Assert.Equal(SurfaceQueryOutcome.Failure, result.Outcome);
        Assert.Contains("유한", result.Reason);
        Assert.Contains("지지 Face", result.Reason);
    }

    [Fact]
    public void MalformedSelectedStillPrefersAValidSelectedCandidateWhenPresent()
    {
        var valid = Valid(5, 11);
        SurfaceQueryResult result = SurfaceProbePolicy.Resolve(
            SurfaceProbeOutcome.FromHit(valid, Datum),
            SurfaceProbeOutcome.FromHit(Valid(9, 12), Datum),
            SurfaceProbePolicy.NotRun);
        Assert.Equal(11, result.Sample.FaceId);
    }
}
