using HeatingMold.Core.Primitives;

namespace HeatingMold.Application.Ports.Runtime;

/// <summary>응용 작업의 사후 알림 종류입니다.</summary>
public enum ApplicationEventKind
{
    /// <summary>새 확정 스냅샷을 화면에 반영해야 합니다.</summary>
    SessionChanged,

    /// <summary>후보 변경이 확정 전에 실패했습니다.</summary>
    OperationFailed,

    /// <summary>Native 장면을 확정 설계에서 다시 구성해야 합니다.</summary>
    PresentationRepairNeeded
}

/// <summary>확정 이후 화면·호스트가 처리할 작업 알림입니다.</summary>
public sealed record ApplicationEvent(
    ApplicationEventKind Kind,
    DesignRevision? DesignRevision,
    string? MessageKey = null);

/// <summary>플랫폼 호스트가 Native 호출 스레드와 취소 상태를 점검합니다.</summary>
public interface IOperationContext
{
    /// <summary>현재 호출 스레드에서 Native 기하를 사용할 수 있는지 검사합니다.</summary>
    void VerifyGeometryThread();
}

/// <summary>확정 이후 알림을 전달하는 UI 독립 포트입니다.</summary>
public interface IApplicationEvents
{
    /// <summary>세션 변경 또는 실패 후 복구 알림을 발행합니다.</summary>
    void Publish(ApplicationEvent applicationEvent);
}

/// <summary>기하 질의의 호출 순서를 진단하는 단일 UI 스레드 구현입니다.</summary>
public sealed class SingleThreadOperationContext : IOperationContext
{
    private readonly int _ownerThreadId;

    /// <summary>현재 호출 스레드를 Native 소유 스레드로 기록합니다.</summary>
    public SingleThreadOperationContext()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>소유 스레드 밖에서 Native 기하 호출을 거부합니다.</summary>
    public void VerifyGeometryThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Native 기하 연산은 세션 소유 UI 스레드에서 실행해야 합니다.");
    }
}
