using HeatingMold.App.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>준비 후보 특징 조회가 활성 문서 API와 분리되는지 확인합니다.</summary>
public sealed class FaceFeatureBridgeAdapterTests
{
    /// <summary>Face 특징·UV 경계·3D 표본을 모두 같은 후보 ID로 읽습니다.</summary>
    [Fact]
    public void TryReadPreparedCandidateFeatures_UsesCandidateScopedQueries()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateFaceGeometryFeatures = new FaceGeometryFeaturesResult
        {
            Status = FaceFeatureQueryStatus.Success,
            Features =
            [
                new FaceGeometryFeatureData
                {
                    FaceIndex = 2,
                    SurfaceType = "Plane",
                    SurfaceAreaMm2 = 100,
                    Center = [1, 2, 3],
                    AdjacentFaceIndices = []
                }
            ]
        };

        var adapter = new FaceFeatureBridgeAdapter();
        bool succeeded = adapter.TryReadPreparedCandidateFeatures(73, out var features, out string diagnostic);

        Assert.True(succeeded, diagnostic);
        Assert.Single(features);
        Assert.Equal(2, features[0].NativeFaceIndex);
        Assert.Equal(73, Bridge.LastPreparedCandidateFaceQueryId);
        Assert.Equal(73, Bridge.LastPreparedCandidateBoundaryQueryId);
        Assert.Equal(73, Bridge.LastPreparedCandidateSampleQueryId);
        Assert.Equal(0, Bridge.ActiveFaceFeatureQueryCount);
    }

    /// <summary>없어진 후보는 명시 실패로 전달하고 활성 문서로 질의를 바꾸지 않습니다.</summary>
    [Fact]
    public void TryReadPreparedCandidateFeatures_ReportsMissingCandidate()
    {
        Bridge.Reset();
        var adapter = new FaceFeatureBridgeAdapter();

        bool succeeded = adapter.TryReadPreparedCandidateFeatures(81, out var features, out string diagnostic);

        Assert.False(succeeded);
        Assert.Empty(features);
        Assert.Contains("준비 후보", diagnostic);
        Assert.Equal(81, Bridge.LastPreparedCandidateFaceQueryId);
        Assert.Equal(0, Bridge.ActiveFaceFeatureQueryCount);
    }
}
