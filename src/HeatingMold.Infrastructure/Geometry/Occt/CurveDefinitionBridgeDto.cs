using System.Collections.ObjectModel;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>OCCT 어댑터 경계를 통과하는 곡선 정의 wire 데이터입니다.</summary>
public sealed class CurveDefinitionBridgeDto
{
    /// <summary>곡선 종류를 나타내는 안정 문자열입니다.</summary>
    public string? Kind { get; }

    /// <summary>곡선 진행 방향을 나타내는 안정 문자열입니다.</summary>
    public string? Direction { get; }

    /// <summary>직선 시작점을 담은 XYZ 배열입니다.</summary>
    public IReadOnlyList<double>? Start { get; }

    /// <summary>직선 끝점을 담은 XYZ 배열입니다.</summary>
    public IReadOnlyList<double>? End { get; }

    /// <summary>다중선 점들을 XYZ 순서로 평탄화한 배열입니다.</summary>
    public IReadOnlyList<double>? Points { get; }

    /// <summary>원호 프레임의 원점, X축, Y축, 법선을 담은 배열입니다.</summary>
    public IReadOnlyList<double>? Frame { get; }

    /// <summary>원호 중심점을 담은 XYZ 배열입니다.</summary>
    public IReadOnlyList<double>? Center { get; }

    /// <summary>원호 반지름(mm)입니다.</summary>
    public double? RadiusMm { get; }

    /// <summary>원호 시작각(라디안)입니다.</summary>
    public double? StartAngleRad { get; }

    /// <summary>원호 끝각(라디안)입니다.</summary>
    public double? EndAngleRad { get; }

    /// <summary>B-spline 차수입니다.</summary>
    public int? Degree { get; }

    /// <summary>B-spline 극점을 XYZ 순서로 평탄화한 배열입니다.</summary>
    public IReadOnlyList<double>? Poles { get; }

    /// <summary>B-spline 극점별 유리 가중치입니다.</summary>
    public IReadOnlyList<double>? Weights { get; }

    /// <summary>B-spline의 오름차순 knot 값입니다.</summary>
    public IReadOnlyList<double>? Knots { get; }

    /// <summary>B-spline knot별 정수 중복도입니다.</summary>
    public IReadOnlyList<int>? Multiplicities { get; }

    /// <summary>B-spline이 주기 곡선인지 나타냅니다.</summary>
    public bool? IsPeriodic { get; }

    /// <summary>B-spline 시작 trim 매개변수입니다.</summary>
    public double? TrimStart { get; }

    /// <summary>B-spline 끝 trim 매개변수입니다.</summary>
    public double? TrimEnd { get; }

    /// <summary>곡선 종류별 수학 정의를 평탄 wire 배열로 복사해 보관합니다.</summary>
    public CurveDefinitionBridgeDto(
        string? kind,
        string? direction,
        IEnumerable<double>? start = null,
        IEnumerable<double>? end = null,
        IEnumerable<double>? points = null,
        IEnumerable<double>? frame = null,
        IEnumerable<double>? center = null,
        double? radiusMm = null,
        double? startAngleRad = null,
        double? endAngleRad = null,
        int? degree = null,
        IEnumerable<double>? poles = null,
        IEnumerable<double>? weights = null,
        IEnumerable<double>? knots = null,
        IEnumerable<int>? multiplicities = null,
        bool? isPeriodic = null,
        double? trimStart = null,
        double? trimEnd = null)
    {
        Kind = kind;
        Direction = direction;
        Start = Copy(start);
        End = Copy(end);
        Points = Copy(points);
        Frame = Copy(frame);
        Center = Copy(center);
        RadiusMm = radiusMm;
        StartAngleRad = startAngleRad;
        EndAngleRad = endAngleRad;
        Degree = degree;
        Poles = Copy(poles);
        Weights = Copy(weights);
        Knots = Copy(knots);
        Multiplicities = Copy(multiplicities);
        IsPeriodic = isPeriodic;
        TrimStart = trimStart;
        TrimEnd = trimEnd;
    }

    private static IReadOnlyList<T>? Copy<T>(IEnumerable<T>? values)
    {
        if (values is null)
            return null;

        return new ReadOnlyCollection<T>(values.ToArray());
    }
}
