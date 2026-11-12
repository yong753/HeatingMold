namespace HeatingMold.App.VisualTesting;

/// <summary>
/// 확인 대화상자를 시나리오 응답 큐로 처리할 수 있는 시점을 판정한다.
/// 큐는 단계 실행 중에만 유효하며, 단계 실행이 끝나면
/// <see cref="MainWindow.OnVisualTestFinished"/>가 큐를 비우고 수동 검토로 전환한다.
/// 그 뒤의 확인 요청(앱 종료, 경로 편집 삭제, 영역 분할)은 제품 대화상자로 처리해야 한다.
/// 세션 존재만으로 판단하면 완료 후 종료에서 빈 큐를 소비해 예외가 발생한다.
/// </summary>
internal static class VisualTestConfirmationPolicy
{
    public static bool UsesQueuedResponses(bool sessionExists, VisualTestRunner runner)
        => sessionExists && runner != null && !runner.IsFinished;
}
