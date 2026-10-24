using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>OCCT의 면 특징과 UV 경계를 재열기용 관리형 Face 특징으로 변환합니다.</summary>
public sealed class FaceFeatureBridgeAdapter
{
    private readonly FaceReferenceFeatureBuilder _featureBuilder = new();

    /// <summary>현재 모델의 모든 Face 특징을 읽고 실패 시 부분 결과를 반환하지 않습니다.</summary>
    public bool TryReadCurrentModelFeatures(
        out IReadOnlyList<NativeFaceFeature> features,
        out string diagnostic)
        => TryReadFeatures(candidateId: null, out features, out diagnostic);

    /// <summary>활성 문서를 바꾸지 않고 준비 후보의 모든 Face 특징을 읽습니다.</summary>
    public bool TryReadPreparedCandidateFeatures(
        long candidateId,
        out IReadOnlyList<NativeFaceFeature> features,
        out string diagnostic)
    {
        if (candidateId <= 0)
        {
            features = Array.Empty<NativeFaceFeature>();
            diagnostic = "준비된 Native 후보 ID가 유효하지 않습니다.";
            return false;
        }

        return TryReadFeatures(candidateId, out features, out diagnostic);
    }

    private bool TryReadFeatures(
        long? candidateId,
        out IReadOnlyList<NativeFaceFeature> features,
        out string diagnostic)
    {
        features = Array.Empty<NativeFaceFeature>();
        diagnostic = null;

        FaceGeometryFeaturesResult nativeResult = candidateId.HasValue
            ? Bridge.GetPreparedCandidateFaceGeometryFeatures(candidateId.Value)
            : Bridge.GetFaceGeometryFeatures();
        if (nativeResult is null)
        {
            diagnostic = "Native Face 특징 질의 결과가 비어 있습니다.";
            return false;
        }

        if (nativeResult.Status != FaceFeatureQueryStatus.Success)
        {
            diagnostic = nativeResult.Status switch
            {
                FaceFeatureQueryStatus.InvalidModel => "현재 문서에 읽은 모델이 없습니다.",
                FaceFeatureQueryStatus.GeometryFailure => "OCCT가 Face 특징을 계산하지 못했습니다.",
                FaceFeatureQueryStatus.CandidateNotFound => "Face 특징을 읽을 준비 후보를 찾지 못했습니다.",
                _ => "Native Face 특징 질의 상태를 알 수 없습니다."
            };
            return false;
        }

        if (nativeResult.Features is null || nativeResult.Features.Length == 0)
        {
            diagnostic = "Native Face 특징 목록이 비어 있습니다.";
            return false;
        }

        try
        {
            NativeFaceFeatureSnapshot[] snapshots = nativeResult.Features
                .Select(feature => CreateSnapshot(feature, candidateId))
                .ToArray();
            features = _featureBuilder.Build(snapshots);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            diagnostic = string.Concat("Face 특징 변환 실패: ", exception.Message);
            features = Array.Empty<NativeFaceFeature>();
            return false;
        }
    }

    private static NativeFaceFeatureSnapshot CreateSnapshot(FaceGeometryFeatureData nativeFeature, long? candidateId)
    {
        if (nativeFeature is null)
            throw new ArgumentException("Native Face 특징 목록에 null 항목이 있습니다.");
        if (nativeFeature.SurfaceType is null)
            throw new ArgumentException("Native 표면 종류가 비어 있습니다.");

        double[] center = RequireLength(nativeFeature.Center, 3, nameof(nativeFeature.Center));
        if (nativeFeature.AdjacentFaceIndices is null)
            throw new ArgumentException("Native 인접 Face 목록이 비어 있습니다.");

        UvWire[] nativeWires = candidateId.HasValue
            ? Bridge.GetPreparedCandidateFaceUvBoundary(candidateId.Value, nativeFeature.FaceIndex)
            : Bridge.GetFaceUvBoundary(nativeFeature.FaceIndex);
        if (nativeWires is null)
            throw new ArgumentException("Native Face 경계를 읽지 못했습니다.");

        FaceBoundaryLoopSnapshot[] boundaryLoops = nativeWires
            .Select(wire => CreateBoundaryLoop(nativeFeature.FaceIndex, wire, candidateId))
            .ToArray();

        return new NativeFaceFeatureSnapshot(
            nativeFeature.FaceIndex,
            MapGeometryKind(nativeFeature.SurfaceType),
            new AreaMm2(nativeFeature.SurfaceAreaMm2),
            new Vec3(center[0], center[1], center[2]),
            nativeFeature.AdjacentFaceIndices,
            boundaryLoops);
    }

    private static FaceBoundaryLoopSnapshot CreateBoundaryLoop(int faceIndex, UvWire wire, long? candidateId)
    {
        if (wire is null || !wire.Closed)
            throw new ArgumentException("Native Face 경계 와이어가 닫혀 있지 않습니다.");

        double[] uvPoints = wire.Points;
        if (uvPoints is null || uvPoints.Length < 6 || uvPoints.Length % 2 != 0)
            throw new ArgumentException("Native Face UV 경계 표본이 올바르지 않습니다.");

        var points = new List<Vec3>(uvPoints.Length / 2);
        for (int index = 0; index < uvPoints.Length; index += 2)
        {
            double[] modelPoint = candidateId.HasValue
                ? Bridge.SamplePreparedCandidateUvTo3D(candidateId.Value, faceIndex, uvPoints[index], uvPoints[index + 1])
                : Bridge.SampleUvTo3D(faceIndex, uvPoints[index], uvPoints[index + 1]);
            double[] coordinates = RequireLength(modelPoint, 3, nameof(modelPoint));
            points.Add(new Vec3(coordinates[0], coordinates[1], coordinates[2]));
        }

        return new FaceBoundaryLoopSnapshot(!wire.IsOuter, wire.Closed, points);
    }

    private static FaceGeometryKind MapGeometryKind(string surfaceType) => surfaceType switch
    {
        "Plane" => FaceGeometryKind.Plane,
        "Cylinder" => FaceGeometryKind.Cylinder,
        "Cone" => FaceGeometryKind.Cone,
        "Sphere" => FaceGeometryKind.Sphere,
        "Torus" => FaceGeometryKind.Torus,
        _ => FaceGeometryKind.Other
    };

    private static double[] RequireLength(double[] values, int expectedLength, string parameterName)
    {
        if (values is null || values.Length != expectedLength)
            throw new ArgumentException(
                string.Format("Native {0} 결과는 길이가 {1}이어야 합니다.", parameterName, expectedLength),
                parameterName);

        return values;
    }
}
