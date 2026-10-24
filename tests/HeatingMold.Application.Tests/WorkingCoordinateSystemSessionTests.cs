using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Application.Tests;

/// <summary>작업 좌표계 응용 세션이 preview와 확정값을 분리하는지 검증합니다.</summary>
public sealed class WorkingCoordinateSystemSessionTests
{
    /// <summary>preview 실패나 취소가 기존 확정 좌표계를 바꾸지 않습니다.</summary>
    [Fact]
    public void PreviewIsSeparateAndStaleConfirmationKeepsPreviousValue()
    {
        var session = new WorkingCoordinateSystemSession();
        WorkingCoordinateSystem front = CreateCoordinateSystem(1, 10, new Vec3(0, 0, 1));
        session.SetPreview(front);
        session.Confirm(new ModelRevision(10));

        WorkingCoordinateSystem back = CreateCoordinateSystem(2, 10, new Vec3(0, 0, -1));
        session.SetPreview(back);
        Assert.Same(front, session.Confirmed);
        Assert.Equal(WorkingCoordinateSystemState.Preview, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Confirm(new ModelRevision(11)));
        Assert.Same(front, session.Confirmed);

        session.CancelPreview();
        Assert.Same(front, session.Confirmed);
        session.Invalidate();
        Assert.Equal(WorkingCoordinateSystemState.Unconfirmed, session.State);
    }

    private static WorkingCoordinateSystem CreateCoordinateSystem(long id, long revision, Vec3 normal) =>
        new(new WorkingCoordinateSystemId(id), new ModelRevision(revision),
            new ProjectionFrame(new Vec3(0, 0, 101), normal, new Vec3(1, 0, 0)));
}
