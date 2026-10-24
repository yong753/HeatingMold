using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.App.ViewModels;

/// <summary>회로 편집 중인 절점과 발열 구간을 안정 ID로 식별합니다.</summary>
internal readonly record struct CircuitTopologyEndpointKey(
    HeatingPathSegmentId SegmentId,
    ControlPointId ControlPointId,
    ConnectionPointRole Role);

/// <summary>신규 연결부 편집 목록에 표시할 안정 ID와 문맥입니다.</summary>
internal sealed record CircuitTopologyEndpointChoice(
    CircuitTopologyEndpointKey Key,
    CircuitId CircuitId,
    RegionId RegionId,
    string DisplayName);

/// <summary>topology 통합 때 안정 ID로 보존할 회로와 표시 이름입니다.</summary>
internal sealed record CircuitSurvivorChoice(CircuitId CircuitId, string DisplayName);

/// <summary>신규 연결부의 표면 앵커와 외부 좌표 초안을 보관합니다.</summary>
internal sealed class CircuitTopologyEndpointDraft
{
    /// <summary>확정 전까지 유지할 새 연결부 ID입니다.</summary>
    public ConnectionPointId ConnectionPointId { get; }

    /// <summary>사용자가 지정한 금형 표면 앵커입니다.</summary>
    public SurfaceAnchor SurfaceAnchor { get; set; }

    /// <summary>사용자가 입력한 금형 외부 연결 좌표입니다.</summary>
    public Vec3? ExternalConnectionPoint { get; set; }

    /// <summary>연결부 ID를 받아 입력이 완료되지 않은 초안을 만듭니다.</summary>
    public CircuitTopologyEndpointDraft(ConnectionPointId connectionPointId)
    {
        ConnectionPointId = connectionPointId;
    }
}

/// <summary>회전 구간 목록에 표시할 활성 상태와 안정 ID입니다.</summary>
internal sealed class CircuitTurnChoice : INotifyPropertyChanged
{
    private bool _isActive;

    /// <summary>회전 구간의 영속 ID입니다.</summary>
    public HeatingPathSegmentId SegmentId { get; }

    /// <summary>목록에 표시할 간결한 설명입니다.</summary>
    public string DisplayName { get; }

    /// <summary>확정 상태 또는 현재 미리보기의 활성 여부입니다.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>체크 상태가 바뀌면 WPF 항목에 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>구간 ID, 표시 이름과 초기 활성 상태를 설정합니다.</summary>
    public CircuitTurnChoice(HeatingPathSegmentId segmentId, string displayName, bool isActive)
    {
        SegmentId = segmentId;
        DisplayName = displayName;
        _isActive = isActive;
    }
}

/// <summary>회로 topology 화면의 미확정 계획과 입력 초안을 소유합니다.</summary>
internal sealed class CircuitTopologyEditorViewModel
{
    private readonly Dictionary<HeatingPathSegmentId, bool> _turnStates = new();
    private readonly Dictionary<CircuitTopologyEndpointKey, CircuitTopologyEndpointDraft> _endpointDrafts = new();
    private IReadOnlyList<CircuitSurvivorChoice> _survivorChoices = Array.Empty<CircuitSurvivorChoice>();
    private IReadOnlyList<CircuitTurnChoice> _turnChoices = Array.Empty<CircuitTurnChoice>();
    private IReadOnlyList<CircuitTopologyEndpointChoice> _endpointChoices = Array.Empty<CircuitTopologyEndpointChoice>();
    private CircuitTopologyEndpointChoice _selectedEndpointChoice;
    private CircuitId? _selectedSurvivorCircuitId;
    private int _modeChangeSuppressionDepth;

    /// <summary>현재 입력으로 분석한 topology 계획입니다.</summary>
    public CircuitTopologyPlan Plan { get; private set; }

    /// <summary>확정 회로를 topology 통합 survivor로 선택할 목록입니다.</summary>
    public IReadOnlyList<CircuitSurvivorChoice> SurvivorChoices => _survivorChoices;

    /// <summary>현재 선택한 topology 통합 survivor 회로입니다.</summary>
    public CircuitSurvivorChoice SelectedSurvivorChoice =>
        _selectedSurvivorCircuitId is { } id
            ? _survivorChoices.FirstOrDefault(choice => choice.CircuitId == id)
            : null;

