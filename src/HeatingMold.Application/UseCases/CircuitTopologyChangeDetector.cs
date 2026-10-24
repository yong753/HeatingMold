using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;

namespace HeatingMold.Application.UseCases;

/// <summary>회로 topology 계획이 확정 프로젝트를 실제로 바꾸는지 판정합니다.</summary>
public static class CircuitTopologyChangeDetector
{
    /// <summary>회전 상태, 회로 순서·구간·공급과 양 끝 연결부를 비교합니다.</summary>
    public static bool HasChanges(DesignProject project, CircuitTopologyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        if (HasTurnStateChanges(project, plan))
            return true;
        if (project.Circuits.Count != plan.Mapping.Assignments.Count)
            return true;

        Dictionary<CircuitId, Circuit> circuitsById = project.Circuits
            .ToDictionary(circuit => circuit.Id);
        for (int index = 0; index < plan.Mapping.Assignments.Count; index++)
        {
            CircuitIdentityAssignment assignment = plan.Mapping.Assignments[index];
            if (!circuitsById.TryGetValue(assignment.CircuitId, out Circuit? circuit) ||
                circuit is null ||
                circuit.Sequence != index ||
                !circuit.Segments.SequenceEqual(assignment.Component.Segments) ||
                circuit.Supply != assignment.PreservedSupply ||
                !EndpointMatches(
                    project,
                    circuit.Id,
                    ConnectionPointRole.Start,
                    assignment.StartConnectionPointId,
                    assignment.Component.Segments[0].SegmentId,
                    assignment.Component.StartControlPointId) ||
                !EndpointMatches(
                    project,
                    circuit.Id,
                    ConnectionPointRole.End,
                    assignment.EndConnectionPointId,
                    assignment.Component.Segments[^1].SegmentId,
                    assignment.Component.EndControlPointId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>회전 구간 초안과 확정 경로의 활성 상태를 비교합니다.</summary>
    private static bool HasTurnStateChanges(DesignProject project, CircuitTopologyPlan plan)
    {
        Dictionary<HeatingPathSegmentId, bool> committedStates = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.Kind == HeatingPathSegmentKind.Turn)
            .ToDictionary(segment => segment.Id, segment => segment.IsActive);

        return plan.TurnSegmentStates.Any(requested =>
            committedStates.TryGetValue(requested.Key, out bool committedState) &&
            committedState != requested.Value);
    }

    /// <summary>안정 연결부 ID·역할·절점과 구간이 배정 결과에 맞는지 확인합니다.</summary>
    private static bool EndpointMatches(
        DesignProject project,
        CircuitId circuitId,
        ConnectionPointRole role,
        ConnectionPointId? expectedPointId,
        HeatingPathSegmentId expectedSegmentId,
        ControlPointId expectedControlPointId)
    {
        if (!expectedPointId.HasValue)
            return false;

        ConnectionPoint[] matches = project.ConnectionPoints
            .Where(point => point.Id == expectedPointId.Value)
            .ToArray();
        return matches.Length == 1 &&
            matches[0].CircuitId == circuitId &&
            matches[0].Role == role &&
            matches[0].HeatingPathSegmentId == expectedSegmentId &&
            matches[0].ControlPointId == expectedControlPointId;
    }
}
