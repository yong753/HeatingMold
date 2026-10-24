using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Geometry;

/// <summary>원본 3차원 중심 곡선의 수학적 정의입니다.</summary>
public abstract record CurveDefinition
{
    /// <summary>곡선 정의의 영속 형식 표식입니다.</summary>
    public abstract CurveKind Kind { get; }

    /// <summary>곡선이 저장된 진행 방향입니다.</summary>
    public CurveDirection Direction { get; }

    /// <summary>명시한 곡선 방향을 보관하는 기본 정의를 만듭니다.</summary>
    protected CurveDefinition(CurveDirection direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));

        Direction = direction;
    }
}

/// <summary>서로 다른 곡선 정의의 형식 구분입니다.</summary>
public enum CurveKind
{
    /// <summary>직선입니다.</summary>
    Line,

    /// <summary>점 순서를 가진 다중선입니다.</summary>
    Polyline,

    /// <summary>평면에 놓인 원호입니다.</summary>
    Arc,

    /// <summary>가중치와 knot를 가진 B-spline입니다.</summary>
    BSpline
}

/// <summary>곡선 파라미터를 따라가는 정방향 또는 역방향입니다.</summary>
public enum CurveDirection
{
    /// <summary>정의된 시작 파라미터에서 끝 파라미터로 진행합니다.</summary>
    Forward,

    /// <summary>정의된 끝 파라미터에서 시작 파라미터로 진행합니다.</summary>
    Reversed
}

/// <summary>서로 다른 두 점을 잇는 직선 정의입니다.</summary>
public sealed record LineCurveDefinition : CurveDefinition
{
    /// <summary>직선의 시작점(mm)입니다.</summary>
    public Vec3 Start { get; }

    /// <summary>직선의 끝점(mm)입니다.</summary>
    public Vec3 End { get; }

    /// <summary>시작점과 끝점이 다른 직선 곡선을 만듭니다.</summary>
    public LineCurveDefinition(Vec3 start, Vec3 end, CurveDirection direction = CurveDirection.Forward)
        : base(direction)
    {
        if (start == end)
            throw new ArgumentException("직선 곡선은 길이가 0일 수 없습니다.");

        Start = start;
        End = end;
    }

    /// <summary>곡선 정의 종류를 직선으로 표시합니다.</summary>
    public override CurveKind Kind => CurveKind.Line;
}

/// <summary>순서가 보존된 3차원 점들의 다중선 정의입니다.</summary>
public sealed record PolylineCurveDefinition : CurveDefinition
{
    /// <summary>다중선을 이루는 점의 읽기 전용 순서 목록입니다.</summary>
    public IReadOnlyList<Vec3> Points { get; }

    /// <summary>인접 중복점을 포함하지 않는 다중선 곡선을 만듭니다.</summary>
    public PolylineCurveDefinition(IEnumerable<Vec3> points, CurveDirection direction = CurveDirection.Forward)
        : base(direction)
    {
        Vec3[] copiedPoints = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copiedPoints.Length < 2)
            throw new ArgumentException("다중선에는 두 개 이상의 점이 필요합니다.", nameof(points));
        if (copiedPoints.Zip(copiedPoints.Skip(1)).Any(pair => pair.First == pair.Second))
            throw new ArgumentException("다중선에 연속으로 같은 점을 둘 수 없습니다.", nameof(points));

        Points = new ReadOnlyCollection<Vec3>(copiedPoints);
    }

    /// <summary>곡선 정의 종류를 다중선으로 표시합니다.</summary>
    public override CurveKind Kind => CurveKind.Polyline;
}

/// <summary>오른손 평면 프레임과 각도 구간으로 정의한 원호입니다.</summary>
public sealed record ArcCurveDefinition : CurveDefinition
{
    /// <summary>원호가 놓이는 모델 좌표 평면입니다.</summary>
    public ProjectionFrame Plane { get; }

    /// <summary>원호의 중심점(mm)입니다.</summary>
    public Vec3 Center { get; }

    /// <summary>양의 원호 반지름(mm)입니다.</summary>
    public LengthMm RadiusMm { get; }

    /// <summary>시작 매개변수(라디안)입니다.</summary>
    public double StartAngleRad { get; }

    /// <summary>끝 매개변수(라디안)입니다.</summary>
    public double EndAngleRad { get; }

    /// <summary>양의 반지름과 서로 다른 유한 각도로 원호를 만듭니다.</summary>
    public ArcCurveDefinition(
        ProjectionFrame plane,
        Vec3 center,
        LengthMm radiusMm,
        double startAngleRad,
        double endAngleRad,
        CurveDirection direction = CurveDirection.Forward)
        : base(direction)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (!double.IsFinite(radiusMm.Value) || radiusMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusMm), "원호 반지름은 양수여야 합니다.");
        if (!double.IsFinite(startAngleRad) || !double.IsFinite(endAngleRad) || startAngleRad == endAngleRad)
            throw new ArgumentException("원호의 시작·끝 각도는 서로 다른 유한 라디안 값이어야 합니다.");

        Plane = plane;
        Center = center;
        RadiusMm = radiusMm;
        StartAngleRad = startAngleRad;
        EndAngleRad = endAngleRad;
    }

    /// <summary>곡선 정의 종류를 원호로 표시합니다.</summary>
    public override CurveKind Kind => CurveKind.Arc;
}

