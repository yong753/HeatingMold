using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Editing;

namespace HeatingMold.App.ViewModels;

/// <summary>편집할 회전 구간의 안정 ID와 기준 revision을 보관합니다.</summary>
internal sealed record TurnSegmentCurveChoice(
    HeatingPathId PathId,
    HeatingPathSegmentId SegmentId,
    DesignRevision Revision,
    string DisplayName);

/// <summary>다중선 내부점의 표시 순서와 현재 revision 입력 문자열을 보관합니다.</summary>
internal sealed class TurnPolylineInteriorPointInput : INotifyPropertyChanged
{
    private string _x;
    private string _y;
    private string _z;

    /// <summary>경로 진행 방향을 기준으로 한 내부점 번호입니다.</summary>
    public int PointNumber { get; }

    /// <summary>현재 곡선 정의에 한정된 내부점 위치입니다.</summary>
    internal int DefinitionIndex { get; }

    /// <summary>내부점 X 좌표 입력(mm)입니다.</summary>
    public string X
    {
        get => _x;
        set => SetValue(ref _x, value, nameof(X));
    }

    /// <summary>내부점 Y 좌표 입력(mm)입니다.</summary>
    public string Y
    {
        get => _y;
        set => SetValue(ref _y, value, nameof(Y));
    }

    /// <summary>내부점 Z 좌표 입력(mm)입니다.</summary>
    public string Z
    {
        get => _z;
        set => SetValue(ref _z, value, nameof(Z));
    }

    /// <summary>화면 입력 행의 값이 바뀌었음을 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>곡선 정의의 현재 내부점과 표시 번호로 입력 행을 만듭니다.</summary>
    public TurnPolylineInteriorPointInput(int pointNumber, int definitionIndex, Vec3 point)
    {
        PointNumber = pointNumber;
        DefinitionIndex = definitionIndex;
        _x = Format(point.X);
        _y = Format(point.Y);
        _z = Format(point.Z);
    }

    /// <summary>유한 좌표만 현재 문화권의 짧은 왕복 형식으로 입력할 수 있습니다.</summary>
    internal bool TryGetPosition(out Vec3 point)
    {
        point = default;
        if (!TryParse(X, out double x) || !TryParse(Y, out double y) || !TryParse(Z, out double z))
            return false;

        point = new Vec3(x, y, z);
        return true;
    }

    private void SetValue(ref string field, string value, string propertyName)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string Format(double value) => value.ToString("G", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value)
    {
        value = 0;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            double.IsFinite(value);
    }
}

/// <summary>회전 다중선 내부점 입력과 preview 요청의 안정 참조를 소유합니다.</summary>
internal sealed class TurnSegmentCurveEditorViewModel : INotifyPropertyChanged
{
    private DesignProject _project;
    private Vec3[] _definitionPoints = [];
    private CurveDirection _direction;
    private TurnSegmentCurveChoice _selectedChoice;
    private bool _isPreviewActive;

    /// <summary>현재 확정 경로의 편집 가능한 회전 구간 목록입니다.</summary>
    public ObservableCollection<TurnSegmentCurveChoice> Choices { get; } = new();

    /// <summary>선택한 곡선의 내부점 입력 행입니다.</summary>
    public ObservableCollection<TurnPolylineInteriorPointInput> InteriorPoints { get; } = new();

