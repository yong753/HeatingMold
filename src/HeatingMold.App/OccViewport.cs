using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HeatingMold.OccBridge;

namespace HeatingMold.App
{
    /// <summary>
    /// WPF HwndHost 자식 HWND에 OCCT AIS 뷰어를 임베드 (S1 항목 1·6).
    /// 마우스 메시지는 자식 HWND의 WndProc에서 직접 처리해 Bridge로 전달한다.
    /// </summary>
    public class OccViewport : HwndHost
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        private const uint TME_LEAVE = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr DefWndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly DefWndProcDelegate s_defProc = DefWindowProc;
        private static bool s_classRegistered;

        // "static" 클래스는 CS_PARENTDC라 GL DC가 부모 DC와 공유됨 → 커스텀 클래스 사용 (배경 브러시 없음)
        private const string WindowClassName = "HeatingMoldOccViewport";

        private static void EnsureClassRegistered()
        {
            if (s_classRegistered) return;

            var wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.style = 0;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_defProc);
            wc.hInstance = GetModuleHandle(null);
            wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512); // IDC_ARROW
            wc.hbrBackground = IntPtr.Zero;
            wc.lpszClassName = WindowClassName;

            ushort atom = RegisterClassEx(ref wc);
            // 0 반환 + ERROR_CLASS_ALREADY_EXISTS(1410)면 이미 등록된 것 — 정상
            if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            s_classRegistered = true;
        }

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_PAINT = 0x000F;
        private const int WM_CAPTURECHANGED = 0x0215;

        private IntPtr _hwnd;
        private bool _ready;
        private bool _dragging;
        private bool _dragStarted;
        private bool _trackingMouse;
        private int _downX, _downY;
        private int _lastHover = -2;      // W5 — 호버 이벤트 중복 억제

        private const int ClickDragThreshold = 4;   // px — 이하면 클릭(픽킹), 초과 시 회전

        /// <summary>W3 — 면 클릭 선택 결과 (faceId, 없으면 -1)</summary>
        public event Action<int> FacePicked;

        /// <summary>W5 — 호버 중인 면 변경 (faceId, 없으면 -1)</summary>
        public event Action<int> FaceHovered;

        public bool IsReady => _ready;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClassRegistered();
            _hwnd = CreateWindowEx(0, WindowClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _ready = false;
            _dragging = false;
            _dragStarted = false;
            DestroyWindow(hwnd.Handle);
        }

        /// <summary>뷰어 초기화 (1회). Loaded 이후에 호출할 것.</summary>
        public bool InitViewer()
        {
            if (_ready) return true;
            if (_hwnd == IntPtr.Zero) return false;
            _ready = Bridge.InitViewer(_hwnd, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
            return _ready;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_ready) Bridge.ResizeView((int)ActualWidth, (int)ActualHeight);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    SetCapture(hwnd);
                    _dragging = true;
                    _dragStarted = false;
                    _downX = LoWord(lParam);
                    _downY = HiWord(lParam);
                    handled = true;
                    break;

                case WM_MOUSEMOVE:
                {
                    if (!_trackingMouse)
                    {
                        var tme = new TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)),
                            dwFlags = TME_LEAVE,
                            hwndTrack = hwnd
                        };
                        TrackMouseEvent(ref tme);
                        _trackingMouse = true;
                    }

                    int x = LoWord(lParam), y = HiWord(lParam);
                    if (_dragging)
                    {
                        if (!_dragStarted)
                        {
                            int dx = x - _downX, dy = y - _downY;
                            if (dx * dx + dy * dy >= ClickDragThreshold * ClickDragThreshold)
                            {
                                _dragStarted = true;
                                Bridge.ClearHover();          // 회전 중 잔상 방지
                                Bridge.RotateStart(_downX, _downY);
                            }
                        }
                        if (_dragStarted)
                        {
                            Bridge.RotateMove(x, y);
                            handled = true;
                        }
                    }
                    else if (_ready)
                    {
                        int hover = Bridge.HoverFace(x, y);   // W5 — 호버 면 하이라이트
                        if (hover != _lastHover)
                        {
                            _lastHover = hover;
                            FaceHovered?.Invoke(hover);
                        }
                    }
                    break;
                }

                case WM_MOUSELEAVE:
                    _trackingMouse = false;
                    Bridge.ClearHover();
                    if (_lastHover != -1)
                    {
                        _lastHover = -1;
                        FaceHovered?.Invoke(-1);
                    }
                    handled = true;
                    break;

                case WM_LBUTTONUP:
                    if (_dragging)
                    {
                        _dragging = false;
                        if (_dragStarted)
                        {
                            Bridge.RotateEnd();
                        }
                        else
                        {
                            // 이동 없는 클릭 = 면 선택 (W5 — 하이라이트 유지)
                            int idx = Bridge.SelectFace(LoWord(lParam), HiWord(lParam));
                            FacePicked?.Invoke(idx);
                        }
                        ReleaseCapture();
                        handled = true;
                    }
                    break;

                case WM_CAPTURECHANGED:
                    _dragging = false;
                    _dragStarted = false;
                    break;

                case WM_MOUSEWHEEL:
                {
                    int delta = (short)(((long)wParam >> 16) & 0xFFFF);
                    Bridge.ZoomView(delta > 0 ? 1.15 : 1.0 / 1.15);
                    handled = true;
                    break;
                }

                case WM_ERASEBKGND:
                    handled = true;
                    break;

                case WM_PAINT:
                    // 배경을 그리지 않고 무효 영역만 검증 — GL 콘텐츠 유지
                    ValidateRect(hwnd, IntPtr.Zero);
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
        private static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
    }
}
