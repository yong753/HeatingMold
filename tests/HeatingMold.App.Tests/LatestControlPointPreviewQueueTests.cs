using HeatingMold.App.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>드래그 미리보기는 최신 커서만 처리하고 취소된 세대 응답을 버리는지 확인합니다.</summary>
public sealed class LatestControlPointPreviewQueueTests
{
    /// <summary>연속된 커서 이동은 마지막 위치만 꺼내고 같은 위치는 중복 예약하지 않습니다.</summary>
    [Fact]
    public void Queue_ReplacesPendingPointAndSuppressesDuplicates()
    {
        var queue = new LatestControlPointPreviewQueue();
        long generation = queue.Begin();

        Assert.True(queue.Queue(10, 20));
        Assert.True(queue.Queue(30, 40));
        Assert.False(queue.Queue(30, 40));

        Assert.True(queue.TryTake(generation, out (int X, int Y) point));
        Assert.Equal((30, 40), point);
        Assert.False(queue.Queue(30, 40));
        Assert.False(queue.TryTake(generation, out _));
    }

    /// <summary>취소 뒤 이전 세대 요청은 현재 미리보기에 들어오지 않습니다.</summary>
    [Fact]
    public void Cancel_InvalidatesQueuedResponseFromPreviousGeneration()
    {
        var queue = new LatestControlPointPreviewQueue();
        long previousGeneration = queue.Begin();
        Assert.True(queue.Queue(10, 20));

        queue.Cancel();
        long currentGeneration = queue.Begin();

        Assert.False(queue.IsCurrent(previousGeneration));
        Assert.False(queue.TryTake(previousGeneration, out _));
        Assert.False(queue.TryTake(currentGeneration, out _));
        Assert.True(queue.IsCurrent(currentGeneration));
    }
}