    /// <summary>회전 구간 곡선의 미확정 미리보기가 표시 중인지 나타냅니다.</summary>
    public bool IsPreviewActive
    {
        get => _isPreviewActive;
        private set
        {
            if (_isPreviewActive == value)
                return;

            _isPreviewActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPreviewActive)));
        }
    }

    /// <summary>선택 항목 변경을 화면 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Native 뷰어가 미리보기를 표시한 뒤 편집 상태를 활성화합니다.</summary>
    public void MarkPreviewActive() => IsPreviewActive = true;

    /// <summary>활성 미리보기 상태를 한 번만 해제하고 Native 정리가 필요한지 반환합니다.</summary>
    public bool TryClearPreviewState()
    {
        if (!IsPreviewActive)
            return false;

        IsPreviewActive = false;
        return true;
    }

    /// <summary>현재 편집 대상으로 선택한 안정 경로·구간 참조입니다.</summary>
    public TurnSegmentCurveChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (Equals(_selectedChoice, value))
                return;

            _selectedChoice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
            LoadSelectedCurve();
        }
    }

    /// <summary>확정 설계 갱신 시 안정 ID를 유지하고 현재 내부점 값을 다시 불러옵니다.</summary>
    public void Refresh(DesignProject project)
    {
        HeatingPathId? previousPathId = _selectedChoice?.PathId;
        HeatingPathSegmentId? previousSegmentId = _selectedChoice?.SegmentId;
        _project = project;
        TurnSegmentCurveChoice[] choices = project?.HeatingPaths
            .SelectMany((path, pathIndex) => path.Segments
                .Where(IsEditableTurn)
                .OrderBy(segment => segment.Sequence)
                .Select(segment => new TurnSegmentCurveChoice(
                    path.Id,
                    segment.Id,
                    project.Revision,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "경로 {0} · 회전 {1} · ID {2}",
                        pathIndex + 1,
                        segment.Sequence + 1,
                        segment.Id.Value.ToString("N").Substring(0, 8)))))
            .ToArray() ?? [];

        Choices.Clear();
        foreach (TurnSegmentCurveChoice choice in choices)
            Choices.Add(choice);

        SelectedChoice = previousPathId is { } pathId && previousSegmentId is { } segmentId
            ? choices.FirstOrDefault(choice => choice.PathId == pathId && choice.SegmentId == segmentId)
                ?? choices.FirstOrDefault()
            : choices.FirstOrDefault();
        if (SelectedChoice is null)
            LoadSelectedCurve();
    }

    /// <summary>수정한 내부점으로 stable ID와 revision이 묶인 곡선 요청을 만듭니다.</summary>
    public bool TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic)
    {
        request = null;
        diagnostic = string.Empty;
        if (_project is null || _selectedChoice is null)
        {
            diagnostic = "편집할 활성 회전 구간이 없습니다.";
            return false;
        }
        if (_selectedChoice.Revision != _project.Revision)
        {
            diagnostic = "회전 구간 편집의 기준 설계가 바뀌었습니다.";
            return false;
        }

        var points = _definitionPoints.ToArray();
        foreach (TurnPolylineInteriorPointInput input in InteriorPoints)
        {
            if (!input.TryGetPosition(out Vec3 point))
            {
                diagnostic = string.Concat("회전 내부점 ", input.PointNumber, "의 좌표는 유한한 숫자여야 합니다.");
                return false;
            }

            points[input.DefinitionIndex] = point;
        }

        try
        {
            var curve = new PolylineCurveDefinition(points, _direction);
            request = new TurnSegmentCurveEditRequest(
                _selectedChoice.PathId,
                _selectedChoice.SegmentId,
                _selectedChoice.Revision,
                curve);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostic = "회전 곡선에 같은 위치의 연속 내부점이 있습니다.";
            return false;
        }
    }

    private void LoadSelectedCurve()
    {
        InteriorPoints.Clear();
        _definitionPoints = [];
        _direction = CurveDirection.Forward;
        if (_project is null || _selectedChoice is null)
            return;

        HeatingPathSegment segment = _project.HeatingPaths
            .SingleOrDefault(path => path.Id == _selectedChoice.PathId)?.Segments
            .SingleOrDefault(candidate => candidate.Id == _selectedChoice.SegmentId);
        if (!IsEditableTurn(segment))
            return;

        PolylineCurveDefinition curve = (PolylineCurveDefinition)segment.Curve;
        _definitionPoints = curve.Points.ToArray();
        _direction = curve.Direction;
        int firstInteriorIndex = curve.Direction == CurveDirection.Forward
            ? 1
            : curve.Points.Count - 2;
        int indexStep = curve.Direction == CurveDirection.Forward ? 1 : -1;
        int lastInteriorIndex = curve.Direction == CurveDirection.Forward ? curve.Points.Count - 1 : 0;
        int pointNumber = 2;
        for (int index = firstInteriorIndex; index != lastInteriorIndex; index += indexStep)
        {
            InteriorPoints.Add(new TurnPolylineInteriorPointInput(
                pointNumber++,
                index,
                curve.Points[index]));
        }
    }

    private static bool IsEditableTurn(HeatingPathSegment segment) =>
        segment is not null &&
        segment.Kind == HeatingPathSegmentKind.Turn &&
        segment.IsActive &&
        segment.Curve is PolylineCurveDefinition { Points.Count: >= 3 };
}
