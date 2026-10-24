namespace HeatingMold.App.Geometry;

/// <summary>드래그 세대별 커서 요청을 최신 위치 하나로 제한합니다.</summary>
internal sealed class LatestControlPointPreviewQueue
{
    private (int X, int Y)? _pendingPoint;
    private (int X, int Y)? _lastTakenPoint;

    /// <summary>현재 드래그와 예약 응답을 구분하는 세대 번호입니다.</summary>
    internal long Generation { get; private set; }

    /// <summary>새 드래그 세대를 열고 이전 커서 요청을 폐기합니다.</summary>
    internal long Begin()
    {
        Generation++;
        _pendingPoint = null;
        _lastTakenPoint = null;
        return Generation;
    }

    /// <summary>중복 위치를 생략하고 최신 커서 위치만 예약합니다.</summary>
    internal bool Queue(int x, int y)
    {
        var point = (X: x, Y: y);
        if (_pendingPoint == point || (_pendingPoint is null && _lastTakenPoint == point))
            return false;

        _pendingPoint = point;
        return true;
    }

    /// <summary>현재 세대의 최신 요청을 꺼내고 이전 위치로 기록합니다.</summary>
    internal bool TryTake(long generation, out (int X, int Y) point)
    {
        point = default;
        if (generation != Generation || _pendingPoint is not { } pending)
            return false;

        point = pending;
        _pendingPoint = null;
        _lastTakenPoint = pending;
        return true;
    }

    /// <summary>대기 중인 위치를 지우고 이전 응답을 오래된 세대로 만듭니다.</summary>
    internal void Cancel()
    {
        Generation++;
        _pendingPoint = null;
        _lastTakenPoint = null;
    }

    /// <summary>예약된 응답이 현재 드래그 세대인지 확인합니다.</summary>
    internal bool IsCurrent(long generation) => generation == Generation;
}
