using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

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
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_PAINT = 0x000F;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_KEYDOWN = 0x0100;

        private IntPtr _hwnd;
        private bool _ready;
        private bool _dragging;
        private bool _panDragging;
        private bool _dragStarted;
        private bool _trackingMouse;
        private int _downX, _downY;
        private int _lastSelectionPreviewX, _lastSelectionPreviewY;
        private int _lastExclusionPreviewX, _lastExclusionPreviewY;
        private int _lastHover = -2;      // W5 — 호버 이벤트 중복 억제
        private int _activeDragMode; // 0=orbit, 1=rectangle selection, 2=exclusion sketch
        private bool _connectorEditMode;
        private bool _circuitHoverMode;
        public bool ConnectorEditMode
        {
            get => _connectorEditMode;
            set { if (_connectorEditMode == value) return; _connectorEditMode = value; if (!value) ConnectorHovered?.Invoke(-1); }
        }
        public bool CircuitHoverMode
        {
            get => _circuitHoverMode;
            set { if (_circuitHoverMode == value) return; _circuitHoverMode = value; if (!value) RoutePieceHovered?.Invoke(-1); }
        }
        private bool _powerLocationMode;
        private DispatcherTimer _powerPreviewTimer;
        private (int X, int Y)? _pendingPowerPreview, _lastPowerPreview;
        public bool PowerLocationMode
        {
            get => _powerLocationMode;
            set
            {
                CancelPowerLeadPreview();
                if (_powerLocationMode == value) return;
                _powerLocationMode = value;
            }
        }

        private const int ClickDragThreshold = 4;   // px — 이하면 클릭(픽킹), 초과 시 회전

        /// <summary>W3 — 면 클릭 선택 결과 (faceId, 없으면 -1)</summary>
        public event Action<int, bool> FacePicked;

        /// <summary>W5 — 호버 중인 면 변경 (faceId, 없으면 -1)</summary>
        public event Action<int> FaceHovered;
        public event Action<int[], bool> FacesRectanglePicked;
        public event Action<int, int, int, int> ExclusionSketchMoved;
        public event Action<int, int, int, int> ExclusionSketchCompleted;
        public event Action ExclusionSketchCancelled;
        public event Action<int> ConnectorHovered;
        public event Action<int> RoutePieceHovered;
        public event Action<int> ConnectorPicked;
        public event Action<int, int> PowerLocationPicked;
        public event Action<int, int> PowerLocationMoved;
        public event Action<int, int> ExitFacePicked;
        public event Action EnterPressed;

        private bool _exitFacePickMode;
        /// <summary>인출면 지정 모드 — 클릭 좌표만 올리고 가열면 선택 상태는 건드리지 않는다.</summary>
        public bool ExitFacePickMode
        {
            get => _exitFacePickMode;
            set { if (_exitFacePickMode == value) return; _exitFacePickMode = value; }
        }

        public bool RectangleSelectionMode { get; set; }
        public bool ExclusionSketchMode { get; set; }

        public bool IsReady => _ready;

        // Only the latest cursor is projected, at most once per 100 ms. Duplicate
        // pixels do not rerun OCCT; all navigation/lifecycle exits invalidate it.
        private void QueuePowerLocationPreview(int x, int y)
        {
            var point = (X: x, Y: y);
            if (_pendingPowerPreview == point || (_pendingPowerPreview == null && _lastPowerPreview == point)) return;
            _pendingPowerPreview = point;
            if (_powerPreviewTimer == null)
            {
                _powerPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(100) };
                _powerPreviewTimer.Tick += FlushPowerLocationPreview;
            }
            if (!_powerPreviewTimer.IsEnabled) _powerPreviewTimer.Start();
        }

        private void FlushPowerLocationPreview(object sender, EventArgs e)
        {
            _powerPreviewTimer?.Stop();
            var point = _pendingPowerPreview;
            _pendingPowerPreview = null;
            if (!_ready || !PowerLocationMode || _dragging || _panDragging || point == null || point == _lastPowerPreview) return;
            _lastPowerPreview = point;
            PowerLocationMoved?.Invoke(point.Value.X, point.Value.Y);
        }

        public void CancelPowerLeadPreview()
        {
            _powerPreviewTimer?.Stop();
            _pendingPowerPreview = _lastPowerPreview = null;
            Bridge.ClearPowerLeadPreview();
        }

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
            CancelPowerLeadPreview();
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
            CancelPowerLeadPreview();
            if (_ready) Bridge.ResizeView((int)ActualWidth, (int)ActualHeight);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_KEYDOWN:
                    if ((int)wParam == 0x0D) { EnterPressed?.Invoke(); handled = true; }
                    break;

                case WM_LBUTTONDOWN:
                    CancelPowerLeadPreview();
                    SetFocus(hwnd);   // 키보드 포커스가 없으면 Enter가 뷰포트에 전달되지 않는다
                    SetCapture(hwnd);
                    _dragging = true;
                    _dragStarted = false;
                    _downX = LoWord(lParam);
                    _downY = HiWord(lParam);
                    _lastExclusionPreviewX = _downX;
                    _lastExclusionPreviewY = _downY;
                    _lastSelectionPreviewX = _downX;
                    _lastSelectionPreviewY = _downY;
                    bool shift = (GetKeyState(0x10) & 0x8000) != 0;
                    _activeDragMode = PowerLocationMode ? 0 : (ExclusionSketchMode ? 2 : (shift || RectangleSelectionMode ? 1 : 0));
                    handled = true;
                    break;

                case WM_RBUTTONDOWN:
                    CancelPowerLeadPreview();
                    SetCapture(hwnd);
                    _panDragging = true;
                    _downX = LoWord(lParam);
                    _downY = HiWord(lParam);
                    if (_ready) Bridge.PanStart(_downX, _downY);
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                    if (_panDragging)
                    {
                        if (_ready) Bridge.PanEnd();
                        _panDragging = false;
                        ReleaseCapture();
                        handled = true;
                    }
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
                    if (_panDragging)
                    {
                        if (_ready) Bridge.PanMove(x, y);
                        handled = true;
                        break;
                    }
                    if (_dragging)
                    {
                        if (!_dragStarted)
                        {
                            int dx = x - _downX, dy = y - _downY;
                            if (dx * dx + dy * dy >= ClickDragThreshold * ClickDragThreshold)
                            {
                                _dragStarted = true;
                                if (_activeDragMode == 0)
                                {
                                    Bridge.ClearHover();          // 회전 중 잔상 방지
                                    CancelPowerLeadPreview();
                                    Bridge.RotateStart(_downX, _downY);
                                }
                            }
                        }
                        if (_dragStarted)
                        {
                            if (_activeDragMode == 0) Bridge.RotateMove(x, y);
                            else if (_activeDragMode == 1 &&
                                     (Math.Abs(x - _lastSelectionPreviewX) >= 4 || Math.Abs(y - _lastSelectionPreviewY) >= 4))
                            {
                                _lastSelectionPreviewX = x;
                                _lastSelectionPreviewY = y;
                                Bridge.SetSelectionRectangle(_downX, _downY, x, y);
                            }
                            else if (_activeDragMode == 2 &&
                                     (Math.Abs(x - _lastExclusionPreviewX) >= 4 || Math.Abs(y - _lastExclusionPreviewY) >= 4))
                            {
                                _lastExclusionPreviewX = x;
                                _lastExclusionPreviewY = y;
                                ExclusionSketchMoved?.Invoke(_downX, _downY, x, y);
                            }
                            handled = true;
                        }
                    }
                    else if (_ready && PowerLocationMode)
                    {
                        // Do not highlight/select faces or connectors while choosing destinations.
                        QueuePowerLocationPreview(x, y);
                    }
                    else if (_ready && ConnectorEditMode)
                    {
                        ConnectorHovered?.Invoke(Bridge.PickConnector(x, y));
                    }
                    else if (_ready && CircuitHoverMode)
                    {
                        RoutePieceHovered?.Invoke(Bridge.PickRoutePiece(x, y));
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
                    CancelPowerLeadPreview();
                    _trackingMouse = false;
                    Bridge.ClearHover();
                    ConnectorHovered?.Invoke(-1);
                    RoutePieceHovered?.Invoke(-1);
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
                            int x = LoWord(lParam), y = HiWord(lParam);
                            if (_activeDragMode == 0) Bridge.RotateEnd();
                            else if (_activeDragMode == 1)
                            {
                                Bridge.ClearSelectionRectangle();
                                bool toggle = (GetKeyState(0x11) & 0x8000) != 0;
                                int[] ids = Bridge.SelectFacesInRectangle(_downX, _downY, x, y, toggle);
                                FacesRectanglePicked?.Invoke(ids, toggle);
                            }
                            else if (_activeDragMode == 2)
                                ExclusionSketchCompleted?.Invoke(_downX, _downY, x, y);
                        }
                        else if (_activeDragMode == 0 && ExitFacePickMode)
                        {
                            ExitFacePicked?.Invoke(LoWord(lParam), HiWord(lParam));
                        }
                        else if (_activeDragMode == 0 && PowerLocationMode)
                        {
                            CancelPowerLeadPreview();
                            PowerLocationPicked?.Invoke(LoWord(lParam), HiWord(lParam));
                        }
                        else if (_activeDragMode == 0 && ConnectorEditMode)
                        {
                            ConnectorPicked?.Invoke(Bridge.PickConnector(LoWord(lParam), HiWord(lParam)));
                        }
                        else if (_activeDragMode == 0)
                        {
                            // 이동 없는 클릭 = 면 선택 (W5 — 하이라이트 유지)
                            bool toggle = (GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
                            int idx = Bridge.SelectFace(LoWord(lParam), HiWord(lParam), toggle);
                            FacePicked?.Invoke(idx, toggle);
                        }
                        ReleaseCapture();
                        handled = true;
                    }
                    break;

                case WM_CAPTURECHANGED:
                    CancelPowerLeadPreview();
                    _dragging = false;
                    if (_panDragging && _ready) Bridge.PanEnd();
                    _panDragging = false;
                    if (_activeDragMode == 1) Bridge.ClearSelectionRectangle();
                    if (_activeDragMode == 2 && _dragStarted) ExclusionSketchCancelled?.Invoke();
                    _dragStarted = false;
                    break;

                case WM_MOUSEWHEEL:
                {
                    int delta = (short)(((long)wParam >> 16) & 0xFFFF);
                    var point = new POINT
                    {
                        X = (short)((long)lParam & 0xFFFF),
                        Y = (short)(((long)lParam >> 16) & 0xFFFF)
                    };
                    ScreenToClient(hwnd, ref point);
                    CancelPowerLeadPreview();
                    Bridge.ZoomViewAtPoint(point.X, point.Y, WheelZoomAmount(delta));
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

        private static int WheelZoomAmount(int delta)
        {
            int steps = delta / 120;
            if (steps == 0) steps = delta > 0 ? 1 : -1;
            return -steps * 12; // Invert only wheel input, not toolbar zoom commands.
        }

        private static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
        private static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
    }
}
