using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Core 곡선 정의와 OCCT 경계 wire 데이터 사이를 손실 없이 변환합니다.</summary>
public sealed class CurveDefinitionMapper
{
    private const double FrameTolerance = 1e-9;

    /// <summary>Core 곡선의 형식별 수학 정의를 평탄 wire 데이터로 옮깁니다.</summary>
    public CurveDefinitionBridgeDto ToBridgeDto(CurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string kind = definition.Kind.ToString();
        string direction = definition.Direction.ToString();
        return definition switch
        {
            LineCurveDefinition line => new CurveDefinitionBridgeDto(
                kind,
                direction,
                start: Flatten(line.Start),
                end: Flatten(line.End)),
            PolylineCurveDefinition polyline => new CurveDefinitionBridgeDto(
                kind,
                direction,
                points: Flatten(polyline.Points)),
            ArcCurveDefinition arc => new CurveDefinitionBridgeDto(
                kind,
                direction,
                frame: Flatten(arc.Plane),
                center: Flatten(arc.Center),
                radiusMm: arc.RadiusMm.Value,
                startAngleRad: arc.StartAngleRad,
                endAngleRad: arc.EndAngleRad),
            BSplineCurveDefinition spline => new CurveDefinitionBridgeDto(
                kind,
                direction,
                degree: spline.Degree,
                poles: Flatten(spline.Poles),
                weights: spline.Weights,
                knots: spline.Knots,
                multiplicities: spline.Multiplicities,
                isPeriodic: spline.IsPeriodic,
                trimStart: spline.TrimStart,
                trimEnd: spline.TrimEnd),
            _ => throw new NotSupportedException($"지원하지 않는 곡선 정의 형식입니다: {definition.GetType().FullName}.")
        };
    }

    /// <summary>wire 배열을 검증한 뒤 Core의 정확한 곡선 수학 정의로 복원합니다.</summary>
    public CurveDefinition FromBridgeDto(CurveDefinitionBridgeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        CurveKind kind = ParseEnum<CurveKind>(dto.Kind, "kind");
        CurveDirection direction = ParseEnum<CurveDirection>(dto.Direction, "direction");

        try
        {
            return kind switch
            {
                CurveKind.Line => ReadLine(dto, direction),
                CurveKind.Polyline => ReadPolyline(dto, direction),
                CurveKind.Arc => ReadArc(dto, direction),
                CurveKind.BSpline => ReadBSpline(dto, direction),
                _ => throw InvalidPayload("지원하지 않는 곡선 종류입니다.")
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            throw new InvalidDataException("곡선 wire 데이터가 유효한 Core 정의를 만들지 못했습니다.", exception);
        }
    }

    private static LineCurveDefinition ReadLine(CurveDefinitionBridgeDto dto, CurveDirection direction)
    {
        EnsurePayload(dto, nameof(dto.Start), nameof(dto.End));
        return new LineCurveDefinition(ReadVector(dto.Start, "start"), ReadVector(dto.End, "end"), direction);
    }

    private static PolylineCurveDefinition ReadPolyline(CurveDefinitionBridgeDto dto, CurveDirection direction)
    {
        EnsurePayload(dto, nameof(dto.Points));
        double[] flatPoints = Required(dto.Points, "points").ToArray();
        if (flatPoints.Length < 6 || flatPoints.Length % 3 != 0)
            throw InvalidPayload("다중선 점 배열은 XYZ 세 성분으로 된 두 점 이상이어야 합니다.");

        Vec3[] points = new Vec3[flatPoints.Length / 3];
        for (int index = 0; index < points.Length; index++)
            points[index] = ReadVectorAt(flatPoints, index * 3);

        return new PolylineCurveDefinition(points, direction);
    }

    private static ArcCurveDefinition ReadArc(CurveDefinitionBridgeDto dto, CurveDirection direction)
    {
        EnsurePayload(
            dto,
            nameof(dto.Frame),
            nameof(dto.Center),
            nameof(dto.RadiusMm),
            nameof(dto.StartAngleRad),
            nameof(dto.EndAngleRad));

        ProjectionFrame plane = ReadFrame(dto.Frame);
        return new ArcCurveDefinition(
            plane,
            ReadVector(dto.Center, "center"),
            new LengthMm(Required(dto.RadiusMm, "radiusMm")),
            Required(dto.StartAngleRad, "startAngleRad"),
            Required(dto.EndAngleRad, "endAngleRad"),
            direction);
    }

    private static BSplineCurveDefinition ReadBSpline(CurveDefinitionBridgeDto dto, CurveDirection direction)
    {
        EnsurePayload(
            dto,
            nameof(dto.Degree),
            nameof(dto.Poles),
            nameof(dto.Weights),
            nameof(dto.Knots),
            nameof(dto.Multiplicities),
            nameof(dto.IsPeriodic),
            nameof(dto.TrimStart),
            nameof(dto.TrimEnd));

        double[] flatPoles = Required(dto.Poles, "poles").ToArray();
        if (flatPoles.Length < 6 || flatPoles.Length % 3 != 0)
            throw InvalidPayload("B-spline 극점 배열은 XYZ 세 성분으로 된 두 점 이상이어야 합니다.");

        Vec3[] poles = new Vec3[flatPoles.Length / 3];
        for (int index = 0; index < poles.Length; index++)
            poles[index] = ReadVectorAt(flatPoles, index * 3);

        return new BSplineCurveDefinition(
            Required(dto.Degree, "degree"),
            poles,
            Required(dto.Weights, "weights"),
            Required(dto.Knots, "knots"),
            Required(dto.Multiplicities, "multiplicities"),
            Required(dto.IsPeriodic, "isPeriodic"),
            Required(dto.TrimStart, "trimStart"),
            Required(dto.TrimEnd, "trimEnd"),
            direction);
    }

    private static ProjectionFrame ReadFrame(IReadOnlyList<double>? values)
    {
        double[] frame = Required(values, "frame").ToArray();
        if (frame.Length != 12)
            throw InvalidPayload("원호 프레임 배열에는 원점, X축, Y축, 법선의 12개 성분이 필요합니다.");

        Vec3 origin = ReadVectorAt(frame, 0);
        Vec3 axisX = ReadVectorAt(frame, 3);
        Vec3 axisY = ReadVectorAt(frame, 6);
        Vec3 normal = ReadVectorAt(frame, 9);

        if (Math.Abs(axisX.Length - 1) > FrameTolerance ||
            Math.Abs(axisY.Length - 1) > FrameTolerance ||
            Math.Abs(normal.Length - 1) > FrameTolerance ||
            Math.Abs(Vec3.Dot(axisX, axisY)) > FrameTolerance ||
            Math.Abs(Vec3.Dot(axisX, normal)) > FrameTolerance ||
            Math.Abs(Vec3.Dot(axisY, normal)) > FrameTolerance ||
            (Vec3.Cross(normal, axisX) - axisY).Length > FrameTolerance)
        {
            throw InvalidPayload("원호 프레임은 서로 직교하는 오른손 단위축이어야 합니다.");
        }

        return new ProjectionFrame(origin, normal, axisX);
    }

    private static Vec3 ReadVector(IReadOnlyList<double>? values, string fieldName)
    {
        double[] vector = Required(values, fieldName).ToArray();
        if (vector.Length != 3)
            throw InvalidPayload($"{fieldName} 배열에는 XYZ 세 성분이 필요합니다.");

        return ReadVectorAt(vector, 0);
    }

    private static Vec3 ReadVectorAt(double[] values, int offset) =>
        new(values[offset], values[offset + 1], values[offset + 2]);

    private static IReadOnlyList<double> Flatten(Vec3 point) => [point.X, point.Y, point.Z];

    private static IReadOnlyList<double> Flatten(IEnumerable<Vec3> points) =>
        points.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToArray();

    private static IReadOnlyList<double> Flatten(ProjectionFrame frame) =>
    [
        frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
        frame.AxisX.X, frame.AxisX.Y, frame.AxisX.Z,
        frame.AxisY.X, frame.AxisY.Y, frame.AxisY.Z,
        frame.Normal.X, frame.Normal.Y, frame.Normal.Z
    ];

    private static TEnum ParseEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(Enum.GetName(parsed), value, StringComparison.Ordinal))
        {
            throw InvalidPayload($"{fieldName} 값이 지원되지 않습니다: {value ?? "<null>"}.");
        }

