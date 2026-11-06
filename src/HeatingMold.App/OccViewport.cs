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
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

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
        private const int WM_SIZE = 0x0005;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_PAINT = 0x000F;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_KEYDOWN = 0x0100;

        private static bool IsUserInputMessage(int msg)
        {
            switch (msg)
            {
                case WM_KEYDOWN:
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MOUSEMOVE:
                case WM_MOUSEWHEEL:
                case WM_MOUSELEAVE:
                    return true;
                default:
                    return false;
            }
        }

        private IntPtr _hwnd;
        private bool _ready;
        private int _syncedWidth = -1, _syncedHeight = -1;   // 마지막으로 OCCT에 반영한 클라이언트 크기(px)
        private bool _resizePending;
        private bool _dragging;
        private bool _panDragging;
        private bool _dragStarted;
        private bool _trackingMouse;
        private int _downX, _downY;
        private int _lastExclusionPreviewX, _lastExclusionPreviewY;
        private int _lastHover = -2;      // W5 — 호버 이벤트 중복 억제
        private int _activeDragMode; // 0=orbit, 1=rectangle selection, 2=exclusion sketch
        private DispatcherTimer _rectPreviewTimer;
        private bool _rectPreviewPending;
        private bool _rectPreviewSession;
        private int _rectPreviewX, _rectPreviewY;
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
        public event Action<int[]> FacesRectanglePicked;
        public event Action<int, int, int, int> ExclusionSketchMoved;
        public event Action<int, int, int, int> ExclusionSketchCompleted;
        public event Action ExclusionSketchCancelled;
        public event Action<int> ConnectorHovered;
        public event Action<int> RoutePieceHovered;
        public event Action<int> ConnectorPicked;
        public event Action<int, int> PowerLocationPicked;
        public event Action<int, int> PowerLocationMoved;
        public event Action<int, int> ExitFacePicked;
        /// <summary>인출면 선택 모드에서 커서가 움직일 때 (x, y). 뷰포트를 벗어나면 (-1, -1).</summary>
        public event Action<int, int> ExitFacePickMoved;
        public event Action EnterPressed;

        // ---- 꼭지점 편집 모드 ----
        /// <summary>호버 중인 꼭지점 표시 인덱스 (없으면 -1).</summary>
        public event Action<int> VertexHovered;
        public event Action<int, int, int> VertexDragStarted;
        public event Action<int, int> VertexDragMoved;
        public event Action<int, int> VertexDragCompleted;
        public event Action VertexDragCancelled;
        /// <summary>드래그 중이 아닐 때 Esc — 편집 모드 종료 요청.</summary>
        public event Action VertexEscape;

        private bool _exitFacePickMode;
        /// <summary>인출면 지정 모드 — 클릭 좌표만 올리고 가열면 선택 상태는 건드리지 않는다.</summary>
        public bool ExitFacePickMode
        {
            get => _exitFacePickMode;
            set
            {
                if (_exitFacePickMode == value) return;
                _exitFacePickMode = value;
                if (!value) return;
                // 선택 중에는 면 하이라이트를 쓰지 않으므로 진입 시 기존 호버를 지운다.
                Bridge.ClearHover();
                if (_lastHover != -1)
                {
                    _lastHover = -1;
                    FaceHovered?.Invoke(-1);
                }
            }
        }

        private bool _rectangleSelectionMode;
        public bool RectangleSelectionMode
        {
            get => _rectangleSelectionMode;
            set
            {
                if (_rectangleSelectionMode == value) return;
                if (!value) CancelFaceRectangleDrag();
                _rectangleSelectionMode = value;
            }
        }

        private bool _exclusionSketchMode;
        public bool ExclusionSketchMode
        {
            get => _exclusionSketchMode;
            set
            {
                if (_exclusionSketchMode == value) return;
                if (!value) CancelFaceRectangleDrag();
                _exclusionSketchMode = value;
            }
        }

        /// <summary>진행 중인 제외영역 사각형 드래그를 확정 없이 취소한다.
        /// 캡처 해제가 WM_CAPTURECHANGED 경로로 취소 통지와 미리보기 정리를 수행한다.</summary>
        public void CancelExclusionSketch()
        {
            if (_dragging && _activeDragMode == 2 && _dragStarted)
                ReleaseCapture();
        }

        /// <summary>
        /// 영역 탭에서만 Face 클릭/사각형 선택을 허용한다. 네이티브 선택은 이벤트보다 먼저
        /// 일어나므로 창 쪽에서 이벤트만 막아서는 부족하다.
        /// </summary>
        private bool _faceSelectionEnabled = true;
        public bool FaceSelectionEnabled
        {
            get => _faceSelectionEnabled;
            set
            {
                if (_faceSelectionEnabled == value) return;
                if (!value) CancelFaceRectangleDrag();
                _faceSelectionEnabled = value;
            }
        }

        /// <summary>인출면 선택처럼 화면과 판정 기준을 일치시켜야 하는 모드에서 드래그 회전을 막는다.
        /// 줌/팬은 작업 프레임을 바꾸지 않으므로 허용한다.</summary>
        public bool ViewLocked { get; set; }

        private bool _inputLocked;
        /// <summary>
        /// 시각 테스트가 실제 기능을 호출하는 동안 사용자의 원시 마우스/키 입력을 막는다.
        /// 그리기/크기/캡처 해제 같은 수명주기 메시지는 통과시킨다.
        /// </summary>
        public bool InputLocked
        {
            get => _inputLocked;
            set
            {
                if (_inputLocked == value) return;
                if (value)
                {
                    CancelVertexDrag();
                    CancelPowerLeadPreview();
                    CancelFaceRectangleDrag();
                    _dragging = false;
                    _panDragging = false;
                    _dragStarted = false;
                    try { ReleaseCapture(); } catch (Exception) { }
                }
                _inputLocked = value;
            }
        }

        private bool _vertexEditMode;
        private bool _vertexDragging;
        private int _lastVertexHover = -2;   // 같은 핸들 반복 이벤트 억제
        // 꼭지점 드래그 이동은 최신 위치만 주기적으로 전달한다(느린 매핑 중 이벤트 폭주 방지).
        private DispatcherTimer _vertexDragTimer;
        private (int X, int Y)? _pendingVertexDrag;
        /// <summary>꼭지점 편집 모드 — 회전/팬/줌/면 선택을 막고 핸들 드래그만 허용한다.</summary>
        public bool VertexEditMode
        {
            get => _vertexEditMode;
            set
            {
                if (_vertexEditMode == value) return;
                _vertexEditMode = value;
                _lastVertexHover = -2;
                if (value) return;
                StopVertexDragTimer();
                if (_vertexDragging)
                {
                    _vertexDragging = false;
                    VertexDragCancelled?.Invoke();
                }
                VertexHovered?.Invoke(-1);
            }
        }

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

        /// <summary>진행 중인 꼭지점 드래그를 취소하고 완료 이벤트를 올리지 않는다.</summary>
        private void CancelVertexDrag()
        {
            StopVertexDragTimer();
            if (!_vertexDragging) return;
            _vertexDragging = false;
            VertexDragCancelled?.Invoke();
        }

        private void StopVertexDragTimer()
        {
            _vertexDragTimer?.Stop();
            _pendingVertexDrag = null;
        }

        /// <summary>마지막 커서 위치를 백그라운드 우선순위 타이머로 한 번만 올린다.</summary>
        private void QueueVertexDragMoved(int x, int y)
        {
            _pendingVertexDrag = (X: x, Y: y);
            if (_vertexDragTimer == null)
            {
                _vertexDragTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(33) };
                _vertexDragTimer.Tick += FlushVertexDrag;
            }
            if (!_vertexDragTimer.IsEnabled) _vertexDragTimer.Start();
        }

        private void FlushVertexDrag(object sender, EventArgs e)
        {
            _vertexDragTimer?.Stop();
            var point = _pendingVertexDrag;
            _pendingVertexDrag = null;
            if (!_vertexDragging || point == null) return;
            VertexDragMoved?.Invoke(point.Value.X, point.Value.Y);
        }

        // ---- 사각형 선택 프리뷰 (최신 좌표만 33ms 간격으로 반영) ----
        private void QueueFaceRectanglePreview(int x, int y)
        {
            _rectPreviewX = x;
            _rectPreviewY = y;
            _rectPreviewPending = true;
            if (_rectPreviewTimer == null)
            {
                _rectPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(33) };
                _rectPreviewTimer.Tick += FlushFaceRectanglePreview;
            }
            if (!_rectPreviewTimer.IsEnabled) _rectPreviewTimer.Start();
        }

        private void FlushFaceRectanglePreview(object sender, EventArgs e)
        {
            if (!_rectPreviewPending || !_dragging || _activeDragMode != 1 || !_rectPreviewSession)
            {
                StopFaceRectanglePreview();
                return;
            }
            _rectPreviewPending = false;
            // 테두리와 면 프리뷰를 같은 tick에서 갱신하고, 화면 갱신은 native가 한 번만 결정한다.
            Bridge.SetSelectionRectangle(_downX, _downY, _rectPreviewX, _rectPreviewY);
            Bridge.PreviewFacesInRectangle(_downX, _downY, _rectPreviewX, _rectPreviewY);
        }

        private void StopFaceRectanglePreview()
        {
            _rectPreviewTimer?.Stop();
            _rectPreviewPending = false;
        }

        /// <summary>진행 중인 사각형 선택 드래그를 확정 없이 취소한다(모드/탭/크기 변경 공통).</summary>
        private void CancelFaceRectangleDrag()
        {
            if (!_dragging || _activeDragMode != 1) return;
            StopFaceRectanglePreview();
            if (_rectPreviewSession)
            {
                Bridge.ClearSelectionRectangle();      // 종료 Redraw 전에 테두리를 먼저 제거한다
                Bridge.CancelFaceRectanglePreview();   // 기준 선택 복원 + 화면 1회 갱신
                _rectPreviewSession = false;
            }
            _dragging = false;
            _dragStarted = false;
            ReleaseCapture();
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClassRegistered();
            _hwnd = CreateWindowEx(0, WindowClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _syncedWidth = _syncedHeight = -1;
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            CancelPowerLeadPreview();
            StopFaceRectanglePreview();
            if (_rectPreviewSession)
            {
                Bridge.CancelFaceRectanglePreview();
                _rectPreviewSession = false;
            }
            _ready = false;
            _dragging = false;
            _dragStarted = false;
            _vertexDragging = false;
            StopVertexDragTimer();
            _syncedWidth = _syncedHeight = -1;
            DestroyWindow(hwnd.Handle);
        }

        /// <summary>
        /// 자식 HWND의 실제 클라이언트 크기(px)를 OCCT에 반영한다.
        /// WPF의 ActualWidth/Height는 DIP이므로 크기 비교·전달에 쓰지 않고 GetClientRect를 정본으로 삼는다.
        /// 크기가 그대로면 아무것도 하지 않는다(중복 렌더 방지).
        /// </summary>
        private bool SyncViewSize()
        {
            if (!_ready || _hwnd == IntPtr.Zero) return false;
            if (!GetClientRect(_hwnd, out RECT rc)) return false;

            int width = Math.Max(1, rc.Right - rc.Left);
            int height = Math.Max(1, rc.Bottom - rc.Top);
            if (width == _syncedWidth && height == _syncedHeight) return false;

            _syncedWidth = width;
            _syncedHeight = height;
            Bridge.ResizeView(width, height);
            return true;
        }

        /// <summary>
        /// WPF 레이아웃 도중에는 자식 HWND가 아직 옛 크기일 수 있으므로,
        /// 레이아웃·렌더가 끝난 뒤 한 번 더 실제 크기를 확인한다.
        /// </summary>
        private void ScheduleViewSizeSync()
        {
            if (_resizePending) return;
            _resizePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _resizePending = false;
                SyncViewSize();
            }));
        }

        /// <summary>뷰어 초기화 (1회). Loaded 이후에 호출할 것.</summary>
        public bool InitViewer()
        {
            if (_ready) return true;
            if (_hwnd == IntPtr.Zero) return false;
            _ready = Bridge.InitViewer(_hwnd, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
            if (_ready) SyncViewSize();
            return _ready;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            CancelFaceRectangleDrag();
            CancelPowerLeadPreview();
            CancelVertexDrag();
            // 이 시점의 base 렌더는 자식 HWND 크기를 아직 갱신하지 않았을 수 있다.
            // 즉시 시도하고, 실제 HWND 크기 기준으로 한 번 더 맞춘다.
            SyncViewSize();
            ScheduleViewSizeSync();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            CancelFaceRectangleDrag();
            CancelVertexDrag();
            // DPI가 바뀌면 DIP 크기는 그대로여도 HWND 픽셀 크기가 달라진다.
            _syncedWidth = _syncedHeight = -1;
            ScheduleViewSizeSync();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_inputLocked && IsUserInputMessage(msg))
            {
                handled = true;
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_KEYDOWN:
                    if ((int)wParam == 0x1B)   // VK_ESCAPE
                    {
                        if (_vertexDragging)
                        {
                            _vertexDragging = false;
                            StopVertexDragTimer();
                            VertexDragCancelled?.Invoke();
                            ReleaseCapture();
                            handled = true;
                        }
                        else if (VertexEditMode)
                        {
                            VertexEscape?.Invoke();
                            handled = true;
                        }
                        else if (_dragging && _activeDragMode == 1 && _dragStarted)
                        {
                            // 사각형 드래그 취소 — 드래그 시작 시점의 선택으로 되돌린다.
                            StopFaceRectanglePreview();
                            Bridge.ClearSelectionRectangle();   // 종료 Redraw 전에 테두리 제거
                            if (_rectPreviewSession)
                            {
                                Bridge.CancelFaceRectanglePreview();
                                _rectPreviewSession = false;
                            }
                            else
                            {
                                Bridge.RedrawView();
                            }
                            _dragging = false;
                            _dragStarted = false;
                            ReleaseCapture();
                            handled = true;
                        }
                        else if (_dragging && _activeDragMode == 2 && _dragStarted)
                        {
                            ReleaseCapture();
                            handled = true;
                        }
                        break;
                    }
                    if ((int)wParam == 0x0D)
                    {
                        if (VertexEditMode) { handled = true; break; }   // 편집 중 Enter는 영역 확정으로 새지 않는다
                        if (_dragging && _activeDragMode == 1) { handled = true; break; }   // 사각형 드래그 중 확정 금지
                        EnterPressed?.Invoke();
                        handled = true;
                    }
                    break;

                case WM_LBUTTONDOWN:
                    CancelPowerLeadPreview();
                    SetFocus(hwnd);   // 키보드 포커스가 없으면 Enter가 뷰포트에 전달된다
                    if (VertexEditMode)
                    {
                        int hx = LoWord(lParam), hy = HiWord(lParam);
                        int handle = _ready ? Bridge.PickVertexHandle(hx, hy, 12.0) : -1;
                        if (handle < 0) { handled = true; break; }   // 빈 곳 클릭은 회전/면 선택으로 대체하지 않는다
                        SetCapture(hwnd);
                        _dragging = false;
                        _dragStarted = false;
                        _pendingVertexDrag = null;
                        _vertexDragging = true;
                        VertexDragStarted?.Invoke(handle, hx, hy);
                        handled = true;
                        break;
                    }
                    SetCapture(hwnd);
                    _dragging = true;
                    _dragStarted = false;
                    _downX = LoWord(lParam);
                    _downY = HiWord(lParam);
                    _lastExclusionPreviewX = _downX;
                    _lastExclusionPreviewY = _downY;
                    bool shift = (GetKeyState(0x10) & 0x8000) != 0;
                    // Face 선택이 꺼진 탭에서는 Shift+드래그가 사각형 선택으로 승격되지 않게 한다.
                    bool rectangleSelect = (shift || RectangleSelectionMode) && FaceSelectionEnabled;
                    _activeDragMode = PowerLocationMode ? 0 : (ExclusionSketchMode ? 2 : (rectangleSelect ? 1 : 0));
                    handled = true;
                    break;

                case WM_RBUTTONDOWN:
                    if (VertexEditMode || _vertexDragging) { handled = true; break; }
                    if (_dragging && _activeDragMode == 1) { handled = true; break; }   // 사각형 선택 중 팬 금지(캐시 무효화 방지)
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
                    if (_vertexDragging)
                    {
                        QueueVertexDragMoved(x, y);
                        handled = true;
                        break;
                    }
                    if (VertexEditMode)
                    {
                        if (_ready)
                        {
                            int hover = Bridge.PickVertexHandle(x, y, 10.0);
                            if (hover != _lastVertexHover)
                            {
                                _lastVertexHover = hover;
                                VertexHovered?.Invoke(hover);
                            }
                        }
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
                                if (_activeDragMode == 0 && !ViewLocked)
                                {
                                    Bridge.ClearHover();          // 회전 중 잔상 방지
                                    if (ExitFacePickMode) ExitFacePickMoved?.Invoke(-1, -1);
                                    CancelPowerLeadPreview();
                                    Bridge.RotateStart(_downX, _downY);
                                }
                                else if (_activeDragMode == 1)
                                {
                                    Bridge.ClearHover();          // 사각형 선택 중 호버색 잔상 방지
                                    if (!_rectPreviewSession)
                                    {
                                        Bridge.BeginFaceRectanglePreview();
                                        _rectPreviewSession = true;
                                    }
                                }
                            }
                        }
                        if (_dragStarted)
                        {
                            if (_activeDragMode == 0 && !ViewLocked) Bridge.RotateMove(x, y);
                            else if (_activeDragMode == 1)
                            {
                                // 4px 게이트 없이 최신 좌표만 저장한다. 실제 반영은 33ms tick에서
                                // 테두리+면 프리뷰를 한 번에 처리한다.
                                QueueFaceRectanglePreview(x, y);
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
                    else if (_ready && ExitFacePickMode)
                    {
                        // 인출면 선택은 명시적 모달 동작이므로 회로 호버 등 다른
                        // 모드보다 우선한다. 그렇지 않으면 패턴 생성 후
                        // CircuitHoverMode가 켜져 있을 때 후보 강조가 멈춘다.
                        // 인출면 후보 Edge 강조와 후보 면 하이라이트는
                        // ExitFacePickMoved 처리기(MainWindow.ExitFace)가 소유한다.
                        ExitFacePickMoved?.Invoke(x, y);
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
                    if (VertexEditMode)
                    {
                        _lastVertexHover = -2;
                        VertexHovered?.Invoke(-1);
                    }
                    if (ExitFacePickMode) ExitFacePickMoved?.Invoke(-1, -1);
                    if (_lastHover != -1)
                    {
                        _lastHover = -1;
                        FaceHovered?.Invoke(-1);
                    }
                    handled = true;
                    break;

                case WM_LBUTTONUP:
                    if (_vertexDragging)
                    {
                        int hx = LoWord(lParam), hy = HiWord(lParam);
                        // ReleaseCapture가 WM_CAPTURECHANGED를 유발하므로 종료 상태를 먼저 확정한다.
                        _vertexDragging = false;
                        StopVertexDragTimer();
                        VertexDragCompleted?.Invoke(hx, hy);
                        ReleaseCapture();
                        handled = true;
                        break;
                    }
                    if (_dragging)
                    {
                        _dragging = false;
                        if (_dragStarted)
                        {
                            int x = LoWord(lParam), y = HiWord(lParam);
                            if (_activeDragMode == 0 && !ViewLocked) Bridge.RotateEnd();
                            else if (_activeDragMode == 1)
                            {
                                StopFaceRectanglePreview();
                                _rectPreviewSession = false;
                                Bridge.ClearSelectionRectangle();   // 테두리 제거(렌더 없음)
                                int[] ids = Bridge.SelectFacesInRectangle(_downX, _downY, x, y);
                                if (ids != null) FacesRectanglePicked?.Invoke(ids);   // 실패 시 기존 선택 유지
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
                        else if (_activeDragMode == 0 && FaceSelectionEnabled)
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
                    CancelVertexDrag();
                    _dragging = false;
                    if (_panDragging && _ready) Bridge.PanEnd();
                    _panDragging = false;
                    if (_activeDragMode == 1)
                    {
                        StopFaceRectanglePreview();
                        if (_rectPreviewSession)
                        {
                            Bridge.ClearSelectionRectangle();      // 종료 Redraw 전에 테두리 제거
                            Bridge.CancelFaceRectanglePreview();
                            _rectPreviewSession = false;
                        }
                    }
                    if (_activeDragMode == 2 && _dragStarted) ExclusionSketchCancelled?.Invoke();
                    _dragStarted = false;
                    break;

                case WM_MOUSEWHEEL:
                {
                    if (VertexEditMode || _vertexDragging) { handled = true; break; }
                    if (_dragging && _activeDragMode == 1) { handled = true; break; }   // 사각형 선택 중 줌 금지(캐시 무효화 방지)
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

                case WM_SIZE:
                    // WM_SIZE는 HWND 크기가 실제로 바뀐 뒤에 오므로 여기서 GL 뷰포트까지 맞춘다.
                    // (WPF 레이아웃 순서에 의존하지 않는 유일한 확실한 시점)
                    SyncViewSize();
                    break;

                case WM_ERASEBKGND:
                    handled = true;
                    break;

                case WM_PAINT:
                {
                    // 배경은 그리지 않는다. 다만 뷰포트 크기가 어긋난 채로 새 영역이 노출되면
                    // 검은 영역이 남으므로, 크기가 다를 때만 다시 렌더한 뒤 무효 영역을 검증한다.
                    SyncViewSize();
                    ValidateRect(hwnd, IntPtr.Zero);
                    handled = true;
                    break;
                }
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
