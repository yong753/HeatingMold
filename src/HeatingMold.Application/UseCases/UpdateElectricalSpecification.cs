using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.Quality;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Application.UseCases;

/// <summary>전기 사양 초안을 보관하고 기존 geometry를 재사용해 결과만 갱신합니다.</summary>
public sealed class UpdateElectricalSpecification
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly IElectricalGeometryWorkspace _geometryWorkspace;
    private readonly ConnectionLengthLedgerBuilder _ledgerBuilder;
    private readonly RecalculateElectrical _recalculateElectrical;

    /// <summary>세션 후보 확정, geometry 재사용, 길이 원장과 전기 계산을 연결합니다.</summary>
    public UpdateElectricalSpecification(
        ProjectSession session,
        CandidateCoordinator coordinator,
        IElectricalGeometryWorkspace geometryWorkspace,
        ConnectionLengthLedgerBuilder? ledgerBuilder = null,
        RecalculateElectrical? recalculateElectrical = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _geometryWorkspace = geometryWorkspace ?? throw new ArgumentNullException(nameof(geometryWorkspace));
        _ledgerBuilder = ledgerBuilder ?? new ConnectionLengthLedgerBuilder();
        _recalculateElectrical = recalculateElectrical ?? new RecalculateElectrical();
    }

    /// <summary>도체 저항과 회로별 전원 입력을 확정 설계와 분리해 초안으로 저장합니다.</summary>
    public OperationResult<InputDraft> CreateDraft(
        WireSpecification wireSpecification,
        IReadOnlyDictionary<CircuitId, Supply> circuitSupplies)
    {
        ArgumentNullException.ThrowIfNull(wireSpecification);
        ArgumentNullException.ThrowIfNull(circuitSupplies);

        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure<InputDraft>(null, "ProjectNotLoaded", "전기 사양을 바꾸려면 먼저 설계를 확정해야 합니다.");
        if (project.WireSpecification is not { } currentWire)
            return Failure<InputDraft>(project, "MissingWireSpecification", "현재 설계에 발열 도체 사양이 없습니다.");
        if (wireSpecification.DiameterMm != currentWire.DiameterMm)
        {
            return Failure<InputDraft>(project, "ElectricalUpdateCannotChangeWireDiameter",
                "전기 전용 변경에서는 선경을 바꿀 수 없습니다. 선경 변경은 경로 재생성으로 적용하세요.");
        }
        if (current.CurrentInputDraft is { Kind: not InputDraftKind.ElectricalSpecification })
        {
            return Failure<InputDraft>(project, "AnotherInputDraftIsPending",
                "다른 입력 초안을 먼저 적용하거나 취소해야 합니다.");
        }
        if (!HasExactCircuitIds(project.Circuits, circuitSupplies.Keys) || circuitSupplies.Any(pair => pair.Value is null))
        {
            return Failure<InputDraft>(project, "CircuitSupplySetMismatch",
                "전원 사양은 현재 회로 ID마다 하나씩 지정해야 합니다.");
        }

        Circuit[] circuits = project.Circuits
            .Select(circuit => new Circuit(
                circuit.Id,
                circuit.Sequence,
                circuit.Segments,
                circuit.StartConnectionPointId,
                circuit.EndConnectionPointId,
                circuitSupplies[circuit.Id]))
            .ToArray();
        DesignProject candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            circuits: circuits,
            wireSpecification: wireSpecification,
            replaceWireSpecification: true);

        ValidationReport referenceReport = candidate.ValidateReferences();
        if (!referenceReport.IsValid)
            return OperationResult<InputDraft>.Failure(referenceReport.Issues);

        var draft = new InputDraft(
            InputDraftKind.ElectricalSpecification,
            project.Revision,
            candidate);
        if (!_session.TrySetDraft(draft))
        {
            return Failure<InputDraft>(project, "ElectricalDraftCouldNotBeStored",
                "설계가 바뀌었거나 다른 작업이 진행 중이어서 초안을 저장하지 못했습니다.");
        }

        return OperationResult<InputDraft>.Success(draft);
    }

    /// <summary>현재 전기 초안을 검증하고 Native 곡선 재생성 없이 후보 결과를 확정합니다.</summary>
    public CandidateOutcome Apply(CancellationToken cancellationToken = default)
    {
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project || current.CurrentInputDraft is not { } draft)
            return Outcome(CandidateStatus.ValidationFailed, current, "ElectricalDraftNotFound",
                "적용할 전기 사양 초안이 없습니다.");
        if (draft.Kind != InputDraftKind.ElectricalSpecification || !draft.CanApply)
            return Outcome(CandidateStatus.ValidationFailed, current, "ElectricalDraftInvalid",
                "현재 입력 초안은 전기 사양을 적용할 수 있는 상태가 아닙니다.");
        if (draft.BaseRevision != project.Revision || !IsElectricalOnlyChange(project, draft.CandidateProject))
            return Outcome(CandidateStatus.Stale, current, "ElectricalDraftDoesNotMatchCurrentProject",
                "전기 초안의 기준 revision 또는 변경 범위가 현재 설계와 다릅니다.");
        if (current.Geometry is not { } geometry || current.RuntimeDocument is not { } runtimeDocument)
            return Outcome(CandidateStatus.ValidationFailed, current, "CurrentGeometryNotAvailable",
                "확정 Native 형상이 없어 전기 결과를 재계산할 수 없습니다.");

        return _coordinator.Execute(
            draft.CandidateProject,
            draft.BaseRevision,
            token => _geometryWorkspace.PrepareElectricalUpdate(
                draft.CandidateProject,
                geometry,
                runtimeDocument,
                token),
            prepared => BuildDerivedState(draft.CandidateProject, current, prepared),
            cancellationToken);
    }

    /// <summary>현재 입력 초안을 버리고 확정 설계와 계산 결과를 유지합니다.</summary>
    public bool CancelDraft()
    {
        if (_session.Current.CurrentInputDraft?.Kind != InputDraftKind.ElectricalSpecification)
            return false;

        return _session.TryCancelDraft();
    }

    private OperationResult<CandidateDerivedState> BuildDerivedState(
        DesignProject candidate,
        SessionSnapshot current,
        IPreparedGeometry preparedGeometry)
    {
        GeometrySnapshot oldGeometry = current.Geometry!;
        DependencyStamp newStamp = preparedGeometry.Snapshot.DependencyStamp;
        AppliedAreaAllocation? allocation = null;
        if (current.AreaAllocation is { } oldAllocation)
        {
            if (oldAllocation.DependencyStamp != oldGeometry.DependencyStamp)
            {
                return Failure<CandidateDerivedState>(candidate, "StaleAreaAllocation",
                    "현재 면적 분할 지문이 확정 geometry와 다릅니다.");
            }
            if (!oldAllocation.CircuitAppliedAreas.Keys.ToHashSet().SetEquals(candidate.Circuits.Select(circuit => circuit.Id)))
            {
                return Failure<CandidateDerivedState>(candidate, "AreaAllocationCircuitSetMismatch",
                    "현재 면적 배정의 회로 ID 집합이 전기 변경 후보와 다릅니다.");
            }

            allocation = new AppliedAreaAllocation(
                newStamp,
                oldAllocation.TotalEffectiveSurfaceAreaMm2,
                oldAllocation.CircuitAppliedAreas,
                oldAllocation.UnassignedAppliedAreaMm2,
                oldAllocation.Quality);
        }

        List<CenterPitchQualityReport> qualityReports = new(current.CenterPitchQualityReports.Count);
        foreach (CenterPitchQualityReport report in current.CenterPitchQualityReports)
        {
            if (report.DependencyStamp != oldGeometry.DependencyStamp)
            {
                return Failure<CandidateDerivedState>(candidate, "StaleCenterPitchQualityReport",
                    "현재 품질 보고서 지문이 확정 geometry와 다릅니다.");
            }

            qualityReports.Add(Rebind(report, newStamp));
        }

        if (candidate.Circuits.Count > 0 && candidate.WireSpecification is null)
        {
            return Failure<CandidateDerivedState>(candidate, "MissingWireSpecification",
                "회로 전기 결과를 계산할 발열 도체 사양이 없습니다.");
        }

        var inputs = new List<CircuitElectricalInput>(candidate.Circuits.Count);
        foreach (Circuit circuit in candidate.Circuits.OrderBy(circuit => circuit.Sequence))
        {
            OperationResult<LengthBreakdown> ledgerResult = _ledgerBuilder.Build(
                candidate,
                circuit,
                preparedGeometry.Snapshot);
            if (!ledgerResult.IsSuccess)
                return OperationResult<CandidateDerivedState>.Failure(ledgerResult.Issues);

            inputs.Add(new CircuitElectricalInput(
                circuit.Id,
                candidate.WireSpecification!,
                circuit.Supply,
                ledgerResult.GetValueOrThrow(),
                newStamp));
        }

        ElectricalRecalculationResult recalculation = _recalculateElectrical
            .Execute(inputs, allocation)
            .GetValueOrThrow();
        return OperationResult<CandidateDerivedState>.Success(new CandidateDerivedState(
            recalculation.CircuitResults,
            allocation,
            recalculation.ElectricalAreaResult,
            qualityReports));
    }

    private static CenterPitchQualityReport Rebind(CenterPitchQualityReport report, DependencyStamp stamp) =>
        new(
            report.RegionId,
            stamp,
            report.MeasuredSurfaceAreaMm2,
            report.MaximumSampledCenterlineDistanceMm,
            report.MaximumSampledCenterlineDistanceUpperBoundMm,
            report.SurfaceProbeCount,
            report.WireDiameterMm,
            report.TargetCenterPitchMm,
            report.SurfaceSamples,
            report.LanePitchMeasurements,
            report.RuleVersion);

    private static bool IsElectricalOnlyChange(DesignProject current, DesignProject candidate)
    {
        if (candidate.Id != current.Id || candidate.Revision != current.Revision.Next() ||
            candidate.SchemaVersion != current.SchemaVersion || candidate.Name != current.Name ||
            candidate.CreatedUtc != current.CreatedUtc || candidate.SourceModel != current.SourceModel ||
            candidate.WorkingCoordinateSystem != current.WorkingCoordinateSystem ||
            candidate.Rules != current.Rules || candidate.PlacementConstraints != current.PlacementConstraints ||
            !SameReferences(current.FaceReferences, candidate.FaceReferences) ||
            !SameReferences(current.HeatingRegions, candidate.HeatingRegions) ||
            !SameReferences(current.HeatingPaths, candidate.HeatingPaths) ||
            !SameReferences(current.ConnectionPoints, candidate.ConnectionPoints) ||
            !SameReferences(current.ConnectionPaths, candidate.ConnectionPaths))
        {
            return false;
        }

        if (current.WireSpecification is not { } currentWire || candidate.WireSpecification is not { } candidateWire ||
            candidateWire.DiameterMm != currentWire.DiameterMm ||
            !candidate.Circuits.Select(circuit => circuit.Id).ToHashSet()
                .SetEquals(current.Circuits.Select(circuit => circuit.Id)))
        {
            return false;
        }

        Dictionary<CircuitId, Circuit> candidateCircuits = candidate.Circuits.ToDictionary(circuit => circuit.Id);
        return current.Circuits.All(circuit =>
        {
            Circuit replacement = candidateCircuits[circuit.Id];
            return circuit.Sequence == replacement.Sequence &&
                circuit.StartConnectionPointId == replacement.StartConnectionPointId &&
                circuit.EndConnectionPointId == replacement.EndConnectionPointId &&
                circuit.Segments.SequenceEqual(replacement.Segments);
        });
    }

    private static bool SameReferences<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) where T : class =>
        first.Count == second.Count && first.Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second));

    private static bool HasExactCircuitIds(IEnumerable<Circuit> circuits, IEnumerable<CircuitId> suppliedIds) =>
        circuits.Select(circuit => circuit.Id).ToHashSet().SetEquals(suppliedIds);

    private static OperationResult<T> Failure<T>(DesignProject? project, string code, string evidence) =>
        OperationResult<T>.Failure([CreateIssue(project, code, evidence)]);

    private static CandidateOutcome Outcome(
        CandidateStatus status,
        SessionSnapshot snapshot,
        string code,
        string evidence) =>
        new(status, snapshot, [CreateIssue(snapshot.CurrentProject, code, evidence)]);

    private static ValidationIssue CreateIssue(DesignProject? project, string code, string evidence) =>
        new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            project is null ? Array.Empty<Guid>() : [project.Id.Value],
            project?.Rules.ValidationRuleVersion ?? "application-electrical-v1",
            evidence: [evidence]);
}
