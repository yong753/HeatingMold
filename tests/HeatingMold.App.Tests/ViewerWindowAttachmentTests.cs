using System;
using HeatingMold.App;

namespace HeatingMold.App.Tests;

/// <summary>Native 뷰어와 자식 HWND 연결 상태를 검증합니다.</summary>
public sealed class ViewerWindowAttachmentTests
{
    /// <summary>같은 창은 한 번만 초기화하고 새 창은 다시 Native에 연결합니다.</summary>
    [Fact]
    public void TryAttach_ReusesCurrentHandleAndInitializesReplacementHandle()
    {
        var attachment = new ViewerWindowAttachment();
        var initializedHandles = new System.Collections.Generic.List<IntPtr>();
        IntPtr firstHandle = new(101);
        IntPtr replacementHandle = new(202);

        Assert.True(attachment.TryAttach(firstHandle, handle =>
        {
            initializedHandles.Add(handle);
            return true;
        }));
        Assert.True(attachment.TryAttach(firstHandle, handle =>
        {
            initializedHandles.Add(handle);
            return true;
        }));
        Assert.True(attachment.TryAttach(replacementHandle, handle =>
        {
            initializedHandles.Add(handle);
            return true;
        }));

        Assert.Equal([firstHandle, replacementHandle], initializedHandles);
        Assert.False(attachment.IsAttachedTo(firstHandle));
        Assert.True(attachment.IsAttachedTo(replacementHandle));
    }

    /// <summary>Native 초기화 실패와 빈 HWND는 연결 상태로 기록하지 않습니다.</summary>
    [Fact]
    public void TryAttach_FailureCanBeRetriedAndZeroHandleIsIgnored()
    {
        var attachment = new ViewerWindowAttachment();
        IntPtr windowHandle = new(303);
        int initializationCount = 0;

        Assert.False(attachment.TryAttach(IntPtr.Zero, _ =>
        {
            initializationCount++;
            return true;
        }));
        Assert.False(attachment.TryAttach(windowHandle, _ =>
        {
            initializationCount++;
            return false;
        }));
        Assert.False(attachment.IsAttachedTo(windowHandle));
        Assert.True(attachment.TryAttach(windowHandle, _ =>
        {
            initializationCount++;
            return true;
        }));

        Assert.Equal(2, initializationCount);
        Assert.True(attachment.IsAttachedTo(windowHandle));
    }

    /// <summary>현재 연결된 HWND의 해제만 연결 상태에 반영합니다.</summary>
    [Fact]
    public void Detach_IgnoresStaleHandleAndClearsMatchingHandle()
    {
        var attachment = new ViewerWindowAttachment();
        IntPtr windowHandle = new(404);
        IntPtr staleHandle = new(405);

        Assert.True(attachment.TryAttach(windowHandle, _ => true));
        attachment.Detach(staleHandle);
        Assert.True(attachment.IsAttachedTo(windowHandle));

        attachment.Detach(windowHandle);

        Assert.False(attachment.IsAttachedTo(windowHandle));
    }
}
