using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Export;

/// <summary>확정 snapshot의 곡선을 회로별 STEP wire 데이터로 묶습니다.</summary>
public sealed class StepDesignPayloadBuilder
{
    private readonly CurveDefinitionMapper _curveMapper;

    /// <summary>Core 곡선과 OCCT wire DTO 사이의 매퍼를 연결합니다.</summary>
    public StepDesignPayloadBuilder(CurveDefinitionMapper? curveMapper = null)
    {
        _curveMapper = curveMapper ?? new CurveDefinitionMapper();
    }

    /// <summary>회로 방향, 연결부별 경로, 회로 미배정 곡선을 보존해 payload를 만듭니다.</summary>
    public OperationResult<IReadOnlyList<StepDesignGroup>> Build(ExportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            return BuildValidated(snapshot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Failure("StepPayloadInvalid", exception.Message);
        }
    }

    private OperationResult<IReadOnlyList<StepDesignGroup>> BuildValidated(ExportSnapshot snapshot)
    {
        Dictionary<Guid, CurveGeometrySnapshot> heatingCurves = snapshot.Geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value.Value);
        Dictionary<Guid, CurveGeometrySnapshot> connectionCurves = snapshot.Geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value.Value);
        Dictionary<ConnectionPointId, ConnectionPath> pathsByConnectionPoint = snapshot.Project.ConnectionPaths
            .ToDictionary(path => path.ConnectionPointId);

        var groups = new List<StepDesignGroup>();
        var assignedHeatingSegmentIds = new HashSet<Guid>();
        foreach (Circuit circuit in snapshot.Project.Circuits
                     .OrderBy(circuit => circuit.Sequence)
                     .ThenBy(circuit => circuit.Id.Value))
        {
            StepDesignCurve[] orderedHeatingCurves = circuit.Segments
                .OrderBy(segment => segment.Sequence)
                .Select(segment =>
                {
                    Guid segmentId = segment.SegmentId.Value;
                    if (!heatingCurves.TryGetValue(segmentId, out CurveGeometrySnapshot? curve) || curve is null)
                        throw new InvalidOperationException($"회로 {circuit.Id}의 발열 곡선 {segmentId}가 최종 geometry snapshot에 없습니다.");
                    if (!assignedHeatingSegmentIds.Add(segmentId))
                        throw new InvalidOperationException($"발열 곡선 {segmentId}가 둘 이상의 회로에 배정되었습니다.");

                    return new StepDesignCurve(
                        segmentId,
                        _curveMapper.ToBridgeDto(curve.Definition),
                        segment.IsReversed);
                })
                .ToArray();

            var wires = new List<StepDesignWire>
            {
                new($"C{circuit.Sequence + 1:D3}-HEATING", orderedHeatingCurves)
            };

            foreach (ConnectionPoint connectionPoint in snapshot.Project.ConnectionPoints
                         .Where(point => point.CircuitId == circuit.Id)
                         .OrderBy(point => point.Role))
            {
                if (!pathsByConnectionPoint.TryGetValue(connectionPoint.Id, out ConnectionPath? path) || path is null)
                    continue;

                StepDesignCurve[] orderedConnectionCurves = path.Segments
                    .OrderBy(segment => segment.Sequence)
                    .Select(segment =>
                    {
                        Guid segmentId = segment.Id.Value;
                        if (!connectionCurves.TryGetValue(segmentId, out CurveGeometrySnapshot? curve) || curve is null)
                            throw new InvalidOperationException($"연결 곡선 {segmentId}가 최종 geometry snapshot에 없습니다.");

                        return new StepDesignCurve(
                            segmentId,
                            _curveMapper.ToBridgeDto(curve.Definition),
                            isReversed: false);
                    })
                    .ToArray();
                wires.Add(new StepDesignWire(
                    $"C{circuit.Sequence + 1:D3}-{connectionPoint.Role.ToString().ToUpperInvariant()}-CONNECTION",
                    orderedConnectionCurves));
            }

            groups.Add(new StepDesignGroup(circuit.Id.Value, $"C{circuit.Sequence + 1:D3}", wires));
        }

        foreach (HeatingPath path in snapshot.Project.HeatingPaths.OrderBy(path => path.Id.Value))
        {
            HeatingPathSegment[] unassignedSegments = path.Segments
                .Where(segment => segment.IsActive && !assignedHeatingSegmentIds.Contains(segment.Id.Value))
                .OrderBy(segment => segment.Sequence)
                .ToArray();
            if (unassignedSegments.Length == 0)
                continue;

            StepDesignWire[] unassignedWires = unassignedSegments
                .Select(segment =>
                {
                    var curve = new StepDesignCurve(
                        segment.Id.Value,
                        _curveMapper.ToBridgeDto(heatingCurves[segment.Id.Value].Definition),
                        isReversed: false);
                    return new StepDesignWire(
                        $"UNASSIGNED-HEATING-{segment.Id.Value:N}",
                        [curve]);
                })
                .ToArray();
            groups.Add(new StepDesignGroup(
                circuitId: null,
                $"UNASSIGNED-{path.Id.Value:N}",
                unassignedWires));
        }

        if (!snapshot.Geometry.Curves.Any())
            return Failure("StepExportNoGeometry", "STEP으로 출력할 확정 곡선이 없습니다.");

        return OperationResult<IReadOnlyList<StepDesignGroup>>.Success(groups.AsReadOnly());
    }

    private static OperationResult<IReadOnlyList<StepDesignGroup>> Failure(string code, string evidence) =>
        OperationResult<IReadOnlyList<StepDesignGroup>>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Infrastructure.{code}",
            [],
            "step-design-payload-v1",
            evidence: [evidence])]);
}
