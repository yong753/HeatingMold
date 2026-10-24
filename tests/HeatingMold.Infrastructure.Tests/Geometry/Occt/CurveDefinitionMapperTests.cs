using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>Core 곡선 정의가 표본화 없이 OCCT 경계 DTO를 왕복하는지 검증합니다.</summary>
public sealed class CurveDefinitionMapperTests
{
    private readonly CurveDefinitionMapper _mapper = new();

    /// <summary>직선의 양 끝점과 역방향 정의를 그대로 보존합니다.</summary>
    [Fact]
    public void RoundTrip_LinePreservesEndpointsAndDirection()
    {
        var source = new LineCurveDefinition(new Vec3(1, 2, 3), new Vec3(4, 5, 6), CurveDirection.Reversed);

        CurveDefinitionBridgeDto dto = _mapper.ToBridgeDto(source);
        var restored = Assert.IsType<LineCurveDefinition>(_mapper.FromBridgeDto(dto));

        Assert.Equal([1, 2, 3], dto.Start);
        Assert.Equal([4, 5, 6], dto.End);
        Assert.Equal(source.Start, restored.Start);
        Assert.Equal(source.End, restored.End);
        Assert.Equal(source.Direction, restored.Direction);
    }

    /// <summary>다중선의 점 순서와 방향을 평탄 배열 왕복 후에도 보존합니다.</summary>
    [Fact]
    public void RoundTrip_PolylinePreservesPointOrder()
    {
        var source = new PolylineCurveDefinition(
            [new Vec3(0, 1, 2), new Vec3(3, 4, 5), new Vec3(6, 7, 8)],
            CurveDirection.Reversed);

        CurveDefinitionBridgeDto dto = _mapper.ToBridgeDto(source);
        var restored = Assert.IsType<PolylineCurveDefinition>(_mapper.FromBridgeDto(dto));

        Assert.Equal("Polyline", dto.Kind);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], dto.Points);
        Assert.Equal(source.Points, restored.Points);
        Assert.Equal(source.Direction, restored.Direction);
    }

    /// <summary>원호의 오른손 프레임, 반지름과 각도 구간을 보존합니다.</summary>
    [Fact]
    public void RoundTrip_ArcPreservesFrameAndParameters()
    {
        var plane = new ProjectionFrame(new Vec3(10, 20, 30), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        var source = new ArcCurveDefinition(
            plane,
            new Vec3(12, 24, 30),
            new LengthMm(5.5),
            0.25,
            1.75,
            CurveDirection.Reversed);

        CurveDefinitionBridgeDto dto = _mapper.ToBridgeDto(source);
        var restored = Assert.IsType<ArcCurveDefinition>(_mapper.FromBridgeDto(dto));

        Assert.Equal(12, dto.Frame?.Count);
        Assert.Equal(5.5, dto.RadiusMm);
        Assert.Equal(source.Plane.Origin, restored.Plane.Origin);
        Assert.Equal(source.Plane.AxisX, restored.Plane.AxisX);
        Assert.Equal(source.Plane.AxisY, restored.Plane.AxisY);
        Assert.Equal(source.Center, restored.Center);
        Assert.Equal(source.StartAngleRad, restored.StartAngleRad);
        Assert.Equal(source.EndAngleRad, restored.EndAngleRad);
        Assert.Equal(source.Direction, restored.Direction);
    }

    /// <summary>유리 B-spline의 극점·가중치·knot·중복도와 trim을 모두 보존합니다.</summary>
    [Fact]
    public void RoundTrip_RationalBSplinePreservesExactDefinition()
    {
        var source = new BSplineCurveDefinition(
            degree: 2,
            poles: [new Vec3(0, 0, 0), new Vec3(2, 4, 0), new Vec3(5, 0, 0)],
            weights: [1, 0.65, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0.2,
            trimEnd: 0.8,
            direction: CurveDirection.Reversed);

        CurveDefinitionBridgeDto dto = _mapper.ToBridgeDto(source);
        var restored = Assert.IsType<BSplineCurveDefinition>(_mapper.FromBridgeDto(dto));

        Assert.Equal(source.Degree, restored.Degree);
        Assert.Equal(source.Poles, restored.Poles);
        Assert.Equal(source.Weights, restored.Weights);
        Assert.Equal(source.Knots, restored.Knots);
        Assert.Equal(source.Multiplicities, restored.Multiplicities);
        Assert.Equal(source.IsPeriodic, restored.IsPeriodic);
        Assert.Equal(source.TrimStart, restored.TrimStart);
        Assert.Equal(source.TrimEnd, restored.TrimEnd);
        Assert.Equal(source.Direction, restored.Direction);
    }

    /// <summary>wire 입력 배열을 DTO가 복사해 외부 변경이 이후 복원 결과에 섞이지 않게 합니다.</summary>
    [Fact]
    public void BridgeDto_CopiesInputArrays()
    {
        double[] endpoints = [1, 2, 3];
        var dto = new CurveDefinitionBridgeDto("Line", "Forward", start: endpoints, end: [4, 5, 6]);
        endpoints[0] = 99;

        var restored = Assert.IsType<LineCurveDefinition>(_mapper.FromBridgeDto(dto));

        Assert.Equal(new Vec3(1, 2, 3), restored.Start);
    }

    /// <summary>형식에 맞지 않는 평탄 좌표 수를 임의 보정 없이 거부합니다.</summary>
    [Fact]
    public void FromBridgeDto_RejectsMalformedPolylineCoordinates()
    {
        var dto = new CurveDefinitionBridgeDto("Polyline", "Forward", points: [0, 0, 0, 1]);

        Assert.Throws<InvalidDataException>(() => _mapper.FromBridgeDto(dto));
    }

    /// <summary>곡선 종류와 맞지 않는 추가 payload가 섞인 입력을 거부합니다.</summary>
    [Fact]
    public void FromBridgeDto_RejectsPayloadFromAnotherCurveKind()
    {
        var dto = new CurveDefinitionBridgeDto(
            "Line",
            "Forward",
            start: [0, 0, 0],
            end: [1, 0, 0],
            points: [0, 0, 0, 1, 0, 0]);

        Assert.Throws<InvalidDataException>(() => _mapper.FromBridgeDto(dto));
    }

    /// <summary>열거형의 숫자 표기를 안정 wire 이름으로 오인하지 않습니다.</summary>
    [Fact]
    public void FromBridgeDto_RejectsNumericEnumLabels()
    {
        var dto = new CurveDefinitionBridgeDto("0", "0", start: [0, 0, 0], end: [1, 0, 0]);

        Assert.Throws<InvalidDataException>(() => _mapper.FromBridgeDto(dto));
    }

    /// <summary>수직축 조건을 만족하지 않는 원호 프레임을 거부합니다.</summary>
    [Fact]
    public void FromBridgeDto_RejectsInvalidArcFrame()
    {
        var dto = new CurveDefinitionBridgeDto(
            "Arc",
            "Forward",
            frame: [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, -1],
            center: [0, 0, 0],
            radiusMm: 2,
            startAngleRad: 0,
            endAngleRad: 1);

        Assert.Throws<InvalidDataException>(() => _mapper.FromBridgeDto(dto));
    }

    /// <summary>차수나 knot 구조가 올바르지 않은 B-spline 입력을 거부합니다.</summary>
    [Fact]
    public void FromBridgeDto_RejectsInvalidBSplineStructure()
    {
        var dto = new CurveDefinitionBridgeDto(
            "BSpline",
            "Forward",
            degree: 2,
            poles: [0, 0, 0, 1, 1, 0, 2, 0, 0],
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 2],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);

        Assert.Throws<InvalidDataException>(() => _mapper.FromBridgeDto(dto));
    }
}
