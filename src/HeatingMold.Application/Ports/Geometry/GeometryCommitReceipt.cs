using HeatingMold.Application.Sessions;

namespace HeatingMold.Application.Ports.Geometry;

/// <summary>Native 후보 Publish가 확정되었는지와 최종 문서를 알려줍니다.</summary>
public sealed record GeometryCommitReceipt
{
    /// <summary>후보 Native 상태의 소유권이 확정되었는지 나타냅니다.</summary>
    public bool IsCommitted { get; }

    /// <summary>확정 성공 시 활성 상태가 된 런타임 문서입니다.</summary>
    public RuntimeDocument? RuntimeDocument { get; }

    /// <summary>Native 기하는 확정됐지만 화면 표시를 다시 구성해야 하는지 나타냅니다.</summary>
    public bool PresentationOutOfSync { get; }

    /// <summary>Publish 실패의 표준 코드입니다.</summary>
    public string? FailureCode { get; }

    /// <summary>확정 receipt와 실패 receipt를 서로 모순 없이 만듭니다.</summary>
    public GeometryCommitReceipt(
        bool isCommitted,
        RuntimeDocument? runtimeDocument,
        string? failureCode = null,
        bool presentationOutOfSync = false)
    {
        if (isCommitted && (runtimeDocument is null || !string.IsNullOrWhiteSpace(failureCode)))
            throw new ArgumentException("확정 receipt에는 활성 문서만 있어야 합니다.");
        if (!isCommitted &&
            (runtimeDocument is not null || string.IsNullOrWhiteSpace(failureCode) || presentationOutOfSync))
            throw new ArgumentException("실패 receipt에는 이유 코드만 있어야 합니다.");

        IsCommitted = isCommitted;
        RuntimeDocument = runtimeDocument;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
        PresentationOutOfSync = presentationOutOfSync;
    }
}
