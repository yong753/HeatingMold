using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Tests;

/// <summary>공통 식별자, 단위, revision 계약을 검증합니다.</summary>
public sealed class PrimitiveContractTests
{
    /// <summary>각 엔터티 ID가 비어 있지 않은 독립 식별자를 생성합니다.</summary>
    [Fact]
    public void EntityIds_CreateDistinctNonEmptyValues()
    {
        Guid[] values =
        [
            ProjectId.New().Value,
            FaceRefId.New().Value,
            RegionId.New().Value,
            BoundarySegmentId.New().Value,
            HeatingPathId.New().Value,
            HeatingPathSegmentId.New().Value,
            ControlPointId.New().Value,
            CircuitId.New().Value,
            ConnectionPointId.New().Value,
            ConnectionPathId.New().Value,
            GeometryDocumentId.New().Value,
            CandidateId.New().Value
        ];

        Assert.All(values, value => Assert.NotEqual(Guid.Empty, value));
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    /// <summary>물리량 값은 유한해야 하고 산술 결과의 범위 초과도 허용하지 않습니다.</summary>
    [Fact]
    public void Units_RejectNonFiniteValuesAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LengthMm(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaMm2(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResistanceOhmsPerMeter(double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResistanceOhms(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoltageVrms(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentAmps(double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerWatts(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LengthMm(double.MaxValue) + new LengthMm(double.MaxValue));

        Assert.Equal(1.25, new LengthMm(1_250).ToMeters());
    }

    /// <summary>설계 revision은 음수가 될 수 없고 성공한 변경마다 하나씩 증가합니다.</summary>
    [Fact]
    public void DesignRevision_RejectsNegativeAndIncrementsWithOverflowCheck()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesignRevision(-1));
        Assert.Equal(new DesignRevision(8), new DesignRevision(7).Next());
        Assert.Throws<OverflowException>(() => new DesignRevision(long.MaxValue).Next());
    }
}

/// <summary>2차원·3차원 벡터의 유한값과 수치 안정성 계약을 검증합니다.</summary>
public sealed class VectorContractTests
{
    /// <summary>벡터 입력과 연산 결과가 유한 범위를 벗어나면 거부합니다.</summary>
    [Fact]
    public void Vectors_RejectNonFiniteInputsAndResults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vec2(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vec3(0, double.PositiveInfinity, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vec3(double.MaxValue, 0, 0) * 2);
        Assert.Throws<ArithmeticException>(() => Vec3.Dot(new(double.MaxValue, 0, 0), new(2, 0, 0)));
    }

    /// <summary>큰 성분을 가진 벡터도 오버플로 없이 길이를 계산하고 정규화합니다.</summary>
    [Fact]
    public void VectorLengthAndNormalization_AreStableForLargeFiniteValues()
    {
        var vector = new Vec3(1e200, 1e200, 0);
        var vector2D = new Vec2(1e200, 1e200);

        Assert.True(double.IsFinite(vector.Length));
        Assert.Equal(1.0, vector.Normalized().Length, 12);
        Assert.True(double.IsFinite(vector2D.Length));
        Assert.InRange(vector2D.Length / 1e200, Math.Sqrt(2) - 1e-12, Math.Sqrt(2) + 1e-12);
    }

    /// <summary>영벡터는 방향을 갖지 않으므로 정규화할 수 없습니다.</summary>
    [Fact]
    public void ZeroVector_CannotBeNormalized()
    {
        Assert.Throws<InvalidOperationException>(() => default(Vec3).Normalized());
    }
}

/// <summary>투영 좌표계가 모든 주축에서 오른손 정규직교 기준을 유지하는지 검증합니다.</summary>
public sealed class ProjectionFrameContractTests
{
    /// <summary>모든 주축 방향에서 평면 좌표 왕복과 오른손 방향을 보존합니다.</summary>
    [Fact]
    public void ProjectionFrame_RoundTripsPointsForEveryPrincipalNormal()
    {
        (Vec3 Normal, Vec3 PreferredX)[] axes =
        [
            (new(1, 0, 0), new(0, 1, 0)),
            (new(-1, 0, 0), new(0, 1, 0)),
            (new(0, 1, 0), new(1, 0, 0)),
            (new(0, -1, 0), new(1, 0, 0)),
            (new(0, 0, 1), new(1, 0, 0)),
            (new(0, 0, -1), new(1, 0, 0))
        ];

        foreach ((Vec3 normal, Vec3 preferredX) in axes)
        {
            var origin = new Vec3(10, -20, 30);
            var frame = new ProjectionFrame(origin, normal, preferredX);
            var pointOnPlane = origin + frame.AxisX * 12.5 + frame.AxisY * -4.25;
            var projected = frame.Project(pointOnPlane);

            Assert.Equal(12.5, projected.X, 9);
            Assert.Equal(-4.25, projected.Y, 9);
            Assert.Equal(pointOnPlane, frame.Unproject(projected));
            Assert.Equal(frame.Normal, Vec3.Cross(frame.AxisX, frame.AxisY));
        }
    }

    /// <summary>법선 또는 평면 X축 후보가 영벡터나 평행 방향이면 좌표계 생성을 거부합니다.</summary>
    [Fact]
    public void ProjectionFrame_RejectsDegenerateAxes()
    {
        Assert.Throws<ArgumentException>(() => new ProjectionFrame(default, default, new(1, 0, 0)));
        Assert.Throws<ArgumentException>(() => new ProjectionFrame(default, new(0, 0, 1), default));
        Assert.Throws<ArgumentException>(() => new ProjectionFrame(default, new(0, 0, 1), new(0, 0, -5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectionFrame(new(double.NaN, 0, 0), new(0, 0, 1), new(1, 0, 0)));
    }
}

/// <summary>저장 모델과 계산 결과의 기하 의존성 계약을 검증합니다.</summary>
public sealed class PersistedGeometryContractTests
{
    private const string ModelHash = "B9E93702868F775EEE0478C776E8A98071AABBB4080465E4C6B4B444A6157379";

    /// <summary>저장 좌표계는 세션 ID 없이 오른손 정규직교 값만 보관합니다.</summary>
    [Fact]
    public void WorkingCoordinateSystemDefinition_StoresOnlyRightHandedAxes()
    {
        var coordinateSystem = new WorkingCoordinateSystemDefinition(
            new(2, 3, 4),
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 1));

        Assert.Equal(new Vec3(0, 0, -1), coordinateSystem.RayDirection);
        Assert.DoesNotContain(typeof(WorkingCoordinateSystemDefinition).GetProperties(), property =>
            property.PropertyType == typeof(WorkingCoordinateSystemId) || property.PropertyType == typeof(ModelRevision));
        Assert.Throws<ArgumentException>(() => new WorkingCoordinateSystemDefinition(
            default, new(1, 0, 0), new(0, 0, 1), new(0, 1, 0)));
    }

    /// <summary>원본 해시와 가져오기 단위가 모델의 재현 가능한 정체성을 이룹니다.</summary>
    [Fact]
    public void ModelIdentity_NormalizesHashAndRequiresPositiveUnitScale()
    {
        var identity = new ModelIdentity(ModelHash, 1_000, "STEP", "7.9.3", "face-v1");

        Assert.Equal(ModelHash.ToLowerInvariant(), identity.SourceSha256);
        Assert.Equal(1_000, identity.SourceUnitToMillimeter);
        Assert.Throws<ArgumentException>(() => new ModelIdentity("not-a-hash", 1, "STEP", "1", "face-v1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelIdentity(ModelHash, 0, "STEP", "1", "face-v1"));
    }

    /// <summary>Face의 이웃 특징은 입력 목록 변경과 정렬 순서에 영향을 받지 않습니다.</summary>
    [Fact]
    public void FaceReference_CopiesAndSortsAdjacentFeatures()
    {
        var adjacentFeatures = new List<string> { "z-face", "a-face" };
        var face = new FaceReference(
            FaceRefId.New(), ModelHash, 12, FaceGeometryKind.Plane, new AreaMm2(500),
            new Vec3(10, 20, 30), "boundary-v1", adjacentFeatures);

        adjacentFeatures.Clear();

        Assert.Equal(new[] { "a-face", "z-face" }, face.AdjacentFaceFingerprints);
        Assert.Throws<ArgumentException>(() => new FaceReference(
            default, ModelHash, 12, FaceGeometryKind.Plane, new AreaMm2(500),
            new Vec3(10, 20, 30), "boundary-v1", Array.Empty<string>()));
    }

    /// <summary>작업별 기하 공차 초기값은 문서 설정을 따르고 무효 한도를 거부합니다.</summary>
    [Fact]
    public void GeometryToleranceProfile_UsesDocumentedStartingValues()
    {
        Assert.Equal(0.01, GeometryToleranceProfile.Initial.ConnectionToleranceMm);
        Assert.Equal(0.05, GeometryToleranceProfile.Initial.MeshDeflectionMm);
        Assert.Equal(0.1, GeometryToleranceProfile.Initial.MappingLeafLengthMm);
        Assert.Equal(18, GeometryToleranceProfile.Initial.MappingMaximumDepth);
        Assert.Equal(100_000, GeometryToleranceProfile.Initial.MappingMaximumProbes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeometryToleranceProfile(
            0, 0.05, 0.1, 0.1, 0.05, 18, 100_000, 2));
    }

    /// <summary>파생 결과의 stamp는 모든 해시 입력과 규칙 버전을 보존합니다.</summary>
    [Fact]
    public void DependencyStamp_RejectsMalformedHashes()
    {
        var stamp = new DependencyStamp(new DesignRevision(4), ModelHash, ModelHash, ModelHash, "geometry-v1");

        Assert.Equal(4, stamp.DesignRevision.Value);
        Assert.Equal(ModelHash.ToLowerInvariant(), stamp.SourceModelSha256);
        Assert.Throws<ArgumentException>(() => new DependencyStamp(
            new DesignRevision(4), "short", ModelHash, ModelHash, "geometry-v1"));
    }
}