/// <summary>극점·가중치·knot·trim 구간을 가진 B-spline 곡선입니다.</summary>
public sealed record BSplineCurveDefinition : CurveDefinition
{
    /// <summary>곡선 다항식 차수입니다.</summary>
    public int Degree { get; }

    /// <summary>곡선을 정의하는 극점입니다.</summary>
    public IReadOnlyList<Vec3> Poles { get; }

    /// <summary>극점마다 대응하는 양의 유리 가중치입니다.</summary>
    public IReadOnlyList<double> Weights { get; }

    /// <summary>중복도를 합치기 전 오름차순 knot 값입니다.</summary>
    public IReadOnlyList<double> Knots { get; }

    /// <summary>knot마다 대응하는 양의 정수 중복도입니다.</summary>
    public IReadOnlyList<int> Multiplicities { get; }

    /// <summary>주기 곡선인지 나타냅니다.</summary>
    public bool IsPeriodic { get; }

    /// <summary>저장할 시작 trim 파라미터입니다.</summary>
    public double TrimStart { get; }

    /// <summary>저장할 끝 trim 파라미터입니다.</summary>
    public double TrimEnd { get; }

    /// <summary>배열 짝, 유리 가중치, knot 순서와 trim 구간을 검증해 곡선을 만듭니다.</summary>
    public BSplineCurveDefinition(
        int degree,
        IEnumerable<Vec3> poles,
        IEnumerable<double> weights,
        IEnumerable<double> knots,
        IEnumerable<int> multiplicities,
        bool isPeriodic,
        double trimStart,
        double trimEnd,
        CurveDirection direction = CurveDirection.Forward)
        : base(direction)
    {
        Vec3[] copiedPoles = poles?.ToArray() ?? throw new ArgumentNullException(nameof(poles));
        double[] copiedWeights = weights?.ToArray() ?? throw new ArgumentNullException(nameof(weights));
        double[] copiedKnots = knots?.ToArray() ?? throw new ArgumentNullException(nameof(knots));
        int[] copiedMultiplicities = multiplicities?.ToArray() ?? throw new ArgumentNullException(nameof(multiplicities));

        if (degree < 1 || degree >= copiedPoles.Length)
            throw new ArgumentOutOfRangeException(nameof(degree), "차수는 1 이상이며 극점 수보다 작아야 합니다.");
        if (copiedWeights.Length != copiedPoles.Length || copiedWeights.Any(weight => !double.IsFinite(weight) || weight <= 0))
            throw new ArgumentException("극점마다 양의 유한 가중치가 하나씩 필요합니다.", nameof(weights));
        if (copiedKnots.Length < 2 || copiedKnots.Length != copiedMultiplicities.Length)
            throw new ArgumentException("knot와 중복도 배열에는 두 개 이상의 대응 항목이 필요합니다.");
        if (copiedKnots.Any(knot => !double.IsFinite(knot)) ||
            copiedKnots.Zip(copiedKnots.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            throw new ArgumentException("knot 값은 유한하며 엄격히 증가해야 합니다.", nameof(knots));
        }
        if (copiedMultiplicities.Any(value => value < 1 || value > degree + 1))
            throw new ArgumentOutOfRangeException(nameof(multiplicities), "knot 중복도는 1부터 차수+1 사이여야 합니다.");
        if (copiedMultiplicities.Skip(1).Take(copiedMultiplicities.Length - 2).Any(value => value > degree))
            throw new ArgumentException("내부 knot 중복도는 곡선 차수를 넘을 수 없습니다.", nameof(multiplicities));
        if (isPeriodic && copiedMultiplicities[0] != copiedMultiplicities[^1])
            throw new ArgumentException("주기 곡선의 첫 knot와 마지막 knot 중복도는 같아야 합니다.", nameof(multiplicities));

        // 비주기는 전체 중복도 합을, 주기는 반복 seam 하나를 제외한 합을 극점 수와 맞춥니다.
        long totalMultiplicity = copiedMultiplicities.Sum(value => (long)value);
        long expectedMultiplicity = isPeriodic
            ? (long)copiedPoles.Length + copiedMultiplicities[0]
            : (long)copiedPoles.Length + degree + 1;
        if (totalMultiplicity != expectedMultiplicity)
            throw new ArgumentException("knot 중복도 합이 극점 수와 차수에 맞지 않습니다.", nameof(multiplicities));

        if (!double.IsFinite(trimStart) || !double.IsFinite(trimEnd) || trimStart < copiedKnots[0] ||
            trimEnd > copiedKnots[^1] || trimStart >= trimEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(trimStart), "trim 구간은 knot 범위 안의 증가하는 유한 구간이어야 합니다.");
        }
        if (isPeriodic && (copiedMultiplicities[0] > degree || copiedMultiplicities[^1] > degree))
            throw new ArgumentException("주기 곡선의 양 끝 knot 중복도는 차수보다 클 수 없습니다.", nameof(multiplicities));

        Degree = degree;
        Poles = new ReadOnlyCollection<Vec3>(copiedPoles);
        Weights = new ReadOnlyCollection<double>(copiedWeights);
        Knots = new ReadOnlyCollection<double>(copiedKnots);
        Multiplicities = new ReadOnlyCollection<int>(copiedMultiplicities);
        IsPeriodic = isPeriodic;
        TrimStart = trimStart;
        TrimEnd = trimEnd;
    }

    /// <summary>곡선 정의 종류를 B-spline으로 표시합니다.</summary>
    public override CurveKind Kind => CurveKind.BSpline;
}
