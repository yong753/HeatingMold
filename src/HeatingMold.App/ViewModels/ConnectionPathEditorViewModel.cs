using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.App.ViewModels;

/// <summary>편집할 연결 경로와 해당 설계 revision을 선택 목록에 표시합니다.</summary>
internal sealed record ConnectionPathChoice(
    ConnectionPointId ConnectionPointId,
    DesignRevision DesignRevision,
    string DisplayName);

/// <summary>경로의 한 선형 구간 끝점과 검증 속성을 편집 표에 보관합니다.</summary>
internal sealed class ConnectionPathLegRow
{
    /// <summary>구간 끝점의 X 좌표(mm)입니다.</summary>
    public double X { get; set; }

    /// <summary>구간 끝점의 Y 좌표(mm)입니다.</summary>
    public double Y { get; set; }

    /// <summary>구간 끝점의 Z 좌표(mm)입니다.</summary>
    public double Z { get; set; }

    /// <summary>구간이 금형 위인지 외부인지 나타냅니다.</summary>
    public GeometryDomain GeometryDomain { get; set; }

    /// <summary>발열사 연장 또는 별도 연결선 역할입니다.</summary>
    public ConductorRole ConductorRole { get; set; }

    /// <summary>실제 경로 길이에 더할 추가 여유(mm)입니다.</summary>
    public double AdditionalAllowanceMm { get; set; }

    /// <summary>이 구간 시작에서 금형 이탈을 표시합니다.</summary>
    public bool IsMoldExitTransition { get; set; }

    /// <summary>측정 끝점과 확정 구간 속성으로 편집 행을 만듭니다.</summary>
    public ConnectionPathLegRow(
        Vec3 endPoint,
        GeometryDomain geometryDomain,
        ConductorRole conductorRole,
        double additionalAllowanceMm,
        bool isMoldExitTransition)
    {
        X = endPoint.X;
        Y = endPoint.Y;
        Z = endPoint.Z;
        GeometryDomain = geometryDomain;
        ConductorRole = conductorRole;
        AdditionalAllowanceMm = additionalAllowanceMm;
        IsMoldExitTransition = isMoldExitTransition;
    }
}

/// <summary>내부 경유점 제거 요청의 처리 결과입니다.</summary>
internal enum ConnectionPathWaypointRemovalResult
{
    /// <summary>내부 경유점을 제거했습니다.</summary>
    Removed,

    /// <summary>제거할 행이 선택되지 않았습니다.</summary>
    NoSelection,

    /// <summary>시작 표면점이나 마지막 외부점은 제거할 수 없습니다.</summary>
    EndpointProtected
}

