using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private static readonly bool WorkingRouteMigrationComplete = false;
    private readonly WorkingSideSession _workingSide = new();
    private IReadOnlyList<WorkingBoundaryRing> _workingRings = Array.Empty<WorkingBoundaryRing>();
    private IReadOnlyList<WorkingBoundaryRing> _confirmedRings = Array.Empty<WorkingBoundaryRing>();
    private IReadOnlyList<WorkingBoundaryRing> _previewRings = Array.Empty<WorkingBoundaryRing>();
    private WorkingBoundarySample _workingBoundarySample;
    private sealed record WorkingPreviewBaseline(WorkingPresentation Presentation, bool PickChecked, bool PickMode);
    private WorkingPreviewBaseline _workingPreviewBaseline;
    private bool _restoringWorkingPickMode;
    // Injectable preparation boundary also permits deterministic post-display failure regressions.
    private Func<long, long, bool> _prepareWorkingFront = Bridge.ReturnToWorkingFront;

    private void ReleaseWorkingPreviewBaseline()
    {
        if (_workingPreviewBaseline != null) Bridge.ReleaseWorkingPresentation(_workingPreviewBaseline.Presentation.Id);
        _workingPreviewBaseline = null;
    }

    private void RestoreWorkingPickMode(bool isChecked, bool mode)
    {
        _restoringWorkingPickMode = true;
        try { WorkingPickBox.IsChecked = isChecked; Viewport.WorkingBoundaryPickMode = mode; }
        finally { _restoringWorkingPickMode = false; }
    }

    private void ResetWorkingSide()
    {
        ReleaseWorkingPreviewBaseline();
        if (_workingSide.Preview != null) Bridge.ReleaseWorkingContext(_workingSide.Preview.Id.Value);
        if (_workingSide.Confirmed != null) Bridge.ReleaseWorkingContext(_workingSide.Confirmed.Id.Value);
        _workingSide.Invalidate();
        _workingBoundarySample = null;
        _workingRings = _confirmedRings = _previewRings = Array.Empty<WorkingBoundaryRing>();
        WorkingPickBox.IsChecked = false;
        Bridge.ShowWorkingBoundary(null);
        Bridge.ShowWorkingDiagnostic(null, false);
        WorkingSideText.Text = "작업 방향 미확정 · 패턴 생성 보류";
        DrawWorkingBoundary();
    }

    private bool WorkingGenerationReady()
    {
        StatusText.Text = _workingSide.Confirmed == null ? "작업 방향을 먼저 미리보기/확정하세요" :
            "생성 보류: 기존 선택 Face 투영은 전체 모델 첫 충돌을 보장하지 않습니다. 패턴/전원 경로 마이그레이션 필요 (현재 깊이 0 기하 진단만 지원).";
        return false; // Deliberate checkpoint gate. Do not enable until the route itself is first-hit safe.
    }

    private void WorkingPreview_Click(object sender, RoutedEventArgs e)
    {
        int i = WorkingAxisBox.SelectedIndex;
        var n = (i / 2) switch { 0 => new Vec3(1,0,0), 1 => new Vec3(0,1,0), _ => new Vec3(0,0,1) };
        PreviewWorkingSide(n * (i % 2 == 0 ? 1 : -1));
    }

    private void PreviewWorkingSide(Vec3 n)
    {
        var presentation = Bridge.CaptureWorkingPresentation();
        if (presentation == null) { StatusText.Text = "작업 방향 표시 상태를 저장하지 못했습니다"; return; }
        var priorPreview = _workingSide.Preview;
        var priorRings = _workingRings;
        var priorPreviewRings = _previewRings;
        var priorSample = _workingBoundarySample;
        var priorText = WorkingSideText.Text;
        bool priorChecked = WorkingPickBox.IsChecked == true, priorMode = Viewport.WorkingBoundaryPickMode;
        WorkingContext context = null;
        bool committed = false;
        try
        {
            var x = Math.Abs(n.X) < .9 ? new Vec3(1,0,0) : new Vec3(0,1,0);
            context = Bridge.CreateWorkingContext(n.X,n.Y,n.Z,x.X,x.Y,x.Z);
            if (context == null) throw new InvalidOperationException("전체 모델 작업 방향/메시 구성 실패");
            var f = context.Frame;
            var frame = new ProjectionFrame(new Vec3(f[0],f[1],f[2]),new Vec3(f[9],f[10],f[11]),new Vec3(f[3],f[4],f[5]));
            var triangles = new List<IReadOnlyList<Vec2>>();
            for (int i=0;i+8<context.Triangles.Length;i+=9)
                triangles.Add(new[] { frame.Project(new Vec3(context.Triangles[i],context.Triangles[i+1],context.Triangles[i+2])),
                    frame.Project(new Vec3(context.Triangles[i+3],context.Triangles[i+4],context.Triangles[i+5])),
                    frame.Project(new Vec3(context.Triangles[i+6],context.Triangles[i+7],context.Triangles[i+8])) });
            var rings = WorkingBoundary.FromTriangles(triangles, context.MeshDeflection);
            if (rings.Count == 0) throw new InvalidOperationException("닫힌 투영 외곽이 없습니다");
            var next = new WorkingSideDefinition(new(context.Id),new(context.Revision),frame);
            // Prepare native presentation before publishing/releasing any preview state.
            DisplayWorkingBoundary(next,rings);
            Bridge.ShowWorkingDiagnostic(null,false); // Hide, do not discard the confirmed sample/savepoint.
            if (!_prepareWorkingFront(context.Id,context.Revision))
                throw new InvalidOperationException("작업 방향 뷰를 준비하지 못했습니다");
            DrawWorkingBoundary(rings);
            _workingSide.SetPreview(next);
            _workingRings = _previewRings = rings;
            RestoreWorkingPickMode(false,false); // Suspended while a preview is outstanding; restored on cancel.
            WorkingSideText.Text = $"미리보기 N={n} · 기존 확정 유지 · 확정 버튼 필요";
            StatusText.Text = $"전체 모델 투영: 외곽 {rings.Count(r=>!r.IsHole)}, 홀 {rings.Count(r=>r.IsHole)} · 메시 목표 편향 {context.MeshDeflection}mm (정확/보증 경계 아님)";
            if (_workingPreviewBaseline == null)
                _workingPreviewBaseline = new(presentation,priorChecked,priorMode);
            committed = true;
            if (priorPreview != null) Bridge.ReleaseWorkingContext(priorPreview.Id.Value);
        }
        catch (Exception ex)
        {
            if (priorPreview == null) _workingSide.CancelPreview(); else _workingSide.SetPreview(priorPreview);
            _workingRings = priorRings; _previewRings = priorPreviewRings;
            _workingBoundarySample = priorSample;
            WorkingSideText.Text = priorText;
            RestoreWorkingPickMode(priorChecked,priorMode);
            bool restored = Bridge.RestoreWorkingPresentation(presentation.Id);
            if (context != null) Bridge.ReleaseWorkingContext(context.Id);
            if (restored) DrawWorkingBoundary();
            else ResetWorkingSide(); // Cannot retain a usable-looking selection if native rollback is impossible.
            StatusText.Text = "작업 방향 미리보기 실패: " + ex.Message + (restored ? " · 이전 표시/카메라 유지" : " · 표시 복원 실패 (모델 상태 확인 필요)");
        }
        finally
        {
            if (!committed || _workingPreviewBaseline?.Presentation.Id != presentation.Id)
                Bridge.ReleaseWorkingPresentation(presentation.Id);
        }
    }

    private void WorkingFlip_Click(object sender, RoutedEventArgs e)
    {
        var side = _workingSide.Preview ?? _workingSide.Confirmed;
        if (side == null) { WorkingPreview_Click(sender,e); return; }
        PreviewWorkingSide(side.Frame.Normal * -1);
    }

    private void WorkingConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_workingSide.Preview == null) { StatusText.Text = "먼저 작업 방향을 미리보기하세요"; return; }
        var old = _workingSide.Confirmed;
        try { _workingSide.Confirm(new(Bridge.GetModelRevision())); }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
        _workingRings = _confirmedRings = _previewRings;
        _previewRings = Array.Empty<WorkingBoundaryRing>();
        ReleaseWorkingPreviewBaseline();
        _workingBoundarySample = null;
        RestoreWorkingPickMode(false,false);
        if (old != null) Bridge.ReleaseWorkingContext(old.Id.Value);
        ResetCircuitEditState(true);
        _pattern = null; _heatPath3D = _heatBand3D = null;
        _selectedRegion.Clear(); _exclusions.Clear(); _multiFaceFrame = _workingSide.Confirmed.Frame;
        Bridge.ClearOverlays(); Bridge.ClearExclusionOverlays();
        ShowWorkingBoundary(_workingSide.Confirmed);
        DrawRegionCanvas();
        WorkingSideText.Text = $"확정 N={_workingSide.Confirmed.Frame.Normal} · 모델 r{_workingSide.Confirmed.Revision.Value} · 깊이 0 진단";
        StatusText.Text = "작업 방향 확정 · 카메라 회전과 독립 · 전체 외곽 진단 선택 가능 · 경로 생성은 마이그레이션 대기";
    }

    private void WorkingFront_Click(object sender, RoutedEventArgs e)
    {
        if (_workingSide.Confirmed == null) { StatusText.Text="확정된 작업 방향이 없습니다"; return; }
        // Return to confirmed side, not the unconfirmed preview.
        _workingRings = _confirmedRings;
        ShowWorkingBoundary(_workingSide.Confirmed);
        if (!Bridge.ReturnToWorkingFront(_workingSide.Confirmed.Id.Value,_workingSide.Confirmed.Revision.Value))
            StatusText.Text="작업 방향 컨텍스트가 만료되었습니다. 다시 미리보기하세요";
    }
    private void WorkingCancel_Click(object sender, RoutedEventArgs e)
    {
        var baseline = _workingPreviewBaseline;
        if (baseline != null && !Bridge.RestoreWorkingPresentation(baseline.Presentation.Id))
        {
            ResetWorkingSide();
            StatusText.Text = "작업 방향 표시 상태가 만료되었습니다. 다시 미리보기하세요";
            return;
        }
        if (_workingSide.Preview != null) Bridge.ReleaseWorkingContext(_workingSide.Preview.Id.Value);
        _workingSide.CancelPreview();
        _previewRings = Array.Empty<WorkingBoundaryRing>();
        _workingRings = _confirmedRings;
        if (baseline != null)
        {
            RestoreWorkingPickMode(baseline.PickChecked,baseline.PickMode);
            ReleaseWorkingPreviewBaseline();
            DrawWorkingBoundary();
        }
        else if (_workingSide.Confirmed != null) ShowWorkingBoundary(_workingSide.Confirmed);
        else { Bridge.ShowWorkingBoundary(null); DrawWorkingBoundary(); }
        WorkingSideText.Text = _workingSide.Confirmed == null ? "작업 방향 미확정" :
            $"확정 N={_workingSide.Confirmed.Frame.Normal} · 미리보기 취소";
    }
    private void WorkingPick_Changed(object sender, RoutedEventArgs e)
    {
        if (Viewport == null || _restoringWorkingPickMode) return;
        if (WorkingPickBox.IsChecked != true) { Viewport.WorkingBoundaryPickMode = false; return; }
        if (!CanPickWorkingBoundary())
        {
            RestoreWorkingPickMode(false,false);
            StatusText.Text="경계 진단 전에 유효한 작업 방향을 확정하고 미리보기를 완료/취소하세요";
            return;
        }
        Viewport.WorkingBoundaryPickMode = true;
        if (Viewport.WorkingBoundaryPickMode)
        {
            Viewport.PowerLocationMode = false;
            FaceDragSelectModeBox.IsChecked = ExcludeModeBox.IsChecked = ConnectorEditModeBox.IsChecked = false;
            StatusText.Text="진단: 전체 모델 외곽만 선택 (홀/공유 Edge 제외), 약 10 DIP · 보이는 점만 허용";
        }
    }

    private bool CanPickWorkingBoundary()
    {
        var side = _workingSide.Confirmed;
        return side != null && _workingSide.Preview == null && side.Revision.Value == Bridge.GetModelRevision() &&
            Bridge.QueryWorkingSurface(side.Id.Value,side.Revision.Value,0,0).Status != WorkingQueryStatus.StaleContext;
    }

    private void ShowWorkingBoundary(WorkingSideDefinition side = null)
    {
        side ??= _workingSide.Preview ?? _workingSide.Confirmed;
        if (side == null) return;
        DisplayWorkingBoundary(side,_workingRings);
        var point = _workingBoundarySample?.ContextId == side.Id ? _workingBoundarySample.ReferencePoint : null;
        Bridge.ShowWorkingDiagnostic(point is Vec3 p ? new[]{p.X,p.Y,p.Z} : null,point != null);
        DrawWorkingBoundary();
    }

    private static void DisplayWorkingBoundary(WorkingSideDefinition side, IReadOnlyList<WorkingBoundaryRing> rings)
    {
        // Datum silhouettes are distinct from surface geometry; holes are colored separately in both views.
        Bridge.ShowWorkingBoundary(rings.Select(r=>r.Points.SelectMany(p=>
        { var w=side.Frame.Unproject(p); return new[]{w.X,w.Y,w.Z}; }).ToArray()).ToArray(),
            rings.Select(r=>r.IsHole).ToArray());
    }

    private void WorkingBoundaryCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawWorkingBoundary();
    private void DrawWorkingBoundary(IReadOnlyList<WorkingBoundaryRing> rings = null)
    {
        if (WorkingBoundaryCanvas == null) return;
        WorkingBoundaryCanvas.Children.Clear();
        rings ??= _workingRings;
        if (rings.Count == 0) return;
        var all=rings.SelectMany(r=>r.Points).ToArray();
        double minX=all.Min(p=>p.X),minY=all.Min(p=>p.Y), maxX=all.Max(p=>p.X),maxY=all.Max(p=>p.Y);
        double scale=Math.Min(Math.Max(1,WorkingBoundaryCanvas.ActualWidth-12)/Math.Max(1e-6,maxX-minX),
            Math.Max(1,WorkingBoundaryCanvas.ActualHeight-12)/Math.Max(1e-6,maxY-minY));
        foreach (var ring in rings)
        {
            var poly=new Polygon { Stroke=ring.IsHole ? Brushes.RoyalBlue : Brushes.DarkOrange, StrokeThickness=1.5 };
            foreach (var p in ring.Points) poly.Points.Add(new Point(6+(p.X-minX)*scale,6+(maxY-p.Y)*scale));
            WorkingBoundaryCanvas.Children.Add(poly);
        }
    }

    // Refine a local mesh silhouette candidate using full-model BRep occupancy. This is a numerical
    // bracket, not an exact curve or a guarantee that mesh topology matches tiny CAD features.
    private bool TryRefineBoundary(WorkingSideDefinition side, Vec2 candidate, Vec2 outward,
        out Vec2 uv, out Vec3 point, out double bracket)
    {
        uv=default; point=default; bracket=0;
        double lo=-.2,hi=.2;
        WorkingHit Query(double t) => Bridge.QueryWorkingSurface(side.Id.Value,side.Revision.Value,
            candidate.X+outward.X*t,candidate.Y+outward.Y*t);
        var inside=Query(lo); var outside=Query(hi);
        if (inside.Status!=WorkingQueryStatus.Hit || outside.Status!=WorkingQueryStatus.Miss) return false;
        for (int i=0;i<18;++i)
        {
            double mid=(lo+hi)/2; var hit=Query(mid);
            if (hit.Status==WorkingQueryStatus.Hit) { lo=mid; inside=hit; }
            else if (hit.Status==WorkingQueryStatus.Miss || hit.Status==WorkingQueryStatus.Tangent) hi=mid;
            else return false;
        }
        uv=new(candidate.X+outward.X*lo,candidate.Y+outward.Y*lo);
        point=new(inside.Point[0],inside.Point[1],inside.Point[2]); bracket=hi-lo;
        return true;
    }

    private void OnWorkingBoundaryPicked(int x,int y)
    {
        // A preview can be inspected visually, but accepted diagnostics always belong to confirmed context.
        var side=_workingSide.Confirmed;
        if (!CanPickWorkingBoundary())
        { StatusText.Text="경계 진단 전에 미리보기 방향을 확정하세요"; return; }
        _workingBoundarySample=null;
        Bridge.ShowWorkingDiagnostic(null,false);
        double aperture=10*VisualTreeHelper.GetDpi(Viewport).DpiScaleX;
        double best=aperture*aperture; Vec3? chosen=null; Vec2 chosenUv=default; double error=0;
        foreach (var ring in _confirmedRings.Where(r=>r.NestingDepth==0))
            for (int i=0;i<ring.Points.Count;++i)
            {
                var a=ring.Points[i]; var b=ring.Points[(i+1)%ring.Points.Count];
                double dx=b.X-a.X,dy=b.Y-a.Y,len=Math.Sqrt(dx*dx+dy*dy);
                if (len<1e-9) continue;
                var outward=new Vec2(dy/len,-dx/len);
                // Avoid ambiguous CAD corner tangencies when establishing a segment's screen span.
                double inset=Math.Min(.001,len*.01);
                a=new(a.X+dx/len*inset,a.Y+dy/len*inset);
                b=new(b.X-dx/len*inset,b.Y-dy/len*inset);
                dx=b.X-a.X; dy=b.Y-a.Y;
                // Coarse 3D endpoints establish screen parameter; repeated refinement handles perspective.
                if (!TryRefineBoundary(side,a,outward,out _,out var pa,out _) ||
                    !TryRefineBoundary(side,b,outward,out _,out var pb,out _)) continue;
                var sa=Bridge.WorkingPointToScreen(side.Id.Value,side.Revision.Value,pa.X,pa.Y,pa.Z);
                var sb=Bridge.WorkingPointToScreen(side.Id.Value,side.Revision.Value,pb.X,pb.Y,pb.Z);
                if (sa==null || sb==null) continue;
                double sx=sb[0]-sa[0],sy=sb[1]-sa[1],den=sx*sx+sy*sy;
                double t=den<1 ? .5 : Math.Clamp(((x-sa[0])*sx+(y-sa[1])*sy)/den,0,1);
                for (int step=0;step<4;++step)
                {
                    var c=new Vec2(a.X+dx*t,a.Y+dy*t);
                    if (!TryRefineBoundary(side,c,outward,out var uv,out var p,out var err)) break;
                    var s=Bridge.WorkingPointToScreen(side.Id.Value,side.Revision.Value,p.X,p.Y,p.Z);
                    if (s==null) break;
                    double dist=(s[0]-x)*(s[0]-x)+(s[1]-y)*(s[1]-y);
                    if (dist<=best && Bridge.IsWorkingPointVisible(side.Id.Value,side.Revision.Value,p.X,p.Y,p.Z,.001))
                    { best=dist; chosen=p; chosenUv=uv; error=err; }
                    double dt=.001;
                    var c2=new Vec2(a.X+dx*Math.Min(1,t+dt),a.Y+dy*Math.Min(1,t+dt));
                    if (!TryRefineBoundary(side,c2,outward,out _,out var p2,out _)) break;
                    var s2=Bridge.WorkingPointToScreen(side.Id.Value,side.Revision.Value,p2.X,p2.Y,p2.Z);
                    if (s2==null) break;
                    double vx=(s2[0]-s[0])/dt,vy=(s2[1]-s[1])/dt;
                    if (vx*vx+vy*vy<1) break;
                    t=Math.Clamp(t+((x-s[0])*vx+(y-s[1])*vy)/(vx*vx+vy*vy),0,1);
                }
            }
        if (chosen is not Vec3 hitPoint)
        { StatusText.Text="외곽 선택 실패: 화면 범위 밖/홀/가려짐/접선 또는 BRep 브래킷 실패 · 숨은 면 대체 없음"; return; }
        Bridge.ShowWorkingDiagnostic(new[]{hitPoint.X,hitPoint.Y,hitPoint.Z},true);
        var hitResult=Bridge.QueryWorkingSurface(side.Id.Value,side.Revision.Value,chosenUv.X,chosenUv.Y);
        _workingBoundarySample=new(side.Id,side.Revision,chosenUv,(WorkingSurfaceStatus)hitResult.Status,
            hitPoint,hitResult.FaceId,error);
        StatusText.Text=$"외곽 진단 UV=({chosenUv.X:F3},{chosenUv.Y:F3}) XYZ={hitPoint} Face #{hitResult.FaceId} · 브래킷 {error:G2}mm (메시 후보/근사, 정확 곡선 아님)";
    }
}
