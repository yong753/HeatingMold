using HeatingMold.App;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>
/// 제한 근사 연결의 집계/게시 규칙. 집계는 "이번 준비에서 실행한 근사"만 세고,
/// 네이티브 커밋이 성공하기 전에는 게시하지 않으며, 근사 표본은 strict 캐시에 넣지 않는다.
/// </summary>
public class RouteApproximationPublicationTests
{
    private static SurfaceApproximationBridge Bridge(double advance, double gap3D = 1.0) =>
        new(0, new Vec2(0, 0), new Vec2(advance, 0), advance, gap3D);

    private static SurfaceApproximationReport Report(params SurfaceApproximationBridge[] bridges) =>
        new(bridges);

    [Fact]
    public void Stats_AccumulateBridgesTotalsAndMaximum()
    {
        var stats = new RouteApproximationStats();
        stats.Add(Report(Bridge(2.0, 0.5), Bridge(6.0, 3.0)));
        stats.Add(Report(Bridge(1.5, 0.25)));

        Assert.Equal(3, stats.BridgeCount);
        Assert.Equal(9.5, stats.TotalAdvanceMm, 6);
        Assert.Equal(6.0, stats.MaxAdvanceMm, 6);
        Assert.Equal(3.0, stats.MaxGap3DMm, 6);
    }

    [Fact]
    public void Stats_EmptyReportIsNotCountedAndDescribeStaysEmpty()
    {
        var stats = new RouteApproximationStats();
        stats.Add(SurfaceApproximationReport.None);
        Assert.Equal(0, stats.BridgeCount);
        // 근사가 없을 때 "0곳"이라고 단정하지 않는다(캐시/retain으로 매핑하지 않았을 수 있다).
        Assert.Equal(string.Empty, stats.Describe());
    }

    [Fact]
    public void Stats_DescribeMarksTheValueAsThisPreparationOnly()
    {
        var stats = new RouteApproximationStats();
        stats.Add(Report(Bridge(4.0, 1.0)));
        string text = stats.Describe();
        Assert.Contains("이번 준비", text);
        Assert.Contains("1곳", text);
        Assert.Contains("총", text);
        Assert.Contains("최대", text);
    }

    [Fact]
    public void Publication_IsInvisibleUntilCommitSucceeds()
    {
        var publication = new RouteApproximationPublication();
        publication.Begin();
        publication.Staging.Add(Report(Bridge(5.0)));

        Assert.Equal(0, publication.Committed.BridgeCount);
        Assert.Equal(string.Empty, publication.Note());

        publication.PublishOnCommit();
        Assert.Equal(1, publication.Committed.BridgeCount);
        Assert.NotEqual(string.Empty, publication.Note());
        Assert.Null(publication.Staging);
    }

    [Fact]
    public void Publication_FailedPreparationKeepsThePreviousCommittedValue()
    {
        var publication = new RouteApproximationPublication();
        publication.Begin();
        publication.Staging.Add(Report(Bridge(5.0)));
        publication.PublishOnCommit();

        publication.Begin();
        publication.Staging.Add(Report(Bridge(2.0), Bridge(3.0)));
        publication.AbandonOnFailure();

        // 실패한 준비가 이전 성공 값을 덮어쓰지 않는다.
        Assert.Equal(1, publication.Committed.BridgeCount);
        Assert.Null(publication.Staging);

        // 커밋 없이 다시 준비해도 게시 전에는 이전 값이 유지된다.
        publication.Begin();
        publication.Staging.Add(Report(Bridge(1.0)));
        Assert.Equal(1, publication.Committed.BridgeCount);
    }

    [Fact]
    public void Publication_ResetClearsCommittedAndStaging()
    {
        var publication = new RouteApproximationPublication();
        publication.Begin();
        publication.Staging.Add(Report(Bridge(5.0)));
        publication.PublishOnCommit();

        publication.Reset();

        Assert.Equal(0, publication.Committed.BridgeCount);
        Assert.Equal(string.Empty, publication.Note());
        Assert.Null(publication.Staging);
    }

    [Fact]
    public void OnlyStrictSamplesEnterTheStrictSampleCache()
    {
        Assert.True(RouteSampleCachePolicy.CanCache(RouteSampleSource.Strict));
        Assert.False(RouteSampleCachePolicy.CanCache(RouteSampleSource.PlanarTurn));
        Assert.False(RouteSampleCachePolicy.CanCache(RouteSampleSource.Approximation));
    }
}