/// <summary>연결 경로 편집 표의 행과 입력 전용 조작을 소유합니다.</summary>
internal sealed class ConnectionPathEditorViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<ConnectionPathLegRow> _legs = new();
    private ConnectionPathChoice _selectedChoice;
    private bool _isRefreshingChoices;

    /// <summary>편집할 수 있는 확정 연결 경로 선택 항목입니다.</summary>
    public ObservableCollection<ConnectionPathChoice> Choices { get; } = new();

    /// <summary>연결 경로 선택 목록을 갱신하는 중인지 나타냅니다.</summary>
    public bool IsRefreshingChoices => _isRefreshingChoices;

    /// <summary>WPF 편집 표에 표시할 순서가 있는 경로 행입니다.</summary>
    public ObservableCollection<ConnectionPathLegRow> Legs => _legs;

    /// <summary>현재 편집 대상으로 선택한 연결 경로입니다.</summary>
    public ConnectionPathChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (Equals(_selectedChoice, value))
                return;

            _selectedChoice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
        }
    }

    /// <summary>선택 항목 속성 변경을 WPF binding에 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>확정 프로젝트의 연결 경로를 revision stamp와 함께 선택 목록으로 만듭니다.</summary>
    public static ConnectionPathChoice[] CreateChoices(DesignProject project)
    {
        if (project is null)
            return Array.Empty<ConnectionPathChoice>();

        var pathsByPoint = project.ConnectionPaths.ToDictionary(path => path.ConnectionPointId);
        var circuitsById = project.Circuits.ToDictionary(circuit => circuit.Id);
        return project.ConnectionPoints
            .Where(point => pathsByPoint.ContainsKey(point.Id) && circuitsById.ContainsKey(point.CircuitId))
            .OrderBy(point => circuitsById[point.CircuitId].Sequence)
            .ThenBy(point => point.Role)
            .Select(point => new ConnectionPathChoice(
                point.Id,
                project.Revision,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "회로 {0} · {1} · 경로 {2}구간",
                    circuitsById[point.CircuitId].Sequence + 1,
                    point.Role == ConnectionPointRole.Start ? "시작" : "끝",
                    pathsByPoint[point.Id].Segments.Count)))
            .ToArray();
    }

    /// <summary>새 설계 revision의 경로 목록을 채우고 기존 선택 ID를 가능한 한 유지합니다.</summary>
    public void RefreshChoices(DesignProject project)
    {
        ConnectionPointId? previousConnectionPointId = _selectedChoice?.ConnectionPointId;
        ConnectionPathChoice[] choices = CreateChoices(project);

        bool wasRefreshingChoices = _isRefreshingChoices;
        _isRefreshingChoices = true;
        try
        {
            Choices.Clear();
            foreach (ConnectionPathChoice choice in choices)
                Choices.Add(choice);

            SelectedChoice = previousConnectionPointId is { } connectionPointId
                ? choices.FirstOrDefault(choice => choice.ConnectionPointId == connectionPointId)
                    ?? choices.FirstOrDefault()
                : choices.FirstOrDefault();
        }
        finally
        {
            _isRefreshingChoices = wasRefreshingChoices;
        }
    }

    /// <summary>측정 snapshot의 끝점과 확정 구간 역할로 편집 행을 만듭니다.</summary>
    public static ConnectionPathLegRow[] CreateRows(ConnectionPath path, GeometrySnapshot geometry)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(geometry);

        return path.Segments
            .OrderBy(segment => segment.Sequence)
            .Select(segment => CreateRow(segment, geometry))
            .ToArray();
    }

    /// <summary>선택한 경로를 표에 다시 불러옵니다.</summary>
    public void LoadRows(ConnectionPath path, GeometrySnapshot geometry)
    {
        ConnectionPathLegRow[] rows = CreateRows(path, geometry);
        _legs.Clear();
        foreach (ConnectionPathLegRow row in rows)
            _legs.Add(row);
    }

    /// <summary>선택을 잃거나 revision이 바뀐 경우 편집 행을 비웁니다.</summary>
    public void ClearRows() => _legs.Clear();

    /// <summary>금형 이탈 뒤 기존 종점 앞에 외부 경유점을 추가합니다.</summary>
    public bool TryAddWaypoint()
    {
        int transitionIndex = FindMoldExitTransitionIndex();
        if (_legs.Count < 2 || transitionIndex < 1 || transitionIndex >= _legs.Count)
            return false;

        int insertIndex = _legs.Count - 1;
        ConnectionPathLegRow start = _legs[insertIndex - 1];
        ConnectionPathLegRow end = _legs[insertIndex];
        var waypoint = new Vec3(
            CalculateMidpoint(start.X, end.X),
            CalculateMidpoint(start.Y, end.Y),
            CalculateMidpoint(start.Z, end.Z));
        bool beginsMoldExit = transitionIndex == insertIndex;

        _legs.Insert(
            insertIndex,
            new ConnectionPathLegRow(
                waypoint,
                GeometryDomain.External,
                ConductorRole.SeparateConnectionWire,
                additionalAllowanceMm: 0,
                isMoldExitTransition: beginsMoldExit));
        if (beginsMoldExit)
            end.IsMoldExitTransition = false;

        return true;
    }

    /// <summary>선택한 내부 경유점을 제거하고 금형 이탈 표식의 유일성을 유지합니다.</summary>
    public ConnectionPathWaypointRemovalResult RemoveWaypoint(ConnectionPathLegRow selectedRow)
    {
        if (selectedRow is null)
            return ConnectionPathWaypointRemovalResult.NoSelection;

        int index = _legs.IndexOf(selectedRow);
        if (index <= 0 || index >= _legs.Count - 1)
            return ConnectionPathWaypointRemovalResult.EndpointProtected;

        if (selectedRow.IsMoldExitTransition)
            _legs[index + 1].IsMoldExitTransition = true;
        _legs.RemoveAt(index);
        return ConnectionPathWaypointRemovalResult.Removed;
    }

    /// <summary>편집 행을 검증해 계획 요청으로 복사하고 첫 오류 행 번호를 반환합니다.</summary>
    public bool TryCreateRequests(
        out ConnectionPathLegRequest[] requests,
        out int invalidRowNumber)
    {
        requests = Array.Empty<ConnectionPathLegRequest>();
        invalidRowNumber = 0;
        var copiedRequests = new ConnectionPathLegRequest[_legs.Count];
        for (int index = 0; index < _legs.Count; index++)
        {
            ConnectionPathLegRow row = _legs[index];
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y) || !double.IsFinite(row.Z) ||
                !double.IsFinite(row.AdditionalAllowanceMm) || row.AdditionalAllowanceMm < 0 ||
                !Enum.IsDefined(row.GeometryDomain) || !Enum.IsDefined(row.ConductorRole))
            {
                invalidRowNumber = index + 1;
                return false;
            }

            copiedRequests[index] = new ConnectionPathLegRequest(
                new Vec3(row.X, row.Y, row.Z),
                row.GeometryDomain,
                row.ConductorRole,
                new LengthMm(row.AdditionalAllowanceMm),
                row.IsMoldExitTransition);
        }

        requests = copiedRequests;
        return true;
    }

    /// <summary>직선 곡선의 완전한 측정 끝점과 확정 속성으로 표 행을 만듭니다.</summary>
    private static ConnectionPathLegRow CreateRow(
        ConnectionPathSegment segment,
        GeometrySnapshot geometry)
    {
        if (segment.Curve is not LineCurveDefinition)
        {
            throw new InvalidOperationException(
                "현재 연결 경로에 직선이 아닌 구간이 있습니다. 이 경로는 기존 편집기로 바꾸지 않습니다.");
        }

        CurveGeometrySnapshot measured = geometry.Curves.SingleOrDefault(curve =>
            curve.ConnectionPathSegmentId == segment.Id);
        if (measured is null || !measured.Approximation.IsComplete)
        {
            throw new InvalidOperationException(
                "연결 경로의 완전한 Native 측정 끝점이 없습니다. 기하를 다시 확정한 뒤 편집하세요.");
        }

        Vec3 measuredEnd = segment.Curve.Direction == CurveDirection.Forward
            ? measured.End
            : measured.Start;
        return new ConnectionPathLegRow(
            measuredEnd,
            segment.GeometryDomain,
            segment.ConductorRole,
            segment.AdditionalAllowanceMm.Value,
            segment.IsMoldExitTransition);
    }

    /// <summary>표식이 하나뿐인 금형 이탈 구간의 행 번호를 찾습니다.</summary>
    private int FindMoldExitTransitionIndex()
    {
        int[] transitionIndices = _legs
            .Select((row, index) => (row, index))
            .Where(item => item.row.IsMoldExitTransition)
            .Select(item => item.index)
            .ToArray();
        return transitionIndices.Length == 1 ? transitionIndices[0] : -1;
    }

    /// <summary>유한한 두 좌표의 중점을 덧셈 overflow 없이 계산합니다.</summary>
    private static double CalculateMidpoint(double first, double second) => first / 2 + second / 2;
}
