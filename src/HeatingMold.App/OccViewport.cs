using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HeatingMold.App.Geometry;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;
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
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_SIZE = 0x0005;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_PAINT = 0x000F;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int VK_ESCAPE = 0x1B;

        private IntPtr _hwnd;
        private readonly ViewerWindowAttachment _viewerWindowAttachment = new();
        private bool _dragging;
        private bool _panDragging;
        private bool _dragStarted;
        private bool _trackingMouse;
        private int _downX, _downY;
        private int _lastSelectionPreviewX, _lastSelectionPreviewY;
        private int _lastExclusionPreviewX, _lastExclusionPreviewY;
        private int _lastHover = -2;      // W5 — 호버 이벤트 중복 억제
        private int _activeDragMode; // 0=orbit, 1=rectangle selection, 2=exclusion sketch
        private readonly ViewerInputModeState _inputModeState = new();
        private DispatcherTimer _controlPointEditPreviewTimer;
        private readonly LatestControlPointPreviewQueue _controlPointEditPreviewQueue = new();
        private bool _circuitHoverMode;

        /// <summary>현재 뷰포트에서 활성화된 설계 입력 모드입니다.</summary>
        internal ViewerInputMode InputMode => _inputModeState.Mode;

        /// <summary>입력 모드를 바꾸고 다른 설계 입력 동작을 함께 종료합니다.</summary>
        internal void SetInputMode(ViewerInputMode mode)
        {
            ViewerInputModeConfiguration nextConfiguration = ViewerInputModePolicy.GetConfiguration(mode);
            if (_inputModeState.Mode == mode)
                return;

            CancelConnectionPathPreview();
            if (_inputModeState.Configuration.EditsControlPoint)
                EndControlPointEditMode(notifyMainWindow: true, clearPreview: true);

            if (nextConfiguration.EditsControlPoint)
            {
                _controlPointEditPreviewQueue.Begin();
                _circuitHoverMode = false;
                HeatingPathSegmentHovered?.Invoke(-1);
            }

            _inputModeState.Activate(mode);
            if (!nextConfiguration.EditsCircuitTopology)
                TurnSegmentHovered?.Invoke(-1);
        }

        /// <summary>호환 속성의 변경을 단일 입력 모드 전이로 변환합니다.</summary>
        private void SetInputMode(ViewerInputMode mode, bool enabled)
        {
            if (enabled)
            {
                SetInputMode(mode);
            }
            else if (_inputModeState.Mode == mode)
            {
                SetInputMode(ViewerInputMode.Navigate);
            }
        }

        public bool CircuitSplitMode
        {
            get => _inputModeState.Configuration.EditsCircuitTopology;
            set => SetInputMode(ViewerInputMode.CircuitTopologyEditing, value);
        }
        public bool CircuitHoverMode
        {
            get => _circuitHoverMode;
            set
            {
                if (_circuitHoverMode == value) return;
                if (value) CancelControlPointEditMode(notifyMainWindow: true);
                _circuitHoverMode = value;
                if (!value) HeatingPathSegmentHovered?.Invoke(-1);
            }
        }

        /// <summary>선택한 절점을 드래그해 표면 위치를 지정합니다.</summary>
        public bool ControlPointEditMode
        {
            get => _inputModeState.Configuration.EditsControlPoint;
            set => SetInputMode(ViewerInputMode.ControlPointEditing, value);
        }

        /// <summary>표면 앵커 지정을 시작하거나 종료합니다.</summary>
        public bool SurfaceAnchorSelectionMode
        {
            get => _inputModeState.Configuration.PicksSurfaceAnchor;
            set => SetInputMode(ViewerInputMode.SurfaceAnchorPicking, value);
        }

        private const double ClickDragThresholdDip = 4;
        private const double PreviewMovementThresholdDip = 4;

        /// <summary>W3 — 면 클릭 선택 결과 (faceId, 없으면 -1)</summary>
        public event Action<int, bool> FacePicked;

        /// <summary>W5 — 호버 중인 면 변경 (faceId, 없으면 -1)</summary>
        public event Action<int> FaceHovered;
        public event Action<int[], bool> FacesRectanglePicked;
        public event Action<int, int, int, int> ExclusionSketchMoved;
        public event Action<int, int, int, int> ExclusionSketchCompleted;
        public event Action ExclusionSketchCancelled;
        public event Action<int> TurnSegmentHovered;
        public event Action<int> HeatingPathSegmentHovered;
        public event Action<int> TurnSegmentPicked;
        public event Action<int, int> SurfaceAnchorPicked;
        /// <summary>드래그 중 최신 커서 위치를 미리보기로 요청합니다.</summary>
        public event Action<int, int, long> ControlPointEditPreviewMoved;
        /// <summary>드롭한 위치에서 최종 제어점 편집을 요청합니다.</summary>
        public event Action<int, int> ControlPointEditCompleted;
        /// <summary>취소된 제어점 드래그의 화면 후보를 정리하도록 알립니다.</summary>
        public event Action ControlPointEditCancelled;

        /// <summary>Face 사각 선택 모드를 설정합니다.</summary>
        public bool RectangleSelectionMode
        {
            get => _inputModeState.Configuration.UsesFaceRectangle;
            set => SetInputMode(ViewerInputMode.FaceRectangleSelection, value);
        }

        /// <summary>제외 영역 스케치 모드를 설정합니다.</summary>
        public bool ExclusionSketchMode
        {
            get => _inputModeState.Configuration.UsesRegionSketch;
            set => SetInputMode(ViewerInputMode.RegionExclusionSketch, value);
        }

        public bool IsReady => _viewerWindowAttachment.IsAttachedTo(_hwnd);

        private bool _ready => IsReady;

        /// <summary>현재 OCCT 뷰어에 품질 표본 위치를 강조 표시합니다.</summary>
        public bool ShowQualityIssueLocation(Vec3 location)
        {
            if (!_ready)
                return false;

            return Bridge.ShowQualityIssueLocation(location.X, location.Y, location.Z);
        }

        /// <summary>이전에 표시한 품질 표본 강조를 제거합니다.</summary>
        public void ClearQualityIssueLocation() => Bridge.ClearQualityIssueLocation();

        /// <summary>뷰포트에서 연결 경로 임시 표시를 제거합니다.</summary>
        public void CancelConnectionPathPreview()
        {
            Bridge.ClearConnectionPathPreview();
        }

        /// <summary>창이 비활성화될 때 뷰포트의 포인터 입력과 임시 표시를 정리합니다.</summary>
        internal void CancelTransientPointerInput()
        {
            CancelControlPointEditMode(notifyMainWindow: true);
            CancelConnectionPathPreview();

            bool clearSelectionPreview = _dragging && _activeDragMode == 1;
            bool cancelExclusionPreview = _dragStarted && _activeDragMode == 2;
            bool endPan = _panDragging && _ready;
            bool ownsPointerCapture = _dragging || _panDragging;

            _dragging = false;
            _panDragging = false;
            _dragStarted = false;
            _activeDragMode = 0;

            if (clearSelectionPreview)
                Bridge.ClearSelectionRectangle();
            if (cancelExclusionPreview)
                ExclusionSketchCancelled?.Invoke();
            if (endPan)
                Bridge.PanEnd();
            if (ownsPointerCapture && _hwnd != IntPtr.Zero)
                ReleaseCapture();
        }

        /// <summary>현재 제어점 미리보기 세대와 예약된 커서 위치를 폐기합니다.</summary>
        public void CancelControlPointEditPreview()
        {
            _controlPointEditPreviewQueue.Cancel();
            _controlPointEditPreviewTimer?.Stop();
            Bridge.ClearControlPointEditPreview();
        }

        /// <summary>화면 응답이 현재 드래그 세대에 속하는지 확인합니다.</summary>
        public bool IsCurrentControlPointEditGeneration(long generation) =>
            ControlPointEditMode && _controlPointEditPreviewQueue.IsCurrent(generation);

        /// <summary>마지막 커서 위치만 약 30Hz로 보내 드래그 미리보기를 예약합니다.</summary>
        private void QueueControlPointEditPreview(int x, int y)
        {
            if (!_controlPointEditPreviewQueue.Queue(x, y))
                return;

            if (_controlPointEditPreviewTimer is null)
            {
                _controlPointEditPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(33)
                };
                _controlPointEditPreviewTimer.Tick += FlushControlPointEditPreview;
            }

            if (!_controlPointEditPreviewTimer.IsEnabled)
                _controlPointEditPreviewTimer.Start();
        }

        /// <summary>유효한 현재 drag만 MainWindow에 표면 교차 미리보기를 요청합니다.</summary>
        private void FlushControlPointEditPreview(object sender, EventArgs e)
        {
            _controlPointEditPreviewTimer?.Stop();
            if (!_ready || !ControlPointEditMode || !_dragging || !_dragStarted ||
                _activeDragMode != 3)
            {
                return;
            }

            long generation = _controlPointEditPreviewQueue.Generation;
            if (_controlPointEditPreviewQueue.TryTake(generation, out (int X, int Y) point))
                ControlPointEditPreviewMoved?.Invoke(point.X, point.Y, generation);
        }

        /// <summary>드래그를 끝내고 예약 응답과 임시 표시를 폐기합니다.</summary>
        private void EndControlPointEditMode(
            bool notifyMainWindow,
            bool clearPreview,
            bool releaseCapture = true)
        {
            bool wasActive = ControlPointEditMode;
            bool ownsCapture = _dragging && _activeDragMode == 3;
            if (wasActive)
            {
                _inputModeState.Activate(ViewerInputMode.Navigate);
            }
            _controlPointEditPreviewQueue.Cancel();
            _controlPointEditPreviewTimer?.Stop();

            if (ownsCapture)
            {
                _dragging = false;
                _dragStarted = false;
                _activeDragMode = 0;
                if (releaseCapture)
                    ReleaseCapture();
            }
            if (clearPreview)
                Bridge.ClearControlPointEditPreview();
            if (notifyMainWindow && wasActive)
                ControlPointEditCancelled?.Invoke();
        }

        /// <summary>다른 입력 모드가 시작되면 제어점 드래그와 해당 미리보기를 취소합니다.</summary>
        private void CancelControlPointEditMode(bool notifyMainWindow)
        {
            if (!ControlPointEditMode)
                return;

            EndControlPointEditMode(notifyMainWindow, clearPreview: true);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClassRegistered();
            (int width, int height) = GetViewportPixelSize();
            _hwnd = CreateWindowEx(0, WindowClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, width, height,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            try
            {
                EndControlPointEditMode(notifyMainWindow: true, clearPreview: true);
                CancelConnectionPathPreview();
                _dragging = false;
                _dragStarted = false;
            }
            finally
            {
                _viewerWindowAttachment.Detach(hwnd.Handle);
                if (_hwnd == hwnd.Handle)
                    _hwnd = IntPtr.Zero;
                DestroyWindow(hwnd.Handle);
            }
        }

        /// <summary>뷰어 초기화 (1회). Loaded 이후에 호출할 것.</summary>
        public bool InitViewer()
        {
            if (_hwnd == IntPtr.Zero)
                return false;

            (int width, int height) = GetViewportPixelSize();
            return _viewerWindowAttachment.TryAttach(
                _hwnd,
                windowHandle => Bridge.InitViewer(windowHandle, width, height));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            CancelControlPointEditMode(notifyMainWindow: true);
            CancelConnectionPathPreview();
            ResizeNativeViewerToViewport();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    CancelConnectionPathPreview();
                    SetCapture(hwnd);
                    _dragging = true;
                    _dragStarted = false;
                    _downX = LoWord(lParam);
                    _downY = HiWord(lParam);
                    _lastExclusionPreviewX = _downX;
                    _lastExclusionPreviewY = _downY;
                    _lastSelectionPreviewX = _downX;
                    _lastSelectionPreviewY = _downY;
                    _activeDragMode = _inputModeState.Configuration.DragMode;
                    handled = true;
                    break;

                case WM_RBUTTONDOWN:
                    CancelControlPointEditMode(notifyMainWindow: true);
                    CancelConnectionPathPreview();
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
                            DpiScale dpi = VisualTreeHelper.GetDpi(this);
                            int dragThreshold = GetDevicePixelThreshold(
                                ClickDragThresholdDip,
                                dpi.DpiScaleX,
                                dpi.DpiScaleY);
                            if (dx * dx + dy * dy >= dragThreshold * dragThreshold)
                            {
                                _dragStarted = true;
                                if (_activeDragMode == 0)
                                {
                                    Bridge.ClearHover();          // 회전 중 잔상 방지
                                    CancelConnectionPathPreview();
                                    Bridge.RotateStart(_downX, _downY);
                                }
                            }
                        }
                        if (_dragStarted)
                        {
                            if (_activeDragMode == 0) Bridge.RotateMove(x, y);
                            else if (_activeDragMode == 3) QueueControlPointEditPreview(x, y);
                            else if (_activeDragMode == 1 &&
                                     HasMovedAtLeastPreviewThreshold(
                                         x - _lastSelectionPreviewX,
                                         y - _lastSelectionPreviewY))
                            {
                                _lastSelectionPreviewX = x;
                                _lastSelectionPreviewY = y;
                                Bridge.SetSelectionRectangle(_downX, _downY, x, y);
                            }
                            else if (_activeDragMode == 2 &&
                                     HasMovedAtLeastPreviewThreshold(
                                         x - _lastExclusionPreviewX,
                                         y - _lastExclusionPreviewY))
                            {
                                _lastExclusionPreviewX = x;
                                _lastExclusionPreviewY = y;
                                ExclusionSketchMoved?.Invoke(_downX, _downY, x, y);
                            }
                            handled = true;
                        }
                    }
                    else if (_ready && SurfaceAnchorSelectionMode)
                    {
                        // 앵커 선택 중에는 면이나 경로 조각의 hover 질의를 실행하지 않습니다.
                    }
                    else if (_ready && CircuitSplitMode)
                    {
                        TurnSegmentHovered?.Invoke(Bridge.PickTurnSegment(x, y));
                    }
                    else if (_ready && CircuitHoverMode)
                    {
                        HeatingPathSegmentHovered?.Invoke(Bridge.PickHeatingPathSegment(x, y));
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
                    CancelConnectionPathPreview();
                    _trackingMouse = false;
                    Bridge.ClearHover();
                    TurnSegmentHovered?.Invoke(-1);
                    HeatingPathSegmentHovered?.Invoke(-1);
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
                        if (_activeDragMode == 3)
                        {
                            int x = LoWord(lParam), y = HiWord(lParam);
                            bool completedDrag = _dragStarted;
                            EndControlPointEditMode(notifyMainWindow: false, clearPreview: false);
                            if (completedDrag)
                                ControlPointEditCompleted?.Invoke(x, y);
                            else
                                ControlPointEditCancelled?.Invoke();
                            ReleaseCapture();
                            handled = true;
                            break;
                        }

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
                        else if (_activeDragMode == 0 && SurfaceAnchorSelectionMode)
                        {
                            SurfaceAnchorPicked?.Invoke(LoWord(lParam), HiWord(lParam));
                        }
                        else if (_activeDragMode == 0 && CircuitSplitMode)
                        {
                            TurnSegmentPicked?.Invoke(Bridge.PickTurnSegment(LoWord(lParam), HiWord(lParam)));
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
                {
                    CancelConnectionPathPreview();
                    bool controlPointEditWasActive = ControlPointEditMode && _activeDragMode == 3;
                    if (controlPointEditWasActive)
                        EndControlPointEditMode(
                            notifyMainWindow: true,
                            clearPreview: true,
                            releaseCapture: false);
                    _dragging = false;
                    if (_panDragging && _ready) Bridge.PanEnd();
                    _panDragging = false;
                    if (_activeDragMode == 1) Bridge.ClearSelectionRectangle();
                    if (_activeDragMode == 2 && _dragStarted) ExclusionSketchCancelled?.Invoke();
                    _dragStarted = false;
                    break;
                }

                case WM_KEYDOWN:
                    if (ControlPointEditMode && (int)wParam == VK_ESCAPE)
                    {
                        CancelControlPointEditMode(notifyMainWindow: true);
                        handled = true;
                    }
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
                    CancelControlPointEditMode(notifyMainWindow: true);
                    CancelConnectionPathPreview();
                    Bridge.ZoomViewAtPoint(point.X, point.Y, WheelZoomAmount(delta));
                    handled = true;
                    break;
                }

                case WM_SIZE:
                {
                    CancelControlPointEditMode(notifyMainWindow: true);
                    int width = LowWordUnsigned(lParam);
                    int height = HighWordUnsigned(lParam);
                    if (_ready && width > 0 && height > 0)
                        Bridge.ResizeView(width, height);
                    handled = true;
                    break;
                }

                case WM_DPICHANGED:
                case WM_DPICHANGED_AFTERPARENT:
                    CancelControlPointEditMode(notifyMainWindow: true);
                    ResizeNativeViewerToViewport();
                    handled = true;
                    break;

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

        /// <summary>WPF 논리 크기를 현재 시각 DPI에 맞는 자식 창 픽셀 크기로 바꿉니다.</summary>
        private (int Width, int Height) GetViewportPixelSize()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            return (
                DipToDevicePixels(ActualWidth, dpi.DpiScaleX),
                DipToDevicePixels(ActualHeight, dpi.DpiScaleY));
        }

        /// <summary>현재 viewport 픽셀 크기로 OCCT 뷰를 다시 맞춥니다.</summary>
        private void ResizeNativeViewerToViewport()
        {
            if (!_ready)
                return;

            (int width, int height) = GetViewportPixelSize();
            Bridge.ResizeView(width, height);
        }

        /// <summary>논리 단위 길이를 반올림 손실 없이 장치 픽셀 수로 변환합니다.</summary>
        private static int DipToDevicePixels(double dip, double dpiScale)
        {
            if (!double.IsFinite(dip) || dip < 0)
                throw new ArgumentOutOfRangeException(nameof(dip), "논리 길이는 유한한 0 이상이어야 합니다.");
            if (!double.IsFinite(dpiScale) || dpiScale <= 0)
                throw new ArgumentOutOfRangeException(nameof(dpiScale), "DPI 배율은 양의 유한값이어야 합니다.");

            double scaledPixels = Math.Ceiling(dip * dpiScale);
            if (scaledPixels > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dip), "변환한 픽셀 크기가 정수 범위를 넘습니다.");

            return Math.Max(1, (int)scaledPixels);
        }

        /// <summary>드래그 임계값을 가로·세로 DPI 중 큰 배율에 맞춰 픽셀로 바꿉니다.</summary>
        private static int GetDevicePixelThreshold(double thresholdDip, double dpiScaleX, double dpiScaleY) =>
            Math.Max(
                DipToDevicePixels(thresholdDip, dpiScaleX),
                DipToDevicePixels(thresholdDip, dpiScaleY));

        /// <summary>선택/제외 미리보기를 갱신할 만큼 커서가 움직였는지 확인합니다.</summary>
        private bool HasMovedAtLeastPreviewThreshold(int deltaX, int deltaY)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int thresholdX = DipToDevicePixels(PreviewMovementThresholdDip, dpi.DpiScaleX);
            int thresholdY = DipToDevicePixels(PreviewMovementThresholdDip, dpi.DpiScaleY);
            return Math.Abs(deltaX) >= thresholdX || Math.Abs(deltaY) >= thresholdY;
        }

        private static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
        private static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
        private static int LowWordUnsigned(IntPtr v) => (int)((long)v & 0xFFFF);
        private static int HighWordUnsigned(IntPtr v) => (int)(((long)v >> 16) & 0xFFFF);
    }
}
