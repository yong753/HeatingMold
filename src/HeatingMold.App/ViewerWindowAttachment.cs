using System;

namespace HeatingMold.App
{
    /// <summary>뷰어가 연결된 자식 창과 Native 초기화 상태를 관리합니다.</summary>
    internal sealed class ViewerWindowAttachment
    {
        private IntPtr _attachedWindow;

        /// <summary>지정한 창에 Native 뷰어가 연결되어 있는지 반환합니다.</summary>
        public bool IsAttachedTo(IntPtr windowHandle) =>
            windowHandle != IntPtr.Zero && _attachedWindow == windowHandle;

        /// <summary>새 창을 Native 뷰어에 연결하고 성공한 창만 기억합니다.</summary>
        public bool TryAttach(IntPtr windowHandle, Func<IntPtr, bool> initialize)
        {
            if (windowHandle == IntPtr.Zero)
                return false;
            if (initialize == null)
                throw new ArgumentNullException(nameof(initialize));
            if (IsAttachedTo(windowHandle))
                return true;
            if (!initialize(windowHandle))
                return false;

            _attachedWindow = windowHandle;
            return true;
        }

        /// <summary>현재 연결된 창이 폐기될 때 연결 상태를 해제합니다.</summary>
        public void Detach(IntPtr windowHandle)
        {
            if (_attachedWindow == windowHandle)
                _attachedWindow = IntPtr.Zero;
        }
    }
}
