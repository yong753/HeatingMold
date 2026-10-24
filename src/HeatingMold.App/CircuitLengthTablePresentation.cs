using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App;

/// <summary>회로 길이 표에서 한 회로의 발열·연결 경로 길이를 표시합니다.</summary>
internal sealed record CircuitLengthRow(string Circuit, double HeatingLength, double ConnectionLength)
{
    /// <summary>발열 경로와 연결 경로 길이의 합계(mm)입니다.</summary>
    public double TotalLength => HeatingLength + ConnectionLength;
}

/// <summary>확정 프로젝트와 기하 측정값을 회로 길이 표의 화면 행으로 변환합니다.</summary>
internal static class CircuitLengthTablePresentation
{
    /// <summary>회로 순서대로 각 회로의 발열 경로와 양 끝 연결 경로 길이를 집계합니다.</summary>
    public static IReadOnlyList<CircuitLengthRow> BuildRows(
        DesignProject project,
        GeometrySnapshot geometry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(geometry);

        Dictionary<HeatingPathSegmentId, double> heatingLengths = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value, curve => curve.LengthMm.Value);
        Dictionary<ConnectionPathSegmentId, double> connectionLengths = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value, curve => curve.LengthMm.Value);

        return project.Circuits
            .OrderBy(circuit => circuit.Sequence)
            .Select(circuit => CreateRow(circuit, project.ConnectionPaths, heatingLengths, connectionLengths))
            .ToArray();
    }

    /// <summary>확정 측정 길이와 회로가 소유한 연결부만 합산해 표시 행을 만듭니다.</summary>
    private static CircuitLengthRow CreateRow(
        Circuit circuit,
        IReadOnlyList<ConnectionPath> connectionPaths,
        IReadOnlyDictionary<HeatingPathSegmentId, double> heatingLengths,
        IReadOnlyDictionary<ConnectionPathSegmentId, double> connectionLengths)
    {
        double heatingLength = circuit.Segments
            .Where(segment => heatingLengths.ContainsKey(segment.SegmentId))
            .Sum(segment => heatingLengths[segment.SegmentId]);
        HashSet<ConnectionPointId> endpointIds =
        [circuit.StartConnectionPointId, circuit.EndConnectionPointId];
        double connectionLength = connectionPaths
            .Where(path => endpointIds.Contains(path.ConnectionPointId))
            .SelectMany(path => path.Segments)
            .Where(segment => connectionLengths.ContainsKey(segment.Id))
            .Sum(segment => connectionLengths[segment.Id]);

        string circuitLabel = string.Concat(
            "회로 ",
            (circuit.Sequence + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
            " · ",
            circuit.Id.Value.ToString("N")[..8]);
        return new CircuitLengthRow(circuitLabel, heatingLength, connectionLength);
    }
}
