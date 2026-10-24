using System;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>Native 문서의 작업 정면 카메라를 준비하는 동작입니다.</summary>
    private Func<long, long, bool> _prepareWorkingFront = Bridge.ReturnToWorkingFront;

    /// <summary>작업 좌표계 확정 직전에 실행할 화면 정리 동작입니다.</summary>
    private Action _beforeWorkingCoordinateSystemCommit = () => { };

    /// <summary>작업 좌표계 확정 직후 실행할 화면 알림 동작입니다.</summary>
    private Action _afterWorkingCoordinateSystemCommit = () => { };

    /// <summary>사용자가 고른 축을 적용하고 실패하면 선택 UI를 이전 값으로 복구합니다.</summary>
    private void WorkingAxis_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WorkingCoordinates.IsSynchronizingAxisSelection || !IsLoaded || Bridge.GetModelRevision() <= 0)
            return;

        WorkingCoordinates.SelectedAxisIndex = WorkingAxisBox.SelectedIndex;
        int previousIndex = WorkingCoordinates.ConfirmedAxisIndex;
        if (HasCommittedDesignInputs())
        {
            WorkingCoordinates.SynchronizeAxisSelection(previousIndex);

            StatusText.Text = "저장된 설계가 있는 프로젝트의 작업축은 경로·영역 재검증과 함께 변경해야 합니다.";
            return;
        }

        if (ApplyWorkingCoordinateSystem(WorkingCoordinates.SelectedNormal))
            return;

        WorkingCoordinates.SynchronizeAxisSelection(previousIndex);
    }

    /// <summary>이전 모델의 작업 좌표계 context를 해제하고 축 선택을 무효화합니다.</summary>
    private void ResetWorkingCoordinateSystem()
    {
        WorkingCoordinateSystem preview = WorkingCoordinates.Session.Preview;
        WorkingCoordinateSystem confirmed = WorkingCoordinates.Session.Confirmed;

        if (preview is not null)
            Bridge.ReleaseWorkingSurfaceContext(preview.Id.Value);
        if (confirmed is not null)
            Bridge.ReleaseWorkingSurfaceContext(confirmed.Id.Value);

        WorkingCoordinates.Session.Invalidate();
        WorkingCoordinates.DisplayStatus = "작업 좌표계 · 모델을 불러오세요";
    }

    /// <summary>현재 모델 revision에 확정된 작업 좌표계가 있는지 확인합니다.</summary>
    private bool WorkingGenerationReady()
    {
        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        bool isCurrent = coordinateSystem is not null &&
            coordinateSystem.Revision.Value == Bridge.GetModelRevision() &&
            coordinateSystem.Id.Value > 0;

        if (!isCurrent)
            StatusText.Text = "유효한 작업 좌표계가 없습니다";

        return isCurrent;
    }

    /// <summary>새 Native 작업 context와 화면을 준비한 뒤 작업 좌표계를 확정합니다.</summary>
    private bool ApplyWorkingCoordinateSystem(Vec3 normal) =>
        ApplyWorkingCoordinateSystemCore(normal, preserveCommittedGeometry: false);

    /// <summary>저장 프로젝트의 확정 경로 표시를 유지하며 작업 좌표계를 화면 세션에 연결합니다.</summary>
    private bool ApplyWorkingCoordinateSystemPreservingCommittedGeometry(Vec3 normal) =>
        ApplyWorkingCoordinateSystemCore(normal, preserveCommittedGeometry: true);

    /// <summary>새 context를 준비하고 확정 뒤 기존 또는 저장된 Native 표시를 정리합니다.</summary>
    private bool ApplyWorkingCoordinateSystemCore(Vec3 normal, bool preserveCommittedGeometry)
    {
        WorkingCoordinateSystem previous = WorkingCoordinates.Session.Confirmed;
        if (IsAlreadyCurrent(previous, normal))
            return true;

        WorkingCoordinateSystemPresentation presentation =
            Bridge.CaptureWorkingCoordinateSystemPresentation();
        if (presentation is null)
        {
            StatusText.Text = "작업 좌표계 표시 상태를 저장하지 못했습니다";
            return false;
        }

        WorkingSurfaceContext context = null;
        bool isCommitted = false;
        try
        {
            context = CreateWorkingSurfaceContext(normal);
            WorkingCoordinateSystem next = CreateCoordinateSystem(context);

            Bridge.ShowProjectedModelBoundary(null);
            if (!_prepareWorkingFront(context.Id, context.Revision))
                throw new InvalidOperationException("작업 정면 뷰를 준비하지 못했습니다.");

            _beforeWorkingCoordinateSystemCommit();
            WorkingCoordinates.Session.SetPreview(next);
            WorkingCoordinates.Session.Confirm(new ModelRevision(context.Revision));
            isCommitted = true;

            Exception postCommitFailure = CompleteWorkingCoordinateSystemCommit(
                previous,
                next,
                preserveCommittedGeometry);
            if (postCommitFailure is not null)
                ReportPostCommitFailure(postCommitFailure);

            return true;
        }
        catch (Exception exception)
        {
            if (isCommitted)
            {
                ReportPostCommitFailure(exception);
                return true;
            }

            RestorePreviousWorkingCoordinateSystem(presentation, context);
            StatusText.Text = "작업 좌표계 적용 실패: " + exception.Message;
            return false;
        }
        finally
        {
            ReleasePresentationSavepoint(presentation.Id);
        }
    }

    /// <summary>확정된 좌표계와 같은 현재 모델 축인지 확인합니다.</summary>
    private bool IsAlreadyCurrent(WorkingCoordinateSystem coordinateSystem, Vec3 normal) =>
        coordinateSystem is not null &&
        coordinateSystem.Revision.Value == Bridge.GetModelRevision() &&
        coordinateSystem.Projection.Normal == normal;

    /// <summary>법선에 수직인 X축을 골라 Native 작업면 질의 context를 만듭니다.</summary>
    private static WorkingSurfaceContext CreateWorkingSurfaceContext(Vec3 normal)
    {
        Vec3 xAxis = Math.Abs(normal.X) < 0.9
            ? new Vec3(1, 0, 0)
            : new Vec3(0, 1, 0);

        return Bridge.CreateWorkingSurfaceContext(
            normal.X, normal.Y, normal.Z,
            xAxis.X, xAxis.Y, xAxis.Z)
            ?? throw new InvalidOperationException("전체 모델 작업 좌표계 구성에 실패했습니다.");
    }

    /// <summary>Native frame 배열을 관리형 작업 좌표계 값으로 변환합니다.</summary>
    private static WorkingCoordinateSystem CreateCoordinateSystem(WorkingSurfaceContext context)
    {
        double[] frame = context.Frame;
        var projection = new ProjectionFrame(
            new Vec3(frame[0], frame[1], frame[2]),
            new Vec3(frame[9], frame[10], frame[11]),
            new Vec3(frame[3], frame[4], frame[5]));

        return new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(context.Id),
            new ModelRevision(context.Revision),
            projection);
    }

    /// <summary>새 좌표계를 확정한 뒤 기존 편집 상태와 오버레이를 정리합니다.</summary>
    private Exception CompleteWorkingCoordinateSystemCommit(
        WorkingCoordinateSystem previous,
        WorkingCoordinateSystem next,
        bool preserveCommittedGeometry)
    {
        Exception firstFailure = null;
        RunPostCommitStep(_afterWorkingCoordinateSystemCommit, ref firstFailure);

        if (previous is not null)
        {
            RunPostCommitStep(
                () => Bridge.ReleaseWorkingSurfaceContext(previous.Id.Value),
                ref firstFailure);
        }

        RunPostCommitStep(
            () => ResetCircuitEditState(
                clearNative: true,
                preserveCommittedGeometry: preserveCommittedGeometry),
            ref firstFailure);
        RunPostCommitStep(() =>
        {
            RegionSelection.Clear();
            SelectedFacesList.Items.Clear();
            FaceCombo.SelectedIndex = -1;
            RefreshExitSideChoices();
            RegionSelection.ProjectionFrame = next.Projection;
        }, ref firstFailure);

        RunPostCommitStep(Bridge.HideFaceHighlights, ref firstFailure);
        if (!preserveCommittedGeometry)
            RunPostCommitStep(Bridge.ClearOverlays, ref firstFailure);
        RunPostCommitStep(Bridge.ClearExclusionOverlays, ref firstFailure);
        RunPostCommitStep(DrawRegionCanvas, ref firstFailure);

        Vec3 normal = next.Projection.Normal;
        WorkingCoordinates.DisplayStatus =
            $"작업 좌표계 N={normal} · 모델 r{next.Revision.Value} · 삽입 깊이 0";
        if (firstFailure is null)
            StatusText.Text = "작업 좌표계 적용 완료 · 삽입 깊이는 현재 0만 지원 · 연결부 위치 지정은 보류";

        return firstFailure;
    }

    /// <summary>좌표계를 바꾸면 다시 검증해야 하는 영속 설계 입력이 있는지 확인합니다.</summary>
    private bool HasCommittedDesignInputs()
    {
        DesignProject project = _projectSession.Current.CurrentProject;
        return project is not null &&
            (project.HeatingRegions.Count > 0 ||
             project.WireSpecification is not null ||
             project.PlacementConstraints is not null ||
             project.HeatingPaths.Count > 0 ||
             project.Circuits.Count > 0 ||
             project.ConnectionPoints.Count > 0 ||
             project.ConnectionPaths.Count > 0);
    }

    /// <summary>후처리 한 단계를 실행하고 처음 발생한 예외를 보관합니다.</summary>
    private static void RunPostCommitStep(Action step, ref Exception firstFailure)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }
    }

    /// <summary>확정 전 실패에서 표시 저장점을 복원하고 새 context를 폐기합니다.</summary>
    private void RestorePreviousWorkingCoordinateSystem(
        WorkingCoordinateSystemPresentation presentation,
        WorkingSurfaceContext candidateContext)
    {
        bool restored = false;
        try
        {
            restored = Bridge.RestoreWorkingCoordinateSystemPresentation(presentation.Id);
        }
        catch
        {
            // 복원 실패는 아래에서 좌표계 세션을 무효화해 안전하게 닫습니다.
        }

        WorkingCoordinates.Session.CancelPreview();
        if (candidateContext is not null)
        {
            try
            {
                Bridge.ReleaseWorkingSurfaceContext(candidateContext.Id);
            }
            catch
            {
                // 후보 context는 더 이상 화면에 공개하지 않습니다.
            }
        }

        if (restored)
            return;

        try
        {
            ResetWorkingCoordinateSystem();
        }
        catch
        {
            WorkingCoordinates.Session.Invalidate();
        }
    }

    /// <summary>확정 뒤 정리 중 발생한 예외를 실패 상태로 되돌리지 않고 표시합니다.</summary>
    private void ReportPostCommitFailure(Exception exception) =>
        StatusText.Text = "작업 좌표계는 적용되었으나 후처리에 실패했습니다: " + exception.Message;

    /// <summary>Native 표시 저장점을 사용한 뒤 예외와 무관하게 자원을 해제합니다.</summary>
    private static void ReleasePresentationSavepoint(long presentationId)
    {
        try
        {
            Bridge.ReleaseWorkingCoordinateSystemPresentation(presentationId);
        }
        catch
        {
            // 저장점 해제 실패가 이미 확정된 작업 좌표계 상태를 바꾸지는 않습니다.
        }
    }

    /// <summary>현재 확정 좌표계의 정면 방향으로 카메라를 돌립니다.</summary>
    private void WorkingFront_Click(object sender, RoutedEventArgs e)
    {
        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        bool returnedToFront = coordinateSystem is not null &&
            Bridge.ReturnToWorkingFront(coordinateSystem.Id.Value, coordinateSystem.Revision.Value);

        if (!returnedToFront)
            StatusText.Text = "작업 좌표계 context가 만료되었습니다";
    }
}