    /// <summary>현재 topology 계획에서 입력이 필요한 신규 연결부 끝점입니다.</summary>
    public IReadOnlyList<CircuitTopologyEndpointChoice> EndpointChoices => _endpointChoices;

    /// <summary>현재 선택한 신규 연결부 끝점입니다.</summary>
    public CircuitTopologyEndpointChoice SelectedEndpointChoice => _selectedEndpointChoice;

    /// <summary>선택 끝점의 표면 앵커와 외부 좌표 입력 상태입니다.</summary>
    public string EndpointStatusText { get; private set; } = "신규 연결부 입력: 필요 없음";

    /// <summary>선택 끝점의 외부 X 좌표 입력 문자열입니다.</summary>
    public string ExternalXCoordinateText { get; private set; } = string.Empty;

    /// <summary>선택 끝점의 외부 Y 좌표 입력 문자열입니다.</summary>
    public string ExternalYCoordinateText { get; private set; } = string.Empty;

    /// <summary>선택 끝점의 외부 Z 좌표 입력 문자열입니다.</summary>
    public string ExternalZCoordinateText { get; private set; } = string.Empty;

    /// <summary>화면 입력 모드 컨트롤을 동기화하며 발생하는 변경 이벤트를 억제하는 상태입니다.</summary>
    public bool IsModeChangeSuppressed => _modeChangeSuppressionDepth > 0;

    /// <summary>표면 앵커 선택이 완료될 때 연결할 신규 회로 끝점입니다.</summary>
    public CircuitTopologyEndpointKey? PendingSurfaceAnchorEndpointKey { get; set; }

    /// <summary>안정 구간 ID별 미리보기 활성 상태입니다.</summary>
    public IReadOnlyDictionary<HeatingPathSegmentId, bool> TurnStates => _turnStates;

    /// <summary>확정 발열 경로에서 편집할 회전 구간 선택지입니다.</summary>
    public IReadOnlyList<CircuitTurnChoice> TurnChoices => _turnChoices;

    /// <summary>안정 끝점 키별 신규 연결부 입력 초안입니다.</summary>
    public IReadOnlyDictionary<CircuitTopologyEndpointKey, CircuitTopologyEndpointDraft> EndpointDrafts =>
        _endpointDrafts;

    /// <summary>화면 입력 모드를 맞추는 동안 재진입 이벤트를 막는 범위를 시작합니다.</summary>
    public IDisposable SuppressModeChangedEvents()
    {
        _modeChangeSuppressionDepth++;
        return new ModeChangeSuppressionScope(this);
    }

    /// <summary>현재 회전 상태를 조회합니다.</summary>
    public bool TryGetTurnState(HeatingPathSegmentId segmentId, out bool isActive) =>
        _turnStates.TryGetValue(segmentId, out isActive);

    /// <summary>확정 상태와 같은 값은 제거하고 실제 변경만 미리보기로 저장합니다.</summary>
    public void SetTurnState(HeatingPathSegmentId segmentId, bool? isActive)
    {
        if (isActive is { } value)
            _turnStates[segmentId] = value;
        else
            _turnStates.Remove(segmentId);
    }

    /// <summary>회전 구간 ID와 초안 상태를 기준으로 화면 선택지를 정렬해 만듭니다.</summary>
    public void RefreshTurnChoices(IEnumerable<HeatingPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _turnChoices = segments
            .Where(segment => segment.Kind == HeatingPathSegmentKind.Turn)
            .OrderBy(segment => segment.Sequence)
            .ThenBy(segment => segment.Id.Value)
            .Select(segment => new CircuitTurnChoice(
                segment.Id,
                string.Concat("회전 구간 · ", ShortId(segment.Id.Value)),
                _turnStates.TryGetValue(segment.Id, out bool previewState)
                    ? previewState
                    : segment.IsActive))
            .ToArray();
    }

    /// <summary>뷰포트 선택 결과를 같은 안정 ID의 체크 항목에 반영합니다.</summary>
    public void SetTurnChoiceState(HeatingPathSegmentId segmentId, bool isActive)
    {
        CircuitTurnChoice choice = _turnChoices.FirstOrDefault(candidate => candidate.SegmentId == segmentId);
        if (choice is not null)
            choice.IsActive = isActive;
    }

    /// <summary>입력 조건으로 분석한 topology 계획을 보관합니다.</summary>
    public void SetPlan(CircuitTopologyPlan plan) => Plan = plan;

