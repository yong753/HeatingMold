using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.HeatingPath;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Editing;

/// <summary>재생성된 topology에서 기존 사용자 편집을 대응할 수 있는지 판정합니다.</summary>
public sealed class RegenerationCompatibility
{
    /// <summary>현재 편집과 새 생성 초안의 안정 절점·레인 의미를 비교합니다.</summary>
    public OperationResult<RegenerationCompatibilityResult> Analyze(
        CoreHeatingPath currentPath,
        HeatingPathDraft replacementDraft,
        DesignRevision currentRevision)
    {
        ArgumentNullException.ThrowIfNull(currentPath);
        ArgumentNullException.ThrowIfNull(replacementDraft);

        if (replacementDraft.BaseRevision != currentRevision)
        {
            return Failure(
                "StaleRegenerationDraft",
                "재생성 초안의 기준 revision이 현재 설계와 다릅니다.",
                currentPath.Id.Value,
                replacementDraft.Id.Value,
                currentPath.GenerationSource.RegionId.Value);
        }
        if (currentPath.GenerationSource.RegionId != replacementDraft.RegionId)
        {
            return Failure(
                "RegenerationRegionMismatch",
                "기존 경로와 재생성 초안의 발열 영역이 다릅니다.",
                currentPath.Id.Value,
                replacementDraft.Id.Value,
                currentPath.GenerationSource.RegionId.Value,
                replacementDraft.RegionId.Value);
        }

        Dictionary<HeatingPathSegmentId, HeatingPathSegment> generatedById = currentPath.GeneratedSegments
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, HeatingPathSegment> currentById = currentPath.Segments
            .ToDictionary(segment => segment.Id);
        bool topologyChanged = generatedById.Count != currentById.Count ||
            generatedById.Keys.Any(id => !currentById.ContainsKey(id));
        if (topologyChanged)
        {
            return OperationResult<RegenerationCompatibilityResult>.Success(
                new RegenerationCompatibilityResult(
                    RegenerationCompatibilityStatus.ReplaceRequired,
                    [],
                    [CreateIssue(
                        "EditedTopologyChanged",
                        "생성 이후 경로 조각 추가·삭제가 있어 사용자 편집을 자동 대응하지 않습니다.",
                        [currentPath.Id.Value])]));
        }

        if (generatedById.Any(pair => !HasSameTopology(pair.Value, currentById[pair.Key])))
        {
            return OperationResult<RegenerationCompatibilityResult>.Success(
                new RegenerationCompatibilityResult(
                    RegenerationCompatibilityStatus.ReplaceRequired,
                    [],
                    [CreateIssue(
                        "EditedTopologyChanged",
                        "생성 이후 조각의 역할, 절점 연결 또는 순서가 바뀌어 사용자 편집을 자동 대응하지 않습니다.",
                        [currentPath.Id.Value])]));
        }

        HeatingPathSegment[] overrides = currentPath.Segments
            .Where(segment => !AreEquivalent(generatedById[segment.Id], segment))
            .ToArray();
        if (overrides.Length == 0)
        {
            return OperationResult<RegenerationCompatibilityResult>.Success(
                new RegenerationCompatibilityResult(RegenerationCompatibilityStatus.NoOverrides, [], []));
        }

        HeatingPathDraftSegment[] newSegments = replacementDraft.Segments.ToArray();
        var mappings = new List<RegenerationSegmentMapping>();
        var problems = new List<ValidationIssue>();
        var mappedReplacementIds = new HashSet<HeatingPathSegmentId>();
        foreach (HeatingPathSegment editedSegment in overrides)
        {
            HeatingPathSegment generatedSegment = generatedById[editedSegment.Id];
            if (!CanRebaseEndpointEdit(currentPath, generatedSegment, editedSegment, out string rebaseFailure))
            {
                problems.Add(CreateIssue(
                    "EditedSegmentCannotBeRebased",
                    rebaseFailure,
                    [currentPath.Id.Value, editedSegment.Id.Value]));
                continue;
            }

            string? signature = GetSemanticSignature(currentPath.Segments, editedSegment);
            HeatingPathDraftSegment[] matches = signature is null
                ? []
                : newSegments.Where(segment => GetSemanticSignature(newSegments, segment) == signature).ToArray();

            if (matches.Length != 1)
            {
                problems.Add(CreateIssue(
                    "EditedSegmentHasNoUniqueRegenerationMatch",
                    "편집 조각의 레인 또는 인접 U턴 대응이 없거나 둘 이상이라 보존할 수 없습니다.",
                    [currentPath.Id.Value, editedSegment.Id.Value]));
                continue;
            }

            if (!mappedReplacementIds.Add(matches[0].Id))
            {
                problems.Add(CreateIssue(
                    "EditedSegmentHasNoUniqueRegenerationMatch",
                    "여러 편집 조각이 같은 새 조각에 대응되어 보존할 수 없습니다.",
                    [currentPath.Id.Value, editedSegment.Id.Value, matches[0].Id.Value]));
                continue;
            }

            mappings.Add(new RegenerationSegmentMapping(
                editedSegment.Id,
                matches[0].Id,
                signature!));
        }

        RegenerationCompatibilityStatus status = problems.Count == 0
            ? RegenerationCompatibilityStatus.CanPreserveOverrides
            : RegenerationCompatibilityStatus.ReplaceRequired;
        return OperationResult<RegenerationCompatibilityResult>.Success(
            new RegenerationCompatibilityResult(status, mappings, problems),
            problems.Count == 0
                ? [CreateIssue(
                    "EditedSegmentsMappedByLaneMeaning",
                    "편집 대상이 안정 절점 ID가 아닌 같은 레인 역할과 U턴 연결 의미로 대응됐습니다.",
                    [currentPath.Id.Value, replacementDraft.Id.Value],
                    ValidationSeverity.Info)]
                : []);
    }

