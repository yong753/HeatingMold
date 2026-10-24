using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Circuits;

namespace HeatingMold.Design.Tests;

/// <summary>길이 원장 서비스가 프로젝트 소속과 최신 기하 입력을 요구하는지 검사합니다.</summary>
public sealed class ConnectionLengthLedgerBuilderTests
{
    /// <summary>다른 프로젝트 회로의 구간을 현재 프로젝트 원장에 섞지 않습니다.</summary>
    [Fact]
    public void Build_CircuitOutsideProjectReturnsDiagnostic()
    {
        byte[] bytes = [1, 2, 3];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, 1, "TestImporter", "1", "face-v1");
        DesignProject project = DesignProject.CreateEmpty(
            ProjectId.New(), identity,
            new WorkingCoordinateSystemDefinition(
                default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)));
        var circuit = new Circuit(
            CircuitId.New(),
            0,
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, false)],
            ConnectionPointId.New(),
            ConnectionPointId.New(),
            new Supply(null, null, null));
        var stamp = new DependencyStamp(project.Revision, sourceHash, new string('a', 64), new string('b', 64), "test-v1");
        var geometry = new GeometrySnapshot(GeometryDocumentId.New(), new ModelRevision(1), stamp, []);

        var result = new ConnectionLengthLedgerBuilder().Build(project, circuit, geometry);

        Assert.False(result.IsSuccess);
        Assert.Equal("CircuitNotInProject", Assert.Single(result.Issues).Code);
    }
}