    /// <summary>회로 안정 ID로 survivor 목록을 정렬하고 여전히 존재하는 선택을 유지합니다.</summary>
    public void RefreshSurvivorChoices(IEnumerable<Circuit> circuits)
    {
        ArgumentNullException.ThrowIfNull(circuits);
        CircuitId? selectedCircuitId = _selectedSurvivorCircuitId;
        _survivorChoices = circuits
            .OrderBy(circuit => circuit.Sequence)
            .ThenBy(circuit => circuit.Id.Value)
            .Select(circuit => new CircuitSurvivorChoice(
                circuit.Id,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "회로 {0} · ID {1}",
                    circuit.Sequence + 1,
                    ShortId(circuit.Id.Value))))
            .ToArray();
        _selectedSurvivorCircuitId = selectedCircuitId is { } id &&
            _survivorChoices.Any(choice => choice.CircuitId == id)
                ? id
                : null;
    }

    /// <summary>화면에서 선택한 survivor가 현재 목록에 속하는지 확인해 선택 ID를 저장합니다.</summary>
    public void SelectSurvivorChoice(CircuitSurvivorChoice choice)
    {
        _selectedSurvivorCircuitId = choice is null
            ? null
            : _survivorChoices.FirstOrDefault(candidate => candidate.CircuitId == choice.CircuitId)?.CircuitId;
    }

    /// <summary>새 topology 선택지를 갱신하고 기존 선택과 입력 draft를 가능한 한 보존합니다.</summary>
    public void SetEndpointChoices(IEnumerable<CircuitTopologyEndpointChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        CircuitTopologyEndpointKey? selectedKey = _selectedEndpointChoice?.Key;
        CircuitTopologyEndpointChoice[] distinctChoices = choices
            .GroupBy(choice => choice.Key)
            .Select(group => group.First())
            .ToArray();

        RetainEndpointDrafts(distinctChoices.Select(choice => choice.Key));
        _endpointChoices = distinctChoices;
        _selectedEndpointChoice = selectedKey is { } key
            ? distinctChoices.FirstOrDefault(choice => choice.Key == key)
            : null;
        _selectedEndpointChoice ??= distinctChoices.FirstOrDefault();
        RefreshSelectedEndpointPresentation();
    }

    /// <summary>topology 계획을 신규 연결부 편집 목록으로 투영합니다.</summary>
    public void RefreshEndpointChoices(DesignProject project, CircuitTopologyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        CircuitTopologyEndpointChoice[] choices = plan.Mapping.Assignments
            .SelectMany((assignment, index) => new[]
            {
                CreateEndpointChoice(project, assignment, index, ConnectionPointRole.Start),
                CreateEndpointChoice(project, assignment, index, ConnectionPointRole.End)
            })
            .Where(choice => choice is not null)
            .Select(choice => choice!)
            .ToArray();

        SetEndpointChoices(choices);
    }

    /// <summary>끝점 선택을 ViewModel 상태에 반영하고 좌표·앵커 표시를 갱신합니다.</summary>
    public void SelectEndpointChoice(CircuitTopologyEndpointChoice choice)
    {
        _selectedEndpointChoice = choice is null
            ? null
            : _endpointChoices.FirstOrDefault(candidate => candidate.Key == choice.Key);
        RefreshSelectedEndpointPresentation();
    }

    /// <summary>현재 분석 결과에 포함된 끝점만 유지하고 새 끝점 초안을 만듭니다.</summary>
    public void RetainEndpointDrafts(IEnumerable<CircuitTopologyEndpointKey> activeKeys)
    {
        ArgumentNullException.ThrowIfNull(activeKeys);
        HashSet<CircuitTopologyEndpointKey> retainedKeys = activeKeys.ToHashSet();

        foreach (CircuitTopologyEndpointKey staleKey in _endpointDrafts.Keys
                     .Where(key => !retainedKeys.Contains(key))
                     .ToArray())
        {
            _endpointDrafts.Remove(staleKey);
        }

        foreach (CircuitTopologyEndpointKey key in retainedKeys)
            _endpointDrafts.TryAdd(key, new CircuitTopologyEndpointDraft(ConnectionPointId.New()));
    }

    /// <summary>현재 입력 중인 신규 연결부 초안만 초기화합니다.</summary>
    public void ClearEndpointDrafts()
    {
        _endpointDrafts.Clear();
        _endpointChoices = Array.Empty<CircuitTopologyEndpointChoice>();
        _selectedEndpointChoice = null;
        RefreshSelectedEndpointPresentation();
    }

    /// <summary>안정 끝점 키에 대응하는 사용자 입력 초안을 반환합니다.</summary>
    public bool TryGetEndpointDraft(
        CircuitTopologyEndpointKey key,
        out CircuitTopologyEndpointDraft draft) =>
        _endpointDrafts.TryGetValue(key, out draft);

    /// <summary>현재 문화권의 유한한 XYZ 입력을 신규 연결부 초안에 저장합니다.</summary>
    public bool TryUpdateExternalConnectionPoint(
        CircuitTopologyEndpointKey key,
        string xText,
        string yText,
        string zText,
        out string failure)
    {
        if (!_endpointDrafts.TryGetValue(key, out CircuitTopologyEndpointDraft draft))
        {
            failure = "외부 좌표를 저장할 신규 회로 끝점을 선택하세요.";
            return false;
        }

        if (!TryReadFiniteCoordinate(xText, out double x) ||
            !TryReadFiniteCoordinate(yText, out double y) ||
            !TryReadFiniteCoordinate(zText, out double z))
        {
            failure = "신규 연결부 외부 좌표의 X, Y, Z를 모두 유한한 mm 값으로 입력하세요.";
            return false;
        }

        draft.ExternalConnectionPoint = new Vec3(x, y, z);
        failure = string.Empty;
        if (_selectedEndpointChoice?.Key == key)
            RefreshSelectedEndpointPresentation();

        return true;
    }

    /// <summary>회로 배정마다 기존 연결부 또는 완성된 신규 끝점 입력을 연결합니다.</summary>
    public bool TryBuildCircuitEndpointInputs(
        DesignProject project,
        CircuitTopologyPlan plan,
        out Dictionary<CircuitId, CircuitEndpointPair> endpointPairs,
        out HashSet<ConnectionPointId> newConnectionPointIds,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        endpointPairs = new Dictionary<CircuitId, CircuitEndpointPair>();
        newConnectionPointIds = new HashSet<ConnectionPointId>();
        var candidateEndpointPairs = new Dictionary<CircuitId, CircuitEndpointPair>();
        var candidateConnectionPointIds = new HashSet<ConnectionPointId>();
        failure = "새 회로 끝점의 표면 앵커와 외부 연결 좌표를 입력하세요.";
        foreach (CircuitIdentityAssignment assignment in plan.Mapping.Assignments)
        {
            if (!TryCreateEndpointDefinition(
                    project,
                    assignment.StartConnectionPointId,
                    assignment.Component.Segments[0].SegmentId,
                    ConnectionPointRole.Start,
                    assignment.Component.StartControlPointId,
                    out CircuitEndpointDefinition start,
                    out bool isNewStart) ||
                !TryCreateEndpointDefinition(
                    project,
                    assignment.EndConnectionPointId,
                    assignment.Component.Segments[^1].SegmentId,
                    ConnectionPointRole.End,
                    assignment.Component.EndControlPointId,
                    out CircuitEndpointDefinition end,
                    out bool isNewEnd))
            {
                failure = "새 회로 끝점마다 표면 앵커를 선택하고 외부 연결 좌표를 저장해야 합니다.";
                return false;
            }

            candidateEndpointPairs.Add(assignment.CircuitId, new CircuitEndpointPair(start, end));
            if (isNewStart)
                candidateConnectionPointIds.Add(start.Id);
            if (isNewEnd)
                candidateConnectionPointIds.Add(end.Id);
        }

        endpointPairs = candidateEndpointPairs;
        newConnectionPointIds = candidateConnectionPointIds;
        failure = string.Empty;
        return true;
    }

    /// <summary>새 연결부 초안과 작업 좌표계에서 회로 연결 경로 입력을 원자적으로 만듭니다.</summary>
    public bool TryBuildConnectionPathInputs(
        IEnumerable<ConnectionPointId> newConnectionPointIds,
        WorkingCoordinateSystem coordinateSystem,
        ModelRevision modelRevision,
        string moldExitAllowanceText,
        string externalAllowanceText,
        out Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput> connectionPathInputs,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(newConnectionPointIds);
        ArgumentNullException.ThrowIfNull(coordinateSystem);

        connectionPathInputs = new Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput>();
        failure = "각 신규 연결부의 표면 앵커와 외부 좌표가 필요합니다.";
        if (coordinateSystem.Revision != modelRevision)
        {
            failure = "새 연결 경로의 모델 revision이 작업 좌표계와 다릅니다.";
            return false;
        }

        if (!TryReadNonNegativeCoordinate(moldExitAllowanceText, out double moldExitAllowanceMm) ||
            !TryReadNonNegativeCoordinate(externalAllowanceText, out double externalAllowanceMm))
        {
            failure = "금형 위 연장과 외부 연결선의 추가 여유를 0 이상의 유한한 mm 값으로 입력하세요.";
            return false;
        }

        ConnectionPointId[] requestedIds = newConnectionPointIds.Distinct().ToArray();
        var candidateInputs = new Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput>();
        LengthMm moldExitAllowance = new(moldExitAllowanceMm);
        LengthMm externalAllowance = new(externalAllowanceMm);
        foreach (ConnectionPointId connectionPointId in requestedIds)
        {
            CircuitTopologyEndpointDraft[] matchingDrafts = EndpointDrafts.Values
                .Where(draft => draft.ConnectionPointId == connectionPointId)
                .ToArray();
            if (matchingDrafts.Length != 1 ||
                matchingDrafts[0].SurfaceAnchor is not { } surfaceAnchor ||
                matchingDrafts[0].ExternalConnectionPoint is not { } externalPoint)
            {
                failure = "각 신규 연결부마다 표면 앵커와 외부 연결 좌표를 모두 지정해야 합니다.";
                return false;
            }

            ConnectionPathLegRequest[] legs =
            [
                new(
                    surfaceAnchor.ModelPoint,
                    GeometryDomain.OnMold,
                    ConductorRole.HeatingWireExtension,
                    moldExitAllowance),
                new(
                    externalPoint,
                    GeometryDomain.External,
                    ConductorRole.SeparateConnectionWire,
                    externalAllowance,
                    isMoldExitTransition: true)
            ];
            candidateInputs.Add(
                connectionPointId,
                new CircuitTopologyConnectionPathInput(coordinateSystem, modelRevision, legs));
        }

        connectionPathInputs = candidateInputs;
        failure = string.Empty;
        return true;
    }

    /// <summary>현재 미리보기 계획과 연결부 입력을 모두 초기화합니다.</summary>
    public void Reset()
    {
        Plan = null;
        PendingSurfaceAnchorEndpointKey = null;
        _turnStates.Clear();
        ClearEndpointDrafts();
    }

    /// <summary>선택된 끝점 초안에서 앵커 상태와 좌표 문자열을 화면 표시 값으로 만듭니다.</summary>
    public void RefreshSelectedEndpointPresentation()
    {
        if (_selectedEndpointChoice is null ||
            !_endpointDrafts.TryGetValue(_selectedEndpointChoice.Key, out CircuitTopologyEndpointDraft draft))
        {
            EndpointStatusText = "신규 연결부 입력: 필요 없음";
            ExternalXCoordinateText = string.Empty;
            ExternalYCoordinateText = string.Empty;
            ExternalZCoordinateText = string.Empty;
            return;
        }

        string anchorStatus = draft.SurfaceAnchor is null ? "표면 앵커 미지정" : "표면 앵커 지정됨";
        string externalStatus = draft.ExternalConnectionPoint.HasValue ? "외부 좌표 저장됨" : "외부 좌표 미저장";
        EndpointStatusText = string.Concat(anchorStatus, " · ", externalStatus);
        if (draft.ExternalConnectionPoint is { } externalPoint)
        {
            ExternalXCoordinateText = externalPoint.X.ToString("G17", CultureInfo.CurrentCulture);
            ExternalYCoordinateText = externalPoint.Y.ToString("G17", CultureInfo.CurrentCulture);
            ExternalZCoordinateText = externalPoint.Z.ToString("G17", CultureInfo.CurrentCulture);
        }
        else
        {
            ExternalXCoordinateText = string.Empty;
            ExternalYCoordinateText = string.Empty;
            ExternalZCoordinateText = string.Empty;
        }
    }

    private void EndModeChangeSuppression()
    {
        if (_modeChangeSuppressionDepth <= 0)
            throw new InvalidOperationException("회로 topology 모드 억제 범위가 균형을 잃었습니다.");

        _modeChangeSuppressionDepth--;
    }

    /// <summary>현재 문화권에서 해석되는 유한한 좌표 문자열인지 확인합니다.</summary>
    private static bool TryReadFiniteCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    /// <summary>현재 문화권에서 해석되는 유한한 0 이상 길이인지 확인합니다.</summary>
    private static bool TryReadNonNegativeCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value) && value >= 0;

    /// <summary>분석 배정에서 새 연결부 입력이 필요한 끝점만 화면 선택지로 만듭니다.</summary>
    private static CircuitTopologyEndpointChoice CreateEndpointChoice(
        DesignProject project,
        CircuitIdentityAssignment assignment,
        int sequence,
        ConnectionPointRole role)
    {
        HeatingPathSegmentId segmentId = role == ConnectionPointRole.Start
            ? assignment.Component.Segments[0].SegmentId
            : assignment.Component.Segments[^1].SegmentId;
        ControlPointId controlPointId = role == ConnectionPointRole.Start
            ? assignment.Component.StartControlPointId
            : assignment.Component.EndControlPointId;
        ConnectionPointId? preferredId = role == ConnectionPointRole.Start
            ? assignment.StartConnectionPointId
            : assignment.EndConnectionPointId;
        ConnectionPoint[] matches = preferredId is { } id
            ? project.ConnectionPoints.Where(point => point.Id == id).ToArray()
            : project.ConnectionPoints.Where(point => point.ControlPointId == controlPointId).ToArray();
        if (matches.Length == 1 && matches[0].ControlPointId == controlPointId)
            return null;

        HeatingPath path = project.HeatingPaths.SingleOrDefault(candidate =>
            candidate.Segments.Any(segment => segment.Id == segmentId));
        if (path is null)
            return null;

        var key = new CircuitTopologyEndpointKey(segmentId, controlPointId, role);
        string roleLabel = role == ConnectionPointRole.Start ? "시작" : "끝";
        string displayName = string.Format(
            CultureInfo.CurrentCulture,
            "새 연결부 · 회로 {0} {1} · 절점 {2}",
            sequence + 1,
            roleLabel,
            ShortId(controlPointId.Value));
        return new CircuitTopologyEndpointChoice(
            key,
            assignment.CircuitId,
            path.GenerationSource.RegionId,
            displayName);
    }

    private bool TryCreateEndpointDefinition(
        DesignProject project,
        ConnectionPointId? preferredId,
        HeatingPathSegmentId segmentId,
        ConnectionPointRole role,
        ControlPointId controlPointId,
        out CircuitEndpointDefinition endpoint,
        out bool isNewEndpoint)
    {
        ConnectionPoint[] matches = preferredId is { } id
            ? project.ConnectionPoints.Where(point => point.Id == id).ToArray()
            : project.ConnectionPoints.Where(point => point.ControlPointId == controlPointId).ToArray();
        if (matches.Length == 1 && matches[0].ControlPointId == controlPointId)
        {
            ConnectionPoint point = matches[0];
            endpoint = new CircuitEndpointDefinition(
                point.Id,
                point.SurfaceAnchor,
                point.ExternalConnectionPoint);
            isNewEndpoint = false;
            return true;
        }

        CircuitTopologyEndpointKey key = new(segmentId, controlPointId, role);
        if (preferredId.HasValue ||
            !_endpointDrafts.TryGetValue(key, out CircuitTopologyEndpointDraft draft) ||
            draft.SurfaceAnchor is null || draft.ExternalConnectionPoint is not { } externalPoint)
        {
            endpoint = null;
            isNewEndpoint = false;
            return false;
        }

        endpoint = new CircuitEndpointDefinition(
            draft.ConnectionPointId,
            draft.SurfaceAnchor,
            externalPoint);
        isNewEndpoint = true;
        return true;
    }

    /// <summary>화면에 표시할 안정 ID의 앞 여덟 글자를 반환합니다.</summary>
    private static string ShortId(Guid id) =>
        id.ToString("N", CultureInfo.InvariantCulture)[..8];

    private sealed class ModeChangeSuppressionScope : IDisposable
    {
        private CircuitTopologyEditorViewModel _owner;
        private bool _isDisposed;

        public ModeChangeSuppressionScope(CircuitTopologyEditorViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _owner.EndModeChangeSuppression();
        }
    }
}