    private static string? GetSemanticSignature(
        IReadOnlyList<HeatingPathSegment> segments,
        HeatingPathSegment target)
    {
        if (target.Kind == HeatingPathSegmentKind.HeatingRun)
            return target.LaneIndex.HasValue ? $"run:{target.LaneIndex.Value}" : null;
        if (target.Kind == HeatingPathSegmentKind.Turn)
            return target.LaneIndex.HasValue ? $"turn:{target.LaneIndex.Value}" : null;
        if (target.Kind == HeatingPathSegmentKind.FixedJoin)
        {
            int index = IndexOf(segments, target.Id);
            if (index == 0)
                return "lead:start";
            if (index == segments.Count - 1)
                return "lead:end";
        }

        return null;
    }

    private static string? GetSemanticSignature(
        IReadOnlyList<HeatingPathDraftSegment> segments,
        HeatingPathDraftSegment target)
    {
        if (target.Kind == HeatingPathSegmentKind.HeatingRun)
            return target.LaneIndex.HasValue ? $"run:{target.LaneIndex.Value}" : null;
        if (target.Kind == HeatingPathSegmentKind.Turn)
            return target.LaneIndex.HasValue ? $"turn:{target.LaneIndex.Value}" : null;
        if (target.Kind == HeatingPathSegmentKind.FixedJoin)
        {
            int index = IndexOf(segments, target.Id);
            if (index == 0)
                return "lead:start";
            if (index == segments.Count - 1)
                return "lead:end";
        }

        return null;
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, HeatingPathSegmentId id) where T : class => items
        .Select((item, index) => (item, index))
        .Where(pair => pair.item switch
        {
            HeatingPathSegment segment => segment.Id == id,
            HeatingPathDraftSegment draftSegment => draftSegment.Id == id,
            _ => false
        })
        .Select(pair => pair.index)
        .DefaultIfEmpty(-1)
        .Single();

    private static bool AreEquivalent(HeatingPathSegment first, HeatingPathSegment second) =>
        first.Id == second.Id &&
        first.Kind == second.Kind &&
        first.StartControlPointId == second.StartControlPointId &&
        first.EndControlPointId == second.EndControlPointId &&
        first.Sequence == second.Sequence &&
        first.LaneIndex == second.LaneIndex &&
        first.IsActive == second.IsActive &&
        CurvesAreEquivalent(first.Curve, second.Curve);

    private static bool HasSameTopology(HeatingPathSegment generated, HeatingPathSegment current) =>
        generated.Id == current.Id &&
        generated.Kind == current.Kind &&
        generated.StartControlPointId == current.StartControlPointId &&
        generated.EndControlPointId == current.EndControlPointId &&
        generated.Sequence == current.Sequence &&
        generated.LaneIndex == current.LaneIndex;

