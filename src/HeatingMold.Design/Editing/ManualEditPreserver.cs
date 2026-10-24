using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Editing;

/// <summary>의미 대응이 확인된 절점 앵커와 활성 상태를 새 경로에 재적용합니다.</summary>
public sealed class ManualEditPreserver
{
    /// <summary>사용자 지정 공유 절점 좌표와 활성 상태를 옮기고 연결 무결성을 확인합니다.</summary>
    public OperationResult<CoreHeatingPath> Preserve(
        CoreHeatingPath previousPath,
        CoreHeatingPath replacementPath,
        RegenerationCompatibilityResult compatibility)
    {
        ArgumentNullException.ThrowIfNull(previousPath);
        ArgumentNullException.ThrowIfNull(replacementPath);
        ArgumentNullException.ThrowIfNull(compatibility);

        if (compatibility.Status == RegenerationCompatibilityStatus.NoOverrides)
            return OperationResult<CoreHeatingPath>.Success(replacementPath);
        if (compatibility.Status != RegenerationCompatibilityStatus.CanPreserveOverrides)
        {
            return Failure(
                "ManualEditsCannotBePreserved",
                "호환성 분석에서 보존할 수 없는 편집을 확인했습니다.",
                previousPath.Id.Value,
                replacementPath.Id.Value);
        }
        if (previousPath.GenerationSource.RegionId != replacementPath.GenerationSource.RegionId)
        {
            return Failure(
                "RegenerationRegionMismatch",
                "서로 다른 발열 영역의 경로 사이에는 편집을 옮길 수 없습니다.",
                previousPath.Id.Value,
                replacementPath.Id.Value);
        }
        if (compatibility.Mappings.Count == 0)
        {
            return Failure(
                "ManualEditMappingMissing",
                "보존 가능 판정에 필요한 조각 대응이 없습니다.",
                previousPath.Id.Value,
                replacementPath.Id.Value);
        }

        Dictionary<HeatingPathSegmentId, HeatingPathSegment> previousGenerated = previousPath.GeneratedSegments
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, HeatingPathSegment> previousCurrent = previousPath.Segments
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, HeatingPathSegment> replacementSegments = replacementPath.Segments
            .ToDictionary(segment => segment.Id);
        Dictionary<ControlPointId, Vec3> previousPositions = previousPath.ControlPoints
            .ToDictionary(point => point.Id, point => point.Position);
        Dictionary<ControlPointId, Vec3> generatedPositions = [];
        var replacementPointByPreviousId = new Dictionary<ControlPointId, ControlPointId>();
        var previousPointByReplacementId = new Dictionary<ControlPointId, ControlPointId>();
        var mappedPreviousSegmentIds = new HashSet<HeatingPathSegmentId>();

        foreach (RegenerationSegmentMapping mapping in compatibility.Mappings)
        {
            if (!previousGenerated.TryGetValue(mapping.PreviousSegmentId, out HeatingPathSegment? generated) ||
                !previousCurrent.TryGetValue(mapping.PreviousSegmentId, out HeatingPathSegment? edited) ||
                !replacementSegments.TryGetValue(mapping.ReplacementSegmentId, out HeatingPathSegment? replacement))
            {
                return Failure(
                    "ManualEditMappingStale",
                    "호환성 분석의 조각 ID가 현재 경로 정의와 일치하지 않습니다.",
                    previousPath.Id.Value,
                    mapping.PreviousSegmentId.Value,
                    mapping.ReplacementSegmentId.Value);
            }

            if (!RegenerationCompatibility.TryGetPathEndpoints(
                    generated.Curve, out Vec3 generatedStart, out Vec3 generatedEnd) ||
                !RegenerationCompatibility.TryGetPathEndpoints(
                    edited.Curve, out Vec3 editedStart, out Vec3 editedEnd))
            {
                return Failure(
                    "ManualEditCurveUnsupported",
                    "곡선의 경로 끝점을 읽을 수 없어 편집을 안전하게 옮길 수 없습니다.",
                    previousPath.Id.Value,
                    mapping.PreviousSegmentId.Value);
            }
            if (editedStart != previousPositions[edited.StartControlPointId] ||
                editedEnd != previousPositions[edited.EndControlPointId])
            {
                return Failure(
                    "ManualEditControlPointMismatch",
                    "편집 곡선의 끝점이 공유 제어점과 일치하지 않습니다.",
                    previousPath.Id.Value,
                    mapping.PreviousSegmentId.Value);
            }

            if (!AddConsistentPosition(generatedPositions, generated.StartControlPointId, generatedStart) ||
                !AddConsistentPosition(generatedPositions, generated.EndControlPointId, generatedEnd) ||
                !MapPoint(
                    replacementPointByPreviousId,
                    previousPointByReplacementId,
                    generated.StartControlPointId,
                    replacement.StartControlPointId) ||
                !MapPoint(
                    replacementPointByPreviousId,
                    previousPointByReplacementId,
                    generated.EndControlPointId,
                    replacement.EndControlPointId))
            {
                return Failure(
                    "ManualEditControlPointMappingAmbiguous",
                    "의미가 같은 조각들이 공유 절점을 서로 다르게 대응합니다.",
                    previousPath.Id.Value,
                    mapping.PreviousSegmentId.Value,
                    mapping.ReplacementSegmentId.Value);
            }

            mappedPreviousSegmentIds.Add(mapping.PreviousSegmentId);
        }

        Dictionary<ControlPointId, Vec3> replacementPositions = replacementPath.ControlPoints
            .ToDictionary(point => point.Id, point => point.Position);
        var preservedPositions = new Dictionary<ControlPointId, Vec3>();
        foreach ((ControlPointId previousPointId, ControlPointId replacementPointId) in replacementPointByPreviousId)
        {
            if (!generatedPositions.TryGetValue(previousPointId, out Vec3 generatedPosition) ||
                !previousPositions.TryGetValue(previousPointId, out Vec3 editedPosition) ||
                !replacementPositions.ContainsKey(replacementPointId))
            {
                return Failure(
                    "ManualEditControlPointMissing",
                    "재생성 조각의 공유 절점 위치를 찾을 수 없습니다.",
                    previousPath.Id.Value,
                    previousPointId.Value,
                    replacementPointId.Value);
            }

            if (editedPosition != generatedPosition && previousPath.Segments
                    .Where(segment => segment.StartControlPointId == previousPointId ||
                                      segment.EndControlPointId == previousPointId)
                    .Any(segment => !mappedPreviousSegmentIds.Contains(segment.Id)))
            {
                return Failure(
                    "ManualEditAdjacentSegmentUnmapped",
                    "이동한 공유 절점에 연결된 모든 구간을 새 경로에 대응하지 못했습니다.",
                    previousPath.Id.Value,
                    previousPointId.Value);
            }

            if (editedPosition != generatedPosition)
                preservedPositions.Add(replacementPointId, editedPosition);
        }

        ControlPoint[] controlPoints = replacementPath.ControlPoints
            .Select(point => preservedPositions.TryGetValue(point.Id, out Vec3 position)
                ? new ControlPoint(point.Id, position)
                : point)
            .ToArray();
        Dictionary<ControlPointId, Vec3> finalPositions = controlPoints
            .ToDictionary(point => point.Id, point => point.Position);
        Dictionary<HeatingPathSegmentId, RegenerationSegmentMapping> mappingsByReplacementId = compatibility.Mappings
            .ToDictionary(mapping => mapping.ReplacementSegmentId);

        try
        {
            HeatingPathSegment[] segments = replacementPath.Segments
                .Select(ApplyMappedEdit)
                .ToArray();
            Dictionary<ControlPointId, Vec3> finalPointPositions = controlPoints
                .ToDictionary(point => point.Id, point => point.Position);
            foreach (HeatingPathSegment segment in segments)
            {
                if (!RegenerationCompatibility.TryGetPathEndpoints(segment.Curve, out Vec3 start, out Vec3 end) ||
                    start != finalPointPositions[segment.StartControlPointId] ||
                    end != finalPointPositions[segment.EndControlPointId])
                {
                    return Failure(
                        "ManualEditProducedDisconnectedPath",
                        "편집 재적용 후 곡선 끝점과 공유 제어점이 일치하지 않습니다.",
                        previousPath.Id.Value,
                        segment.Id.Value);
                }
            }

            var preservedPath = new CoreHeatingPath(
                replacementPath.Id,
                replacementPath.GenerationSource,
                controlPoints,
                replacementPath.GeneratedSegments,
                segments);
            return OperationResult<CoreHeatingPath>.Success(preservedPath, compatibility.Issues);
        }
        catch (ArgumentException exception)
        {
            return Failure(
                "ManualEditProducedInvalidPath",
                $"편집 보존 결과가 유효한 경로를 만들지 못했습니다: {exception.Message}",
                previousPath.Id.Value,
                replacementPath.Id.Value);
        }

        HeatingPathSegment ApplyMappedEdit(HeatingPathSegment replacement)
        {
            if (!mappingsByReplacementId.TryGetValue(replacement.Id, out RegenerationSegmentMapping? mapping))
                return replacement;

            HeatingPathSegment previous = previousCurrent[mapping.PreviousSegmentId];
            CurveDefinition curve = replacement.Curve;
            const double endpointToleranceMm = 1e-7;
            if (!TryMovePathEndpoint(
                    curve,
                    isPathStart: true,
                    finalPositions[replacement.StartControlPointId],
                    endpointToleranceMm,
                    out curve) ||
                !TryMovePathEndpoint(
                    curve,
                    isPathStart: false,
                    finalPositions[replacement.EndControlPointId],
                    endpointToleranceMm,
                    out curve))
            {
                throw new ArgumentException("새 곡선의 공유 절점을 편집 위치로 옮길 수 없습니다.");
            }

            return new HeatingPathSegment(
                replacement.Id,
                replacement.Kind,
                replacement.StartControlPointId,
                replacement.EndControlPointId,
                replacement.Sequence,
                previous.IsActive,
                curve,
                replacement.LaneIndex);
        }
    }

