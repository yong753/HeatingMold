using System;
using System.Windows;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private static readonly bool WorkingRouteMigrationComplete = false;
    private readonly WorkingSideSession _workingSide = new();
    private Func<long, long, bool> _prepareWorkingFront = Bridge.ReturnToWorkingFront;
    private Action _beforeWorkingSideCommit = () => { };
    private Action _afterWorkingSideCommit = () => { };

    private void ResetWorkingSide()
    {
        if (_workingSide.Preview != null) Bridge.ReleaseWorkingContext(_workingSide.Preview.Id.Value);
        if (_workingSide.Confirmed != null) Bridge.ReleaseWorkingContext(_workingSide.Confirmed.Id.Value);
        _workingSide.Invalidate();
        WorkingSideText.Text = "작업 방향 · 모델을 불러오세요";
    }

    private bool WorkingGenerationReady()
    {
        var side = _workingSide.Confirmed;
        if (side != null && side.Revision.Value == Bridge.GetModelRevision() && side.Id.Value > 0) return true;
        StatusText.Text = "유효한 작업 방향이 없습니다";
        return false;
    }

    private bool ApplyWorkingSide(Vec3 n)
    {
        var old = _workingSide.Confirmed;
        if (old != null && old.Revision.Value == Bridge.GetModelRevision() && old.Frame.Normal == n) return true;

        var presentation = Bridge.CaptureWorkingPresentation();
        if (presentation == null) { StatusText.Text = "작업 방향 상태를 저장하지 못했습니다"; return false; }
        WorkingContext context = null;
        bool committed = false;
        try
        {
            var x = Math.Abs(n.X) < .9 ? new Vec3(1,0,0) : new Vec3(0,1,0);
            context = Bridge.CreateWorkingContext(n.X,n.Y,n.Z,x.X,x.Y,x.Z);
            if (context == null) throw new InvalidOperationException("전체 모델 작업 방향 구성 실패");
            var f = context.Frame;
            var frame = new ProjectionFrame(new Vec3(f[0],f[1],f[2]),new Vec3(f[9],f[10],f[11]),new Vec3(f[3],f[4],f[5]));
            var next = new WorkingSideDefinition(new(context.Id),new(context.Revision),frame);
            Bridge.ShowWorkingBoundary(null);
            if (!_prepareWorkingFront(context.Id,context.Revision)) throw new InvalidOperationException("작업 정면 뷰를 준비하지 못했습니다");
            _beforeWorkingSideCommit();

            _workingSide.SetPreview(next);
            _workingSide.Confirm(new(context.Revision));
            committed = true;
            try
            {
            _afterWorkingSideCommit();
            if (old != null) Bridge.ReleaseWorkingContext(old.Id.Value);

            ResetCircuitEditState(true);
            _pattern = null; _multiFacePatternDisplayed = false;
            _selectedFaceIds.Clear(); SelectedFacesList.Items.Clear();
            _selectedRegion.Clear(); _exclusions.Clear(); _multiFaceFrame = frame; _workingAreaConfirmed = false;
            _confirmedFaceIds.Clear(); _selectionDirty = false;
            Bridge.HideFaceHighlights(); Bridge.ClearOverlays(); Bridge.ClearExclusionOverlays();
            DrawRegionCanvas();
            WorkingSideText.Text = $"작업 방향 N={n} · 모델 r{context.Revision} · 깊이 0";
            StatusText.Text = "작업 방향 적용 완료 · 패턴 생성은 깊이 0만 지원 · 전원 연결 설정은 보류";
            return true;
            }
            catch (Exception ex)
            {
                // Publishing is the commit point. Cleanup must never make the caller
                // treat the committed side as a failed candidate.
                StatusText.Text = "작업 방향은 적용되었으나 후처리에 실패했습니다: " + ex.Message;
                return true;
            }
        }
        catch (Exception ex)
        {
            if (committed)
            {
                // The old context may already be released. Never restore its savepoint or
                // release the now-published candidate; leave the new side active and report
                // the post-commit cleanup failure explicitly.
                StatusText.Text = "작업 방향은 적용되었으나 후처리에 실패했습니다: " + ex.Message;
                return false;
            }
            bool restored = Bridge.RestoreWorkingPresentation(presentation.Id);
            if (context != null) Bridge.ReleaseWorkingContext(context.Id);
            if (!restored) ResetWorkingSide();
            StatusText.Text = "작업 방향 적용 실패: " + ex.Message;
            return false;
        }
        finally
        {
            try { Bridge.ReleaseWorkingPresentation(presentation.Id); } catch { }
        }
    }

    private void WorkingFront_Click(object sender, RoutedEventArgs e)
    {
        var side = _workingSide.Confirmed;
        if (side == null || !Bridge.ReturnToWorkingFront(side.Id.Value,side.Revision.Value))
            StatusText.Text = "작업 방향 컨텍스트가 만료되었습니다";
    }
}
