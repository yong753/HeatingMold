using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>검증된 후보 곡선 측정값을 설계 ID와 의존 지문이 있는 snapshot으로 조립합니다.</summary>
public sealed class CurveGeometrySnapshotBuilder
{
    private const string SnapshotRuleVersion = "occt-curve-snapshot-v1";

    private readonly CurveDefinitionMapper _curveMapper;

    /// <summary>정확 곡선 정의를 지문 입력에 포함할 매퍼를 보관합니다.</summary>
    public CurveGeometrySnapshotBuilder(CurveDefinitionMapper? curveMapper = null)
    {
        _curveMapper = curveMapper ?? new CurveDefinitionMapper();
    }

    /// <summary>발열·연결 구간 전체의 정확 정의와 Native 측정을 immutable snapshot으로 묶습니다.</summary>
    public GeometrySnapshot Build(
        DesignProject candidate,
        GeometryDocumentId documentId,
        ModelRevision modelRevision,
        IReadOnlyDictionary<HeatingPathSegmentId, OcctCurveMeasurement> heatingMeasurements,
        IReadOnlyDictionary<ConnectionPathSegmentId, OcctCurveMeasurement> connectionMeasurements)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(heatingMeasurements);
        ArgumentNullException.ThrowIfNull(connectionMeasurements);

        HeatingPathSegment[] allHeatingSegments = candidate.HeatingPaths
            .SelectMany(path => path.Segments)
            .ToArray();
        ValidateUniqueIds(allHeatingSegments.Select(segment => segment.Id), "발열 경로");

        HeatingPathSegment[] heatingSegments = allHeatingSegments
            .Where(segment => segment.IsActive)
            .ToArray();
        ConnectionPathSegment[] connectionSegments = candidate.ConnectionPaths
            .SelectMany(path => path.Segments)
            .ToArray();

        ValidateUniqueIds(connectionSegments.Select(segment => segment.Id), "연결 경로");
        ValidateMeasurementIds(
            heatingSegments.Select(segment => segment.Id),
            heatingMeasurements.Keys,
            "발열 경로");
        ValidateMeasurementIds(
            connectionSegments.Select(segment => segment.Id),
            connectionMeasurements.Keys,
            "연결 경로");

        var curves = new List<CurveGeometrySnapshot>(heatingSegments.Length + connectionSegments.Length);
        foreach (HeatingPathSegment segment in heatingSegments.OrderBy(segment => segment.Id.Value))
        {
            OcctCurveMeasurement measurement = RequireMeasurement(heatingMeasurements, segment.Id);
            curves.Add(new CurveGeometrySnapshot(
                heatingPathSegmentId: segment.Id,
                connectionPathSegmentId: null,
                definition: segment.Curve,
                lengthMm: measurement.LengthMm,
                start: measurement.Start,
                end: measurement.End,
                samples: measurement.Samples,
                approximation: measurement.Approximation));
        }

        foreach (ConnectionPathSegment segment in connectionSegments.OrderBy(segment => segment.Id.Value))
        {
            OcctCurveMeasurement measurement = RequireMeasurement(connectionMeasurements, segment.Id);
            curves.Add(new CurveGeometrySnapshot(
                heatingPathSegmentId: null,
                connectionPathSegmentId: segment.Id,
                definition: segment.Curve,
                lengthMm: measurement.LengthMm,
                start: measurement.Start,
                end: measurement.End,
                samples: measurement.Samples,
                approximation: measurement.Approximation));
        }

