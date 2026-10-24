using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.App.Tests;

/// <summary>간격 문제 표시 위치가 실제 clearance 진단에서만 선택되는지 확인합니다.</summary>
public sealed class ClearanceIssueLocationPresentationTests
{
    /// <summary>간격 실패가 있으면 진단 순서의 첫 위치를 선택합니다.</summary>
    [Fact]
    public void TryGetLocation_SelectsFirstClearanceIssueAndSkipsOtherIssueCodes()
    {
        var expectedLocation = new Vec3(2, 3, 4);
        ValidationIssue[] issues =
        [
            CreateIssue("UnrelatedGeometryIssue", new Vec3(8, 8, 8)),
            CreateIssue("MinimumClearGapViolation", expectedLocation),
            CreateIssue("MinimumClearGapUnresolved", new Vec3(5, 6, 7))
        ];

        bool found = ClearanceIssueLocationPresentation.TryGetLocation(issues, out Vec3 location);

        Assert.True(found);
        Assert.Equal(expectedLocation, location);
    }

    /// <summary>허용 코드라도 측정 위치가 없으면 뷰포트 위치를 만들지 않습니다.</summary>
    [Fact]
    public void TryGetLocation_RejectsClearanceIssueWithoutLocation()
    {
        ValidationIssue[] issues =
        [
            CreateIssue("MinimumClearGapViolation", location: null),
            CreateIssue("ClearanceMeasurementIncomplete", location: null)
        ];

        bool found = ClearanceIssueLocationPresentation.TryGetLocation(issues, out Vec3 location);

        Assert.False(found);
        Assert.Equal(default(Vec3), location);
    }

    private static ValidationIssue CreateIssue(string code, Vec3? location) => new(
        code,
        ValidationSeverity.Error,
        $"Geometry.{code}",
        [],
        "clearance-location-test-v1",
        location3D: location);
}
