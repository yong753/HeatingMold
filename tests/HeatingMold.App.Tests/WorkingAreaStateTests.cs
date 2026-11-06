using HeatingMold.App;
using HeatingMold.Core.Model;

namespace HeatingMold.App.Tests;

/// <summary>
/// 순수 상태 객체 테스트. WorkingArea는 OccBridge 타입을 포함하므로
/// 네이티브/WPF 하네스에서 검증하고, 여기서는 네이티브 의존성이 없는
/// 생성 조건 스냅샷만 확인한다.
/// </summary>
public class AppliedGenerationInputsTests
{
    [Fact]
    public void AppliedInputsPreserveGenerationSource()
    {
        var inputs = new AppliedGenerationInputs
        {
            Margin = 5.0,
            Spacing = 50.0,
            Direction = PatternDirection.FrameX,
            RouteContextKey = "ctx-1"
        };

        Assert.Equal(5.0, inputs.Margin);
        Assert.Equal(50.0, inputs.Spacing);
        Assert.Equal(PatternDirection.FrameX, inputs.Direction);
        Assert.Equal("ctx-1", inputs.RouteContextKey);
    }
}