    private static bool CanRebaseEndpointEdit(
        CoreHeatingPath path,
        HeatingPathSegment generated,
        HeatingPathSegment edited,
        out string failure)
    {
        if (!TryGetPathEndpoints(generated.Curve, out Vec3 generatedStart, out Vec3 generatedEnd) ||
            !TryGetPathEndpoints(edited.Curve, out Vec3 editedStart, out Vec3 editedEnd))
        {
            failure = "이 곡선 형식은 경로 끝점을 안전하게 재배치할 수 없습니다.";
            return false;
        }

        Dictionary<ControlPointId, Vec3> currentPoints = path.ControlPoints
            .ToDictionary(point => point.Id, point => point.Position);
        if (editedStart != currentPoints[edited.StartControlPointId] ||
            editedEnd != currentPoints[edited.EndControlPointId])
        {
            failure = "편집 곡선 끝점이 공유 제어점과 일치하지 않아 편집을 재적용할 수 없습니다.";
            return false;
        }

        HeatingPathSegment firstGenerated = path.GeneratedSegments[0];
        HeatingPathSegment lastGenerated = path.GeneratedSegments[^1];
        if ((generated.StartControlPointId == firstGenerated.StartControlPointId && editedStart != generatedStart) ||
            (generated.EndControlPointId == lastGenerated.EndControlPointId && editedEnd != generatedEnd))
        {
            failure = "고정 시작·끝 앵커의 이동은 새 배치 앵커와 충돌할 수 있어 자동 보존하지 않습니다.";
            return false;
        }

        CurveDefinition restored = edited.Curve;
        const double endpointToleranceMm = 1e-7;
        if (editedStart != generatedStart)
        {
            if (!CurveEndpointEditor.TryMoveEndpoint(
                    restored,
                    true,
                    generatedStart,
                    endpointToleranceMm,
                    out CurveDefinition? restoredStartCurve,
                    out _) || restoredStartCurve is null)
            {
                failure = "편집 곡선의 시작점을 생성 원본으로 되돌릴 수 없습니다.";
                return false;
            }

            restored = restoredStartCurve;
        }
        if (editedEnd != generatedEnd)
        {
            if (!CurveEndpointEditor.TryMoveEndpoint(
                    restored,
                    false,
                    generatedEnd,
                    endpointToleranceMm,
                    out CurveDefinition? restoredEndCurve,
                    out _) || restoredEndCurve is null)
            {
                failure = "편집 곡선의 끝점을 생성 원본으로 되돌릴 수 없습니다.";
                return false;
            }

            restored = restoredEndCurve;
        }
        if (!CurvesAreEquivalent(generated.Curve, restored))
        {
            failure = "편집 곡선에 끝점 이동 이외의 형상 변경이 있어 자동 보존하지 않습니다.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    internal static bool TryGetPathEndpoints(CurveDefinition curve, out Vec3 start, out Vec3 end)
    {
        Vec3 definitionStart;
        Vec3 definitionEnd;
        switch (curve)
        {
            case LineCurveDefinition line:
                definitionStart = line.Start;
                definitionEnd = line.End;
                break;
            case PolylineCurveDefinition polyline:
                definitionStart = polyline.Points[0];
                definitionEnd = polyline.Points[^1];
                break;
            case ArcCurveDefinition arc:
                definitionStart = EvaluateArcPoint(arc, arc.StartAngleRad);
                definitionEnd = EvaluateArcPoint(arc, arc.EndAngleRad);
                break;
            default:
                start = default;
                end = default;
                return false;
        }

        if (curve.Direction == CurveDirection.Forward)
        {
            start = definitionStart;
            end = definitionEnd;
        }
        else
        {
            start = definitionEnd;
            end = definitionStart;
        }

        return true;
    }

    private static Vec3 EvaluateArcPoint(ArcCurveDefinition arc, double angle) =>
        arc.Center +
        arc.Plane.AxisX * (arc.RadiusMm.Value * Math.Cos(angle)) +
        arc.Plane.AxisY * (arc.RadiusMm.Value * Math.Sin(angle));

    internal static bool CurvesAreEquivalent(CurveDefinition first, CurveDefinition second)
    {
        if (first.Kind != second.Kind || first.Direction != second.Direction)
            return false;

        return (first, second) switch
        {
            (LineCurveDefinition a, LineCurveDefinition b) => a.Start == b.Start && a.End == b.End,
            (PolylineCurveDefinition a, PolylineCurveDefinition b) => a.Points.SequenceEqual(b.Points),
            (ArcCurveDefinition a, ArcCurveDefinition b) =>
                a.Center == b.Center && a.RadiusMm == b.RadiusMm &&
                a.StartAngleRad == b.StartAngleRad && a.EndAngleRad == b.EndAngleRad &&
                a.Plane.Origin == b.Plane.Origin && a.Plane.AxisX == b.Plane.AxisX &&
                a.Plane.AxisY == b.Plane.AxisY && a.Plane.Normal == b.Plane.Normal,
            (BSplineCurveDefinition a, BSplineCurveDefinition b) =>
                a.Degree == b.Degree && a.IsPeriodic == b.IsPeriodic &&
                a.TrimStart == b.TrimStart && a.TrimEnd == b.TrimEnd &&
                a.Poles.SequenceEqual(b.Poles) && a.Weights.SequenceEqual(b.Weights) &&
                a.Knots.SequenceEqual(b.Knots) && a.Multiplicities.SequenceEqual(b.Multiplicities),
            _ => false
        };
    }

    private static ValidationIssue CreateIssue(
        string code,
        string evidence,
        IEnumerable<Guid> entityIds,
        ValidationSeverity severity = ValidationSeverity.Warning) => new(
        code,
        severity,
        $"Design.{code}",
        entityIds,
        "regeneration-compatibility-v1",
        evidence: [evidence]);

    private static OperationResult<RegenerationCompatibilityResult> Failure(
        string code,
        string evidence,
        params Guid[] entityIds) => OperationResult<RegenerationCompatibilityResult>.Failure(
        [CreateIssue(code, evidence, entityIds, ValidationSeverity.Error)]);
}

/// <summary>재생성된 경로에서 사용자 편집을 적용할 수 있는 수준입니다.</summary>
public enum RegenerationCompatibilityStatus
{
    /// <summary>현재 경로에 생성 원본과 다른 사용자 편집이 없습니다.</summary>
    NoOverrides,

    /// <summary>변경한 모든 조각에 고유 레인 의미 대응이 있습니다.</summary>
    CanPreserveOverrides,

    /// <summary>topology 또는 편집 구간이 달라 명시적 교체가 필요합니다.</summary>
    ReplaceRequired
}

/// <summary>기존 조각과 재생성 조각의 의미 대응을 보관합니다.</summary>
public sealed record RegenerationSegmentMapping(
    HeatingPathSegmentId PreviousSegmentId,
    HeatingPathSegmentId ReplacementSegmentId,
    string SemanticKey);

/// <summary>재생성 정책 선택에 사용할 상태, ID 대응과 진단입니다.</summary>
public sealed class RegenerationCompatibilityResult
{
    /// <summary>편집 보존 또는 경로 교체가 필요한지 나타냅니다.</summary>
    public RegenerationCompatibilityStatus Status { get; }

    /// <summary>의미가 같은 것으로 확인한 편집 조각 대응입니다.</summary>
    public IReadOnlyList<RegenerationSegmentMapping> Mappings { get; }

    /// <summary>보존 판단 근거 또는 불가능한 조각 정보입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>재생성 상태와 결과 목록을 방어 복사합니다.</summary>
    public RegenerationCompatibilityResult(
        RegenerationCompatibilityStatus status,
        IEnumerable<RegenerationSegmentMapping> mappings,
        IEnumerable<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        RegenerationSegmentMapping[] copiedMappings = mappings?.ToArray()
            ?? throw new ArgumentNullException(nameof(mappings));
        ValidationIssue[] copiedIssues = issues?.ToArray()
            ?? throw new ArgumentNullException(nameof(issues));
        if (copiedMappings.Any(mapping => mapping.PreviousSegmentId.IsEmpty ||
                                          mapping.ReplacementSegmentId.IsEmpty ||
                                          string.IsNullOrWhiteSpace(mapping.SemanticKey)))
        {
            throw new ArgumentException("재생성 대응에는 두 조각 ID와 의미 키가 필요합니다.", nameof(mappings));
        }

        Status = status;
        Mappings = new ReadOnlyCollection<RegenerationSegmentMapping>(copiedMappings);
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }
}
