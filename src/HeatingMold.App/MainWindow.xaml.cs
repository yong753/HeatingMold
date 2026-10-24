using System;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.UseCases;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.ViewModels;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow : Window
    {
        private readonly ExportOrthographicViews _exportOrthographicViews;
        private readonly ExportDesign _exportDesign;
        private readonly OpenModel _openModel;
        private readonly CreateHeatingRegion _createHeatingRegion;
        private readonly UpdateRegion _updateRegion;
        private readonly AssessHeatingPathRegeneration _assessHeatingPathRegeneration;
        private readonly GenerateHeatingPath _generateHeatingPath;
        private readonly Func<string, bool> _confirmHeatingPathRegeneration;
        private readonly MoveControlPoint _moveControlPoint;
        private readonly EditTurnSegmentCurve _editTurnSegmentCurve;
        private readonly ChangeCircuitTopology _changeCircuitTopology;
        private readonly UpdateConnectionPath _updateConnectionPath;
        private MainWindowViewModel _mainWindowViewModel;
        private readonly OpenProject _openProject;
        private readonly SaveProject _saveProject;
        private readonly IModelSourceStore _modelSourceStore;
        private readonly OcctGeometryWorkspace _geometryWorkspace;
        private readonly ProjectSession _projectSession;
        private readonly ApplicationEventAdapter _applicationEvents;
        private readonly FaceReferenceIndex _faceReferenceIndex = new();
        /// <summary>화면에서 사용하는 안정 Face 참조 기반 영역 선택 상태를 제공합니다.</summary>
        private RegionSelectionViewModel RegionSelection => _mainWindowViewModel.RegionSelection;
        /// <summary>작업 좌표계와 축 선택 화면 상태를 제공합니다.</summary>
        private WorkingCoordinateSystemViewModel WorkingCoordinates => _mainWindowViewModel.WorkingCoordinates;
        /// <summary>세션 변경 화면 상태와 복구 진단을 제공합니다.</summary>
        private MainWindowSessionPresentationViewModel SessionPresentation => _mainWindowViewModel.SessionPresentation;
        private void ResetCircuitEditState(bool clearNative, bool preserveCommittedGeometry = false)
        {
            CancelActiveControlPointEdit(showStatus: false);
            CancelTurnSegmentCurvePreview(showStatus: false);
            ResetCircuitTopologyPreview(restoreCommittedPresentation: false);
            Viewport.CircuitSplitMode = false;
            CircuitTurnSegmentsList.IsEnabled = false;
            CircuitTopologyEndpointCombo.IsEnabled = false;
            if (CircuitSplitModeBox != null) CircuitSplitModeBox.IsChecked = false;
            if (clearNative && !preserveCommittedGeometry)
                Bridge.ClearPathSegments();
        }

        public MainWindow()
            : this(AppCompositionRoot.CreateMainWindowServices())
        {
        }

        internal MainWindow(ExportOrthographicViews exportOrthographicViews)
            : this(AppCompositionRoot.CreateMainWindowServices(exportOrthographicViews))
        {
        }

        internal MainWindow(AppCompositionRoot.MainWindowServices services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _exportOrthographicViews = services.ExportOrthographicViews;
            _exportDesign = services.ExportDesign;
            _openModel = services.OpenModel;
            _createHeatingRegion = services.CreateHeatingRegion;
            _updateRegion = services.UpdateRegion;
            _assessHeatingPathRegeneration = services.AssessHeatingPathRegeneration;
            _generateHeatingPath = services.GenerateHeatingPath;
            _moveControlPoint = services.MoveControlPoint;
            _editTurnSegmentCurve = services.EditTurnSegmentCurve;
            _changeCircuitTopology = services.ChangeCircuitTopology;
            _updateConnectionPath = services.UpdateConnectionPath;
            _openProject = services.OpenProject;
            _saveProject = services.SaveProject;
            _modelSourceStore = services.ModelSourceStore;
            _geometryWorkspace = services.GeometryWorkspace;
            _projectSession = services.ProjectSession;
            _applicationEvents = services.Events;
            var wireSpecificationViewModel = new WireSpecificationInputsViewModel();
            var electricalSpecificationViewModel = new ElectricalSpecificationViewModel(
                _projectSession,
                services.UpdateElectricalSpecification,
                wireSpecificationViewModel);
            _mainWindowViewModel = new MainWindowViewModel(
                wireSpecificationViewModel,
                new HeatingPathPlacementInputViewModel(wireSpecificationViewModel),
                electricalSpecificationViewModel);
            InitializeComponent();
            DataContext = _mainWindowViewModel;
            electricalSpecificationViewModel.StateChanged += OnElectricalSpecificationStateChanged;
            _confirmHeatingPathRegeneration = services.ConfirmHeatingPathRegeneration ?? (message =>
                MessageBox.Show(
                    this,
                    message,
                    "경로 재생성 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes);
            InitializeConnectionPathEditor();
            CircuitTurnSegmentsList.IsEnabled = false;
            _applicationEvents.Published += OnApplicationEvent;
            Viewport.FacePicked += OnFacePicked;
            Viewport.FaceHovered += OnFaceHovered;
            Viewport.FacesRectanglePicked += OnFacesRectanglePicked;
            Viewport.ExclusionSketchMoved += OnExclusionSketchMoved;
            Viewport.ExclusionSketchCompleted += OnExclusionSketchCompleted;
            Viewport.ExclusionSketchCancelled += () => Bridge.SetExclusionPreview(null);
            Viewport.TurnSegmentPicked += OnTurnSegmentPicked;
            Viewport.SurfaceAnchorPicked += OnSurfaceAnchorPicked;
            Viewport.ControlPointEditPreviewMoved += OnControlPointEditPreviewMoved;
            Viewport.ControlPointEditCompleted += OnControlPointEditCompleted;
            Viewport.ControlPointEditCancelled += () => CancelActiveControlPointEdit(showStatus: false);
            Viewport.TurnSegmentHovered += OnTurnSegmentHovered;
            Viewport.HeatingPathSegmentHovered += OnHeatingPathSegmentHovered;
            PreviewKeyDown += OnMainWindowPreviewKeyDown;
            Deactivated += OnMainWindowDeactivated;
            Closed += OnMainWindowClosed;
            Loaded += OnLoaded;
        }

        /// <summary>확정 변경 후 화면 복구 알림을 처리합니다.</summary>
        private void OnApplicationEvent(ApplicationEvent applicationEvent)
        {
            if (applicationEvent.Kind == ApplicationEventKind.SessionChanged)
            {
                CancelActiveControlPointEdit(showStatus: false);
                CancelTurnSegmentCurvePreview(showStatus: false);
                if (!SessionPresentation.IsRefreshSuppressed)
                    SynchronizeCommittedProject(applicationEvent);
                return;
            }

            if (applicationEvent.Kind != ApplicationEventKind.PresentationRepairNeeded)
                return;

            try
            {
                bool repaired = Bridge.RebuildModelPresentation();
                SessionPresentation.RepairMessage = repaired
                    ? "화면 표시를 다시 구성했습니다."
                    : "모델은 교체됐지만 화면 표시 동기화에 실패했습니다.";
                if (repaired && _projectSession.Current.CurrentProject is { } project)
                {
                    string visibilityDiagnostic = ApplyCommittedTurnVisibility(project);
                    if (visibilityDiagnostic is not null)
                        SessionPresentation.RepairMessage = string.Concat(
                            SessionPresentation.RepairMessage,
                            " · ",
                            visibilityDiagnostic);
                }
            }
            catch (Exception exception)
            {
                SessionPresentation.RepairMessage = string.Concat(
                    "모델은 교체됐지만 화면 표시 복구에 실패했습니다: ",
                    exception.Message);
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            GenerateSessionHeatingPath();
        }

        private void StandardView_Click(object sender, RoutedEventArgs e)
        {
            Viewport.CancelConnectionPathPreview();
            string view = (sender as Button)?.Tag as string;
            int axis, sign, upAxis;
            switch (view)
            {
                case "Front": axis = 2; sign = -1; upAxis = 1; break;
                case "Back": axis = 2; sign = 1; upAxis = 1; break;
                case "Top": axis = 1; sign = -1; upAxis = 2; break;
                case "Bottom": axis = 1; sign = 1; upAxis = 2; break;
                case "Left": axis = 0; sign = -1; upAxis = 1; break;
                case "Right": axis = 0; sign = 1; upAxis = 1; break;
                default: return;
            }

            double[] direction = Bridge.GetAutoAxis(axis);
            double[] up = Bridge.GetAutoAxis(upAxis);
            if (direction == null || direction.Length < 3 || up == null || up.Length < 3)
            {
                StatusText.Text = "표준 뷰 축 계산 실패";
                return;
            }

            Bridge.SetViewFrame(sign * direction[0], sign * direction[1], sign * direction[2],
                up[0], up[1], up[2]);
            StatusText.Text = "표준 뷰 — " + view;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "STEP (*.stp;*.step)|*.stp;*.step|모든 파일 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            CancelActiveControlPointEdit(showStatus: false);
            Viewport.CancelConnectionPathPreview();
            Bridge.FitView();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            CancelActiveControlPointEdit(showStatus: false);
            Viewport.CancelConnectionPathPreview();
            Bridge.ZoomView(1.25);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            CancelActiveControlPointEdit(showStatus: false);
            Viewport.CancelConnectionPathPreview();
            Bridge.ZoomView(0.8);
        }

    }
}