        return parsed;
    }

    private static void EnsurePayload(CurveDefinitionBridgeDto dto, params string[] expectedFields)
    {
        HashSet<string> expected = new(expectedFields, StringComparer.Ordinal);
        string[] allFields =
        [
            nameof(dto.Start), nameof(dto.End), nameof(dto.Points), nameof(dto.Frame), nameof(dto.Center),
            nameof(dto.RadiusMm), nameof(dto.StartAngleRad), nameof(dto.EndAngleRad), nameof(dto.Degree),
            nameof(dto.Poles), nameof(dto.Weights), nameof(dto.Knots), nameof(dto.Multiplicities),
            nameof(dto.IsPeriodic), nameof(dto.TrimStart), nameof(dto.TrimEnd)
        ];

        foreach (string field in allFields)
        {
            if (HasPayload(dto, field) != expected.Contains(field))
                throw InvalidPayload($"곡선 종류와 payload 필드 구성이 일치하지 않습니다: {field}.");
        }
    }

    private static bool HasPayload(CurveDefinitionBridgeDto dto, string fieldName) => fieldName switch
    {
        nameof(dto.Start) => dto.Start is not null,
        nameof(dto.End) => dto.End is not null,
        nameof(dto.Points) => dto.Points is not null,
        nameof(dto.Frame) => dto.Frame is not null,
        nameof(dto.Center) => dto.Center is not null,
        nameof(dto.RadiusMm) => dto.RadiusMm.HasValue,
        nameof(dto.StartAngleRad) => dto.StartAngleRad.HasValue,
        nameof(dto.EndAngleRad) => dto.EndAngleRad.HasValue,
        nameof(dto.Degree) => dto.Degree.HasValue,
        nameof(dto.Poles) => dto.Poles is not null,
        nameof(dto.Weights) => dto.Weights is not null,
        nameof(dto.Knots) => dto.Knots is not null,
        nameof(dto.Multiplicities) => dto.Multiplicities is not null,
        nameof(dto.IsPeriodic) => dto.IsPeriodic.HasValue,
        nameof(dto.TrimStart) => dto.TrimStart.HasValue,
        nameof(dto.TrimEnd) => dto.TrimEnd.HasValue,
        _ => throw new ArgumentOutOfRangeException(nameof(fieldName), fieldName, "알 수 없는 곡선 payload 필드입니다.")
    };

    private static T Required<T>(T? value, string fieldName)
        where T : struct => value ?? throw InvalidPayload($"필수 곡선 필드가 없습니다: {fieldName}.");

    private static IReadOnlyList<T> Required<T>(IReadOnlyList<T>? value, string fieldName) =>
        value ?? throw InvalidPayload($"필수 곡선 배열이 없습니다: {fieldName}.");

    private static InvalidDataException InvalidPayload(string message) => new(message);
}