    private static bool TryMovePathEndpoint(
        CurveDefinition curve,
        bool isPathStart,
        Vec3 target,
        double toleranceMm,
        out CurveDefinition updatedCurve)
    {
        if (!RegenerationCompatibility.TryGetPathEndpoints(curve, out Vec3 start, out Vec3 end))
        {
            updatedCurve = curve;
            return false;
        }
        Vec3 current = isPathStart ? start : end;
        if (current == target)
        {
            updatedCurve = curve;
            return true;
        }
        if (!CurveEndpointEditor.TryMoveEndpoint(
                curve,
                isPathStart,
                target,
                toleranceMm,
                out CurveDefinition? moved,
                out _) || moved is null)
        {
            updatedCurve = curve;
            return false;
        }

        updatedCurve = moved;
        return true;
    }

    private static bool AddConsistentPosition(
        IDictionary<ControlPointId, Vec3> positions,
        ControlPointId pointId,
        Vec3 position)
    {
        if (positions.TryGetValue(pointId, out Vec3 existing))
            return existing == position;

        positions.Add(pointId, position);
        return true;
    }

    private static bool MapPoint(
        IDictionary<ControlPointId, ControlPointId> forward,
        IDictionary<ControlPointId, ControlPointId> reverse,
        ControlPointId previousPointId,
        ControlPointId replacementPointId)
    {
        if (forward.TryGetValue(previousPointId, out ControlPointId existingReplacement) &&
            existingReplacement != replacementPointId)
        {
            return false;
        }
        if (reverse.TryGetValue(replacementPointId, out ControlPointId existingPrevious) &&
            existingPrevious != previousPointId)
        {
            return false;
        }

        forward[previousPointId] = replacementPointId;
        reverse[replacementPointId] = previousPointId;
        return true;
    }

    private static OperationResult<CoreHeatingPath> Failure(
        string code,
        string evidence,
        params Guid[] entityIds) => OperationResult<CoreHeatingPath>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            entityIds,
            "manual-edit-preservation-v1",
            evidence: [evidence])]);
}
