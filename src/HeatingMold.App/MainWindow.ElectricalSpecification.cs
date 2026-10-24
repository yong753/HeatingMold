#nullable enable annotations

using HeatingMold.Application.Sessions;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>확정 프로젝트가 바뀌었을 때 전기 ViewModel의 입력을 갱신합니다.</summary>
    private void RefreshElectricalSpecificationEditor(DesignProject? project, bool force = false) =>
        _mainWindowViewModel.ElectricalSpecification.RefreshFromProject(project, force);

    /// <summary>ViewModel 명령의 세션 변경을 확정 결과 패널에 전달합니다.</summary>
    private void OnElectricalSpecificationStateChanged(SessionSnapshot snapshot) =>
        RefreshElectricalResults(snapshot);
}
