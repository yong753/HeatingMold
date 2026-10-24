using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Application.Sessions;

/// <summary>작업 좌표계 입력이 미확정·미리보기·확정 중 어느 상태인지 나타냅니다.</summary>
public enum WorkingCoordinateSystemState
{
    /// <summary>확정된 좌표계가 없는 상태입니다.</summary>
    Unconfirmed,

    /// <summary>새 좌표계를 시험 중이며 기존 확정 좌표계는 유지하는 상태입니다.</summary>
    Preview,

    /// <summary>열린 모델 revision에 대해 좌표계를 확정한 상태입니다.</summary>
    Confirmed
}

/// <summary>작업 좌표계 미리보기와 확정 상태를 관리하는 응용 세션입니다.</summary>
public sealed class WorkingCoordinateSystemSession
{
    /// <summary>현재 교체 후보인 좌표계입니다.</summary>
    public WorkingCoordinateSystem? Preview { get; private set; }

    /// <summary>확정된 좌표계입니다.</summary>
    public WorkingCoordinateSystem? Confirmed { get; private set; }

    /// <summary>미리보기와 확정 값에서 현재 세션 상태를 구합니다.</summary>
    public WorkingCoordinateSystemState State => Preview is not null
        ? WorkingCoordinateSystemState.Preview
        : Confirmed is not null
            ? WorkingCoordinateSystemState.Confirmed
            : WorkingCoordinateSystemState.Unconfirmed;

    /// <summary>확정 상태를 바꾸지 않고 새 미리보기 좌표계를 보관합니다.</summary>
    public void SetPreview(WorkingCoordinateSystem coordinateSystem)
    {
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        Preview = coordinateSystem;
    }

    /// <summary>현재 모델 revision과 맞는 미리보기 값을 좌표계로 확정합니다.</summary>
    public void Confirm(ModelRevision revision)
    {
        if (Preview is null || Preview.Revision != revision)
            throw new InvalidOperationException("작업 좌표계 미리보기가 없거나 현재 모델 revision과 다릅니다.");

        Confirmed = Preview;
        Preview = null;
    }

    /// <summary>미리보기와 확정 좌표계를 모두 무효화합니다.</summary>
    public void Invalidate()
    {
        Preview = null;
        Confirmed = null;
    }

    /// <summary>확정 좌표계를 유지하고 현재 미리보기만 취소합니다.</summary>
    public void CancelPreview() => Preview = null;
}

/// <summary>열린 네이티브 문서의 런타임 ID·기하 세대와 원본 해시입니다.</summary>
public sealed record RuntimeDocument
{
    /// <summary>현재 열린 기하 문서의 런타임 식별자입니다.</summary>
    public GeometryDocumentId DocumentId { get; }

    /// <summary>현재 문서가 제공하는 모델 기하 revision입니다.</summary>
    public ModelRevision ModelRevision { get; }

    /// <summary>세션이 사용 중인 원본 STEP 바이트의 SHA-256입니다.</summary>
    public string SourceModelSha256 { get; }

    /// <summary>런타임 문서 ID, revision과 원본 모델 해시를 검증합니다.</summary>
    public RuntimeDocument(GeometryDocumentId documentId, ModelRevision modelRevision, string sourceModelSha256)
    {
        if (documentId.IsEmpty)
            throw new ArgumentException("열린 문서 ID는 비어 있을 수 없습니다.", nameof(documentId));
        if (sourceModelSha256 is null || sourceModelSha256.Length != 64 || sourceModelSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("원본 모델 SHA-256은 16진수 64자로 입력해야 합니다.", nameof(sourceModelSha256));

        DocumentId = documentId;
        ModelRevision = modelRevision;
        SourceModelSha256 = sourceModelSha256.ToLowerInvariant();
    }
}
