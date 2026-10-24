using HeatingMold.Core.Primitives;
using HeatingMold.Application.Ports.Geometry;

namespace HeatingMold.Application.Sessions;

/// <summary>확정 스냅샷과 사용자 초안을 원자적으로 보관하는 프로젝트 세션입니다.</summary>
public sealed class ProjectSession : IDisposable
{
    private readonly object _sync = new();
    private SessionSnapshot _current;
    private SessionSnapshot _lastApplied;
    private Guid? _activeOperationId;
    private bool _isDisposed;

    /// <summary>현재 세션의 불변 스냅샷입니다.</summary>
    public SessionSnapshot Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    /// <summary>마지막 성공 변경으로 확정한 입력 스냅샷입니다.</summary>
    public SessionSnapshot LastAppliedSnapshot
    {
        get
        {
            lock (_sync)
                return _lastApplied;
        }
    }

    /// <summary>실행 중 여부를 포함해 계산한 현재 사용자 작업 단계입니다.</summary>
    public WorkflowState WorkflowState
    {
        get
        {
            lock (_sync)
                return _current.GetWorkflowState(_activeOperationId.HasValue);
        }
    }

    /// <summary>선택한 초기 세션 스냅샷으로 새 프로젝트 세션을 만듭니다.</summary>
    public ProjectSession(SessionSnapshot? initialSnapshot = null)
    {
        _current = initialSnapshot ?? SessionSnapshot.Empty;
        _lastApplied = _current.WithDraft(null);
    }

    /// <summary>한 번에 하나의 변경 연산만 진행하도록 세션 잠금을 얻습니다.</summary>
    public bool TryBeginOperation(out ProjectOperationLease? lease)
    {
        lock (_sync)
        {
            if (_isDisposed || _activeOperationId.HasValue)
            {
                lease = null;
                return false;
            }

            Guid operationId = Guid.NewGuid();
            _activeOperationId = operationId;
            lease = new ProjectOperationLease(this, operationId, _current);
            return true;
        }
    }

    /// <summary>현재 확정 설계의 revision에 맞는 입력 초안을 보관합니다.</summary>
    public bool TrySetDraft(InputDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_sync)
        {
            if (_isDisposed || _activeOperationId.HasValue || _current.CurrentProject?.Revision != draft.BaseRevision)
                return false;

            _current = _current.WithDraft(draft);
            return true;
        }
    }

    /// <summary>진행 중인 변경이 없을 때 현재 입력 초안을 취소합니다.</summary>
    public bool TryCancelDraft()
    {
        lock (_sync)
        {
            if (_isDisposed || _activeOperationId.HasValue || _current.CurrentInputDraft is null)
                return false;

            _current = _current.WithDraft(null);
            return true;
        }
    }

    /// <summary>저장 완료 revision이 현재 설계와 같을 때만 dirty 상태를 해제합니다.</summary>
    public bool TryMarkSaved(DesignRevision savedRevision)
    {
        lock (_sync)
        {
            if (_isDisposed || _activeOperationId.HasValue || _current.CurrentProject?.Revision != savedRevision)
                return false;

            _current = _current.WithSavedRevision(savedRevision);
            _lastApplied = _current.WithDraft(null);
            return true;
        }
    }

    /// <summary>확정 설계를 바꾸지 않고 표시 복구가 필요함을 기록합니다.</summary>
    public void MarkPresentationOutOfSync()
    {
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _current = _current.WithPresentationOutOfSync();
            _lastApplied = _lastApplied.WithPresentationOutOfSync();
        }
    }

    internal bool TryCommit(
        Guid operationId,
        DesignRevision? expectedBaseRevision,
        SessionSnapshot nextSnapshot)
    {
        ArgumentNullException.ThrowIfNull(nextSnapshot);
        ModelSource? previousSource;
        lock (_sync)
        {
            if (_isDisposed || _activeOperationId != operationId || _current.CurrentProject?.Revision != expectedBaseRevision)
                return false;

            previousSource = ReferenceEquals(_current.SourceModel, nextSnapshot.SourceModel)
                ? null
                : _current.SourceModel;
            _current = nextSnapshot;
            _lastApplied = nextSnapshot.WithDraft(null);
        }

        previousSource?.Dispose();
        return true;
    }

    internal void EndOperation(Guid operationId)
    {
        ModelSource? sourceToDispose = null;
        lock (_sync)
        {
            if (_activeOperationId == operationId)
            {
                _activeOperationId = null;
                if (_isDisposed)
                    sourceToDispose = _current.SourceModel;
            }
        }

        sourceToDispose?.Dispose();
    }

    /// <summary>세션 종료 시 현재 원본 모델을 해제하고 진행 중 연산의 해제를 예약합니다.</summary>
    public void Dispose()
    {
        ModelSource? sourceToDispose = null;
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            if (!_activeOperationId.HasValue)
                sourceToDispose = _current.SourceModel;
        }

        sourceToDispose?.Dispose();
    }
}

/// <summary>변경 후보가 사용하는 단일 연산 잠금과 BaseRevision입니다.</summary>
public sealed class ProjectOperationLease : IDisposable
{
    private readonly ProjectSession _session;
    private readonly Guid _operationId;
    private bool _isDisposed;

    /// <summary>연산 시작 시 캡처한 불변 상태입니다.</summary>
    public SessionSnapshot BaseSnapshot { get; }

    /// <summary>연산 시작 시 캡처한 설계 revision입니다.</summary>
    public DesignRevision? BaseRevision => BaseSnapshot.CurrentProject?.Revision;

    internal ProjectOperationLease(ProjectSession session, Guid operationId, SessionSnapshot baseSnapshot)
    {
        _session = session;
        _operationId = operationId;
        BaseSnapshot = baseSnapshot;
    }

    /// <summary>BaseRevision이 바뀌지 않았을 때 후보 스냅샷을 원자 교체합니다.</summary>
    public bool Commit(SessionSnapshot nextSnapshot)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ProjectOperationLease));

        return _session.TryCommit(_operationId, BaseRevision, nextSnapshot);
    }

    /// <summary>연산이 끝나면 세션 잠금을 한 번만 해제합니다.</summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _session.EndOperation(_operationId);
    }
}
