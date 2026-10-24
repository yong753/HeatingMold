using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.Tests;

/// <summary>정사영 출력 유스케이스의 입력 검증과 출력 포트 전달을 검사합니다.</summary>
public sealed class ExportOrthographicViewsTests
{
    /// <summary>유효한 요청은 정리된 경로와 같은 도면 목록을 출력 포트에 전달합니다.</summary>
    [Fact]
    public void Execute_ForwardsValidatedViewsToExporter()
    {
        var exporter = new FakeOrthographicDxfExporter();
        var useCase = new ExportOrthographicViews(exporter);
        var view = new OrthographicDxfView([], [], [], []);

        OperationResult<ExportReceipt> result = useCase.Execute("  fixture.dxf  ", [view]);

        Assert.True(result.IsSuccess);
        Assert.Equal("fixture.dxf", exporter.Path);
        Assert.Same(view, Assert.Single(exporter.Views));
    }

    /// <summary>빈 경로는 출력 포트를 호출하지 않고 유효성 오류로 반환합니다.</summary>
    [Fact]
    public void Execute_RejectsEmptyPathBeforeCallingExporter()
    {
        var exporter = new FakeOrthographicDxfExporter();
        var useCase = new ExportOrthographicViews(exporter);

        OperationResult<ExportReceipt> result = useCase.Execute(" ", []);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidPath", Assert.Single(result.Issues).Code);
        Assert.Null(exporter.Path);
    }

    private sealed class FakeOrthographicDxfExporter : IOrthographicDxfExporter
    {
        public string? Path { get; private set; }
        public IReadOnlyList<OrthographicDxfView> Views { get; private set; } = [];

        public OperationResult<ExportReceipt> ExportViews(
            string path,
            IReadOnlyList<OrthographicDxfView> views,
            CancellationToken cancellationToken = default)
        {
            Path = path;
            Views = views;
            return OperationResult<ExportReceipt>.Success(
                new ExportReceipt(path, ExportFormat.Dxf, 12, new string('a', 64)));
        }
    }
}