        DependencyStamp dependencyStamp = CreateDependencyStamp(candidate);
        return new GeometrySnapshot(documentId, modelRevision, dependencyStamp, curves);
    }

    /// <summary>곡선 정의를 확인하고 현재 Native 측정값을 후보 설계 지문에 연결합니다.</summary>
    public GeometrySnapshot Rebind(DesignProject candidate, GeometrySnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentSnapshot);

        Dictionary<HeatingPathSegmentId, HeatingPathSegment> expectedHeatingSegments = candidate.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive)
            .ToDictionary(segment => segment.Id);
        Dictionary<ConnectionPathSegmentId, ConnectionPathSegment> expectedConnectionSegments = candidate.ConnectionPaths
            .SelectMany(path => path.Segments)
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> currentHeatingCurves = currentSnapshot.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        Dictionary<ConnectionPathSegmentId, CurveGeometrySnapshot> currentConnectionCurves = currentSnapshot.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value);

        if (!expectedHeatingSegments.Keys.ToHashSet().SetEquals(currentHeatingCurves.Keys) ||
            !expectedConnectionSegments.Keys.ToHashSet().SetEquals(currentConnectionCurves.Keys))
        {
            throw new InvalidOperationException("전기 전용 변경 후보의 곡선 ID 집합이 현재 측정 snapshot과 다릅니다.");
        }

        var reboundCurves = new List<CurveGeometrySnapshot>(currentSnapshot.Curves.Count);
        foreach ((HeatingPathSegmentId segmentId, HeatingPathSegment segment) in expectedHeatingSegments
                     .OrderBy(pair => pair.Key.Value))
        {
            CurveGeometrySnapshot measured = currentHeatingCurves[segmentId];
            EnsureSameCurveDefinition(segment.Curve, measured.Definition, segmentId.Value);
            reboundCurves.Add(CopyMeasurement(measured, segment.Curve));
        }

        foreach ((ConnectionPathSegmentId segmentId, ConnectionPathSegment segment) in expectedConnectionSegments
                     .OrderBy(pair => pair.Key.Value))
        {
            CurveGeometrySnapshot measured = currentConnectionCurves[segmentId];
            EnsureSameCurveDefinition(segment.Curve, measured.Definition, segmentId.Value);
            reboundCurves.Add(CopyMeasurement(measured, segment.Curve));
        }

        return new GeometrySnapshot(
            currentSnapshot.DocumentId,
            currentSnapshot.ModelRevision,
            CreateDependencyStamp(candidate),
            reboundCurves);
    }

    private static CurveGeometrySnapshot CopyMeasurement(CurveGeometrySnapshot source, CurveDefinition definition) =>
        new(
            source.HeatingPathSegmentId,
            source.ConnectionPathSegmentId,
            definition,
            source.LengthMm,
            source.Start,
            source.End,
            source.Samples,
            source.Approximation);

    private static void EnsureSameCurveDefinition(
        CurveDefinition expected,
        CurveDefinition measured,
        Guid segmentId)
    {
        if (!CurveDefinitionComparer.AreEquivalent(expected, measured))
        {
            throw new InvalidOperationException(
                $"전기 전용 변경 후보에서 경로 구간 {segmentId:N}의 곡선 정의가 바뀌었습니다.");
        }
    }

    /// <summary>프로젝트 revision, 작업 좌표계와 정확 곡선 입력에서 지문을 만듭니다.</summary>
    private DependencyStamp CreateDependencyStamp(DesignProject candidate)
    {
        string workingFrameFingerprint = Hash(BuildWorkingFramePayload(candidate.WorkingCoordinateSystem));
        string inputFingerprint = Hash(BuildInputPayload(candidate));
        string[] ruleVersions =
        [
            SnapshotRuleVersion,
            candidate.Rules.GenerationRuleVersion,
            candidate.Rules.ValidationRuleVersion,
            candidate.Rules.AreaRuleVersion,
            candidate.Rules.ElectricalRuleVersion
        ];
        string ruleVersion = string.Join(
            "/",
            ruleVersions.Select(version => $"{Encoding.UTF8.GetByteCount(version)}:{version}"));

        return new DependencyStamp(
            candidate.Revision,
            candidate.SourceModel.SourceSha256,
            workingFrameFingerprint,
            inputFingerprint,
            ruleVersion);
    }

    /// <summary>작업 좌표계의 네 축을 문화권에 무관한 문자열로 직렬화합니다.</summary>
    private static string BuildWorkingFramePayload(WorkingCoordinateSystemDefinition definition)
    {
        var payload = new StringBuilder("working-coordinate-v1");
        AppendVector(payload, definition.Origin);
        AppendVector(payload, definition.AxisX);
        AppendVector(payload, definition.AxisY);
        AppendVector(payload, definition.Normal);
        return payload.ToString();
    }

    /// <summary>프로젝트 입력과 모든 활성 최종 곡선 정의를 결정적으로 직렬화합니다.</summary>
    private string BuildInputPayload(DesignProject candidate)
    {
        byte[] projectJson = JsonSerializer.SerializeToUtf8Bytes(candidate);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashPart(hash, "design-project-input-v1");
        AppendHashPart(hash, projectJson);

        foreach (HeatingPathSegment segment in candidate.HeatingPaths
                     .SelectMany(path => path.Segments)
                     .Where(segment => segment.IsActive)
                     .OrderBy(segment => segment.Id.Value))
        {
            AppendHashPart(hash, "heating");
            AppendHashPart(hash, segment.Id.Value.ToString("N"));
            AppendHashPart(hash, JsonSerializer.Serialize(_curveMapper.ToBridgeDto(segment.Curve)));
        }

        foreach (ConnectionPathSegment segment in candidate.ConnectionPaths
                     .SelectMany(path => path.Segments)
                     .OrderBy(segment => segment.Id.Value))
        {
            AppendHashPart(hash, "connection");
            AppendHashPart(hash, segment.Id.Value.ToString("N"));
            AppendHashPart(hash, JsonSerializer.Serialize(_curveMapper.ToBridgeDto(segment.Curve)));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>벡터의 세 좌표를 round-trip 수치 형식으로 추가합니다.</summary>
    private static void AppendVector(StringBuilder payload, Vec3 value)
    {
        payload.Append('|').Append(value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        payload.Append('|').Append(value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        payload.Append('|').Append(value.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>길이 구분자를 붙여 입력 문자열의 경계가 모호해지지 않게 합니다.</summary>
    private static void AppendHashPart(IncrementalHash hash, string value) =>
        AppendHashPart(hash, Encoding.UTF8.GetBytes(value));

    /// <summary>길이 접두사를 붙여 입력 바이트 경계를 해시 스트림에 추가합니다.</summary>
    private static void AppendHashPart(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    /// <summary>UTF-8 입력의 소문자 SHA-256을 반환합니다.</summary>
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>예상 ID에 맞는 Native 측정값이 존재하는지 확인합니다.</summary>
    private static OcctCurveMeasurement RequireMeasurement<TKey>(
        IReadOnlyDictionary<TKey, OcctCurveMeasurement> measurements,
        TKey id) where TKey : notnull
    {
        if (!measurements.TryGetValue(id, out OcctCurveMeasurement? measurement) || measurement is null)
            throw new InvalidOperationException("후보 곡선 ID에 해당하는 Native 측정값이 없습니다.");

        return measurement;
    }

    /// <summary>예상 구간 ID와 Native 측정 ID가 정확히 일치하는지 확인합니다.</summary>
    private static void ValidateMeasurementIds<TKey>(
        IEnumerable<TKey> expectedIds,
        IEnumerable<TKey> actualIds,
        string geometryName) where TKey : notnull
    {
        HashSet<TKey> expected = expectedIds.ToHashSet();
        HashSet<TKey> actual = actualIds.ToHashSet();
        if (!expected.SetEquals(actual))
        {
            throw new InvalidOperationException(
                $"{geometryName} Native 측정 ID 집합이 후보의 활성 구간과 일치하지 않습니다.");
        }
    }

    /// <summary>후보 목록 안에 안정 ID가 중복되지 않았는지 확인합니다.</summary>
    private static void ValidateUniqueIds<TKey>(IEnumerable<TKey> ids, string geometryName) where TKey : notnull
    {
        TKey[] copiedIds = ids.ToArray();
        if (copiedIds.Distinct().Count() != copiedIds.Length)
            throw new InvalidOperationException($"{geometryName} 구간 ID가 중복되었습니다.");
    }
}
