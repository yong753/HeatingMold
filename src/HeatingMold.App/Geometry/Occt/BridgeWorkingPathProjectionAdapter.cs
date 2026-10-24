using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>작업 경로 후보 포트를 기존 OCCT Bridge API에 연결합니다.</summary>
internal sealed class BridgeWorkingPathProjectionAdapter : IWorkingPathProjectionBridge
{
    /// <summary>모델 좌표 점으로 만든 다중선 곡선을 선택 Face에 투영합니다.</summary>
    public WorkingPathSegmentMeasurement StageProjectedPathSegment(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        string kind,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection)
    {
        double[] points = Flatten(modelPoints);
        var definition = new CurveDefinitionBridgeDto("Polyline", "Forward", points: points);
        HeatingPathSegmentResult result = Bridge.StageProjectedWorkingCurveSegment(
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagingId,
            kind,
            CurveDefinitionBridgeAdapter.ToNativeData(definition),
            selectedFaceIndices.ToArray(),
            projectionDirection.X,
            projectionDirection.Y,
            projectionDirection.Z);

        return ConvertResult(result);
    }

    /// <summary>독립 경로를 선택 Face에 투영하고 Native 측정값을 반환합니다.</summary>
    public WorkingPathSegmentMeasurement StageProjectedPolyline(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection)
    {
        HeatingPathSegmentResult result = Bridge.StageProjectedWorkingPolyline(
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagingId,
            Flatten(modelPoints),
            selectedFaceIndices.ToArray(),
            projectionDirection.X,
            projectionDirection.Y,
            projectionDirection.Z);

        return ConvertResult(result);
    }

    /// <summary>후보 ID 집합을 Native에서 확정하고 표시를 교체합니다.</summary>
    public bool CommitWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> finalSegmentIds,
        IReadOnlyList<int> stagedPolylineIds) => Bridge.CommitWorkingPathGeometry(
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagedSegmentIds.ToArray(),
            finalSegmentIds.ToArray(),
            stagedPolylineIds.ToArray());

    /// <summary>아직 확정하지 않은 Native 후보 조각과 다중선을 폐기합니다.</summary>
    public void DiscardWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> stagedPolylineIds) => Bridge.DiscardWorkingPathGeometry(
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagedSegmentIds.ToArray(),
            stagedPolylineIds.ToArray());

    /// <summary>점 목록을 Native 곡선 DTO의 XYZ 배열로 펼칩니다.</summary>
    internal static double[] Flatten(IReadOnlyList<Vec3> points) => points
        .SelectMany(point => new[] { point.X, point.Y, point.Z })
        .ToArray();

    /// <summary>Native 결과 형식과 수치를 확인한 뒤 관리형 측정값으로 바꿉니다.</summary>
    internal static WorkingPathSegmentMeasurement ConvertResult(
        HeatingPathSegmentResult result,
        bool requireApproximation = false)
    {
        if (result?.Start is not { Length: 3 } start || result.End is not { Length: 3 } end ||
            start.Concat(end).Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("OCCT 경로 staging 결과의 끝점 형식이 유효하지 않습니다.");
        }

        Vec3 startPoint = new(start[0], start[1], start[2]);
        Vec3 endPoint = new(end[0], end[1], end[2]);
        if (!double.IsFinite(result.Length) || result.Length <= 0)
            throw new InvalidOperationException("OCCT 경로 staging 결과의 길이가 양의 유한값이 아닙니다.");

        bool hasApproximation = result.Samples != null || result.ProbeCount != 0 ||
            result.RequestedDeflectionMm != 0 || result.RequestedAngularToleranceRad != 0 ||
            result.MaximumMeasuredDeviationMm != 0 || result.MaximumSampleSpacingMm != 0;
        if (!hasApproximation)
        {
            if (requireApproximation)
                throw new InvalidOperationException("후보 곡선 staging 결과에 Native 표본 근사 증거가 없습니다.");

            return new WorkingPathSegmentMeasurement(result.Length, startPoint, endPoint);
        }

        if (result.Samples is null || result.Samples.Length < 6 || result.Samples.Length % 3 != 0 ||
            result.Samples.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("OCCT 경로 표본은 유한한 XYZ 좌표 세 개 이상이어야 합니다.");
        }

        Vec3[] samples = new Vec3[result.Samples.Length / 3];
        for (int index = 0; index < samples.Length; index++)
        {
            int offset = index * 3;
            samples[index] = new Vec3(
                result.Samples[offset],
                result.Samples[offset + 1],
                result.Samples[offset + 2]);
        }

        if (samples[0] != startPoint || samples[^1] != endPoint)
            throw new InvalidOperationException("OCCT 경로 표본의 양 끝점이 측정 결과와 일치하지 않습니다.");

        double observedMaximumSpacingMm = samples
            .Zip(samples.Skip(1), (first, second) => (second - first).Length)
            .DefaultIfEmpty(0)
            .Max();
        if (requireApproximation &&
            (!double.IsFinite(result.RequestedDeflectionMm) || result.RequestedDeflectionMm <= 0 ||
             !double.IsFinite(result.RequestedAngularToleranceRad) || result.RequestedAngularToleranceRad <= 0 ||
             !double.IsFinite(result.MaximumSampleSpacingMm) || result.MaximumSampleSpacingMm <= 0 ||
             result.ProbeCount <= 0 || string.IsNullOrWhiteSpace(result.ApproximationRuleVersion)))
        {
            throw new InvalidOperationException("후보 곡선 근사 설정이나 확인 증거가 완전하지 않습니다.");
        }
        if (result.MaximumSampleSpacingMm + 1e-9 < observedMaximumSpacingMm)
            throw new InvalidOperationException("OCCT 근사 결과의 최대 표본 간격이 실제 표본과 맞지 않습니다.");
        if (result.ApproximationComplete &&
            (result.MaximumMeasuredDeviationMm > result.RequestedDeflectionMm + 1e-9 ||
             observedMaximumSpacingMm > result.MaximumSampleSpacingMm + 1e-9))
        {
            throw new InvalidOperationException("OCCT 근사 결과가 완료를 표시했지만 요청한 편차나 간격 한도를 넘었습니다.");
        }

        var approximation = new HeatingMold.Core.Geometry.CurveApproximationInfo(
            result.RequestedDeflectionMm,
            result.RequestedAngularToleranceRad,
            result.MaximumMeasuredDeviationMm,
            result.MaximumSampleSpacingMm,
            result.ProbeCount,
            result.ApproximationComplete,
            result.ApproximationRuleVersion ?? "occt-wire-sampling-v1");

        return new WorkingPathSegmentMeasurement(
            result.Length,
            startPoint,
            endPoint,
            samples,
            approximation);
    }

    /// <summary>완전한 Native 표본 근거를 Infrastructure 측정 계약으로 변환합니다.</summary>
    internal static OcctCurveMeasurement ConvertExactCurveMeasurement(HeatingPathSegmentResult result)
    {
        WorkingPathSegmentMeasurement measurement = ConvertResult(result, requireApproximation: true);
        if (measurement.Approximation is null)
            throw new InvalidOperationException("후보 곡선에 Native 근사 증거가 없습니다.");

        return new OcctCurveMeasurement(
            new LengthMm(measurement.LengthMm),
            measurement.Start,
            measurement.End,
            measurement.Samples,
            measurement.Approximation);
    }
}
