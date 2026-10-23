using System;
using System.Windows;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private static readonly bool ConnectionPathMigrationComplete = false;
    private readonly WorkingCoordinateSystemSession _workingCoordinateSystem = new();
    private bool _changingWorkingAxis;
    private Func<long, long, bool> _prepareWorkingFront = Bridge.ReturnToWorkingFront;
    private Action _beforeWorkingCoordinateSystemCommit = () => { };
    private Action _afterWorkingCoordinateSystemCommit = () => { };

    private Vec3 SelectedWorkingNormal() => WorkingAxisBox.SelectedIndex switch
    {
        0 => new(1,0,0), 1 => new(-1,0,0), 2 => new(0,1,0),
        3 => new(0,-1,0), 5 => new(0,0,-1), _ => new(0,0,1)
    };

    private void WorkingAxis_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_changingWorkingAxis || !IsLoaded || Bridge.GetModelRevision() <= 0) return;
        int oldIndex = _workingCoordinateSystem.Confirmed == null ? 4 : _workingCoordinateSystem.Confirmed.Projection.Normal switch
        {
            { X: 1, Y: 0, Z: 0 } => 0, { X: -1, Y: 0, Z: 0 } => 1,
            { X: 0, Y: 1, Z: 0 } => 2, { X: 0, Y: -1, Z: 0 } => 3,
            { X: 0, Y: 0, Z: 1 } => 4, _ => 5
        };
        if (!ApplyWorkingCoordinateSystem(SelectedWorkingNormal()))
        {
            _changingWorkingAxis = true;
            WorkingAxisBox.SelectedIndex = oldIndex;
            _changingWorkingAxis = false;
        }
    }

    private void ResetWorkingCoordinateSystem()
    {
        if (_workingCoordinateSystem.Preview != null) Bridge.ReleaseWorkingSurfaceContext(_workingCoordinateSystem.Preview.Id.Value);
        if (_workingCoordinateSystem.Confirmed != null) Bridge.ReleaseWorkingSurfaceContext(_workingCoordinateSystem.Confirmed.Id.Value);
        _workingCoordinateSystem.Invalidate();
        WorkingCoordinateSystemText.Text = "작업 좌표계 · 모델을 불러오세요";
    }

    private bool WorkingGenerationReady()
    {
        var coordinateSystem = _workingCoordinateSystem.Confirmed;
        if (coordinateSystem != null && coordinateSystem.Revision.Value == Bridge.GetModelRevision() && coordinateSystem.Id.Value > 0) return true;
        StatusText.Text = "유효한 작업 좌표계가 없습니다";
        return false;
    }

    private bool ApplyWorkingCoordinateSystem(Vec3 n)
    {
        var oldCoordinateSystem = _workingCoordinateSystem.Confirmed;
        if (oldCoordinateSystem != null && oldCoordinateSystem.Revision.Value == Bridge.GetModelRevision() && oldCoordinateSystem.Projection.Normal == n) return true;

        var presentation = Bridge.CaptureWorkingCoordinateSystemPresentation();
        if (presentation == null) { StatusText.Text = "작업 좌표계 표시 상태를 저장하지 못했습니다"; return false; }
        WorkingSurfaceContext context = null;
        bool committed = false;
        try
        {
            var x = Math.Abs(n.X) < .9 ? new Vec3(1,0,0) : new Vec3(0,1,0);
            context = Bridge.CreateWorkingSurfaceContext(n.X,n.Y,n.Z,x.X,x.Y,x.Z);
            if (context == null) throw new InvalidOperationException("전체 모델 작업 좌표계 구성 실패");
            var f = context.Frame;
            var frame = new ProjectionFrame(new Vec3(f[0],f[1],f[2]),new Vec3(f[9],f[10],f[11]),new Vec3(f[3],f[4],f[5]));
            var next = new WorkingCoordinateSystem(new(context.Id),new(context.Revision),frame);
            Bridge.ShowProjectedModelBoundary(null);
            if (!_prepareWorkingFront(context.Id,context.Revision)) throw new InvalidOperationException("작업 정면 뷰를 준비하지 못했습니다");
            _beforeWorkingCoordinateSystemCommit();

            _workingCoordinateSystem.SetPreview(next);
            _workingCoordinateSystem.Confirm(new(context.Revision));
            committed = true;
            try
            {
            _afterWorkingCoordinateSystemCommit();
            if (oldCoordinateSystem != null) Bridge.ReleaseWorkingSurfaceContext(oldCoordinateSystem.Id.Value);

            ResetCircuitEditState(true);
            _pattern = null; _multiFacePatternDisplayed = false;
            _selectedFaceIds.Clear(); SelectedFacesList.Items.Clear(); FaceCombo.SelectedIndex = -1;
            _selectedRegion.Clear(); _exclusions.Clear(); _multiFaceProjectionFrame = frame;
            Bridge.HideFaceHighlights(); Bridge.ClearOverlays(); Bridge.ClearExclusionOverlays();
            DrawRegionCanvas();
            WorkingCoordinateSystemText.Text = $"작업 좌표계 N={n} · 모델 r{context.Revision} · 삽입 깊이 0";
            StatusText.Text = "작업 좌표계 적용 완료 · 삽입 깊이는 현재 0만 지원 · 연결부 위치 지정은 보류";
            return true;
            }
            catch (Exception ex)
            {
                // Publishing is the commit point. Cleanup must never make the caller
                // Treat the committed coordinate system as a failed candidate.
                StatusText.Text = "작업 좌표계는 적용되었으나 후처리에 실패했습니다: " + ex.Message;
                return true;
            }
        }
        catch (Exception ex)
        {
            if (committed)
            {
                // The old context may already be released. Never restore its savepoint or
                // release the now-published candidate; leave the new coordinate system active and report
                // the post-commit cleanup failure explicitly.
                StatusText.Text = "작업 좌표계는 적용되었으나 후처리에 실패했습니다: " + ex.Message;
                return false;
            }
            bool restored = Bridge.RestoreWorkingCoordinateSystemPresentation(presentation.Id);
            if (context != null) Bridge.ReleaseWorkingSurfaceContext(context.Id);
            if (!restored) ResetWorkingCoordinateSystem();
            StatusText.Text = "작업 좌표계 적용 실패: " + ex.Message;
            return false;
        }
        finally
        {
            try { Bridge.ReleaseWorkingCoordinateSystemPresentation(presentation.Id); } catch { }
        }
    }

    private void WorkingFront_Click(object sender, RoutedEventArgs e)
    {
        var coordinateSystem = _workingCoordinateSystem.Confirmed;
        if (coordinateSystem == null || !Bridge.ReturnToWorkingFront(coordinateSystem.Id.Value,coordinateSystem.Revision.Value))
            StatusText.Text = "작업 좌표계 컨텍스트가 만료되었습니다";
    }
}
