using System;
using HeatingMold.Application.Ports.Runtime;

namespace HeatingMold.App;

/// <summary>Application 알림을 WPF 화면으로 전달합니다.</summary>
internal sealed class ApplicationEventAdapter : IApplicationEvents
{
    /// <summary>전달된 Application 알림입니다.</summary>
    internal event Action<ApplicationEvent> Published;

    /// <summary>등록된 화면 구독자에게 알림을 전달합니다.</summary>
    public void Publish(ApplicationEvent applicationEvent)
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        Published?.Invoke(applicationEvent);
    }
}
