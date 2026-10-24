using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using HeatingMold.App;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.OccBridge;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>전역 Native 대역 상태를 공유하는 WPF 검증 묶음입니다.</summary>
[CollectionDefinition("WPF Bridge", DisableParallelization = true)]
public sealed class WpfBridgeCollection
{
}

/// <summary>WPF 창을 STA에서 실행하는 공통 테스트 도우미입니다.</summary>
internal static class WpfTestThread
{
    /// <summary>WPF 테스트 동작을 전용 STA 스레드에서 실행합니다.</summary>
    public static void Run(Action test)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

/// <summary>작업 좌표계 변경의 확정 경계를 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class WorkingCoordinateSystemTransactionTests
{
    /// <summary>확정 후 정리 오류가 나도 새 좌표계를 유지하고 남은 정리를 계속합니다.</summary>
    [Fact]
    public void PostCommitFailure_KeepsNewCoordinateSystemAndContinuesCleanup()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var window = new MainWindow();
            SetField(window, "_afterWorkingCoordinateSystemCommit", new Action(
                () => throw new InvalidOperationException("시험용 후처리 오류")));

            bool applied = (bool)Invoke(window, "ApplyWorkingCoordinateSystem", new Vec3(0, 0, 1));
            WorkingCoordinateSystemSession session = GetSession(window);

            Assert.True(applied);
            Assert.NotNull(session.Confirmed);
            Assert.Equal(1, session.Confirmed.Projection.Normal.Z);
            Assert.Contains("후처리에 실패", window.StatusText.Text);
            Assert.Equal(1, Bridge.HideFaceHighlightsCount);
            Assert.Equal(1, Bridge.ClearOverlaysCount);
            Assert.Equal(1, Bridge.ClearExclusionOverlaysCount);
        });
    }

    /// <summary>확정 전 실패는 이전 좌표계와 표시를 복구하고 후보 context를 폐기합니다.</summary>
    [Fact]
    public void PreCommitFailure_RestoresPreviousCoordinateSystemAndPresentation()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var window = new MainWindow();
            Assert.True((bool)Invoke(window, "ApplyWorkingCoordinateSystem", new Vec3(0, 0, 1)));
            WorkingCoordinateSystemSession session = GetSession(window);
            HeatingMold.Core.Geometry.WorkingCoordinateSystem previous = session.Confirmed;
            long previousContextId = previous.Id.Value;

            SetField(window, "_beforeWorkingCoordinateSystemCommit", new Action(
                () => throw new InvalidOperationException("시험용 확정 전 오류")));
            bool applied = (bool)Invoke(window, "ApplyWorkingCoordinateSystem", new Vec3(0, 0, -1));

            Assert.False(applied);
            Assert.Same(previous, session.Confirmed);
            Assert.Null(session.Preview);
            Assert.Equal(1, Bridge.RestoredPresentationCount);
            Assert.Contains(Bridge.ReleasedContextIds, id => id != previousContextId);
            Assert.DoesNotContain(previousContextId, Bridge.ReleasedContextIds);
        });
    }

    private static WorkingCoordinateSystemSession GetSession(MainWindow window) =>
        ((MainWindowViewModel)GetField(window, "_mainWindowViewModel")).WorkingCoordinates.Session;

    private static object Invoke(MainWindow window, string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"테스트 메서드를 찾지 못했습니다: {methodName}");
        return method.Invoke(window, arguments);
    }

    private static object GetField(MainWindow window, string fieldName) =>
        typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new InvalidOperationException($"테스트 필드를 찾지 못했습니다: {fieldName}");

    private static void SetField(MainWindow window, string fieldName, object value)
    {
        FieldInfo field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"테스트 필드를 찾지 못했습니다: {fieldName}");
        field.SetValue(window, value);
    }
}

/// <summary>STEP 로드가 성공하기 전 문서 종속 화면 상태를 보존하는지 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class ModelLoadingTests
{
    /// <summary>확정 모델이 없는 상태에서는 정사영 DXF 출력을 요청하지 않습니다.</summary>
    [Fact]
    public void ExportOrthographicViewsToPath_RequiresCommittedGeometryModel()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var exporter = new RecordingOrthographicDxfExporter();
            var useCase = new ExportOrthographicViews(exporter);
            AppCompositionRoot.MainWindowServices services =
                AppCompositionRoot.CreateMainWindowServices(exportOrthographicViews: useCase);
            var window = new MainWindow(services);

            Invoke(window, "ExportOrthographicViewsToPath", "orthographic.dxf");

            Assert.Empty(exporter.Calls);
            Assert.Contains("STEP 모델을 먼저 여세요", window.StatusText.Text);
        });
    }

    /// <summary>확정 모델을 연 뒤에는 패턴 생성 여부와 무관하게 HLR 네 장을 출력합니다.</summary>
    [Fact]
    public void ExportOrthographicViewsToPath_ExportsFourViewsFromLoadedModel()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".step"));
        File.WriteAllText(sourcePath, "시험 STEP 원본");

        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                Bridge.ShapeInfo = new ShapeInfo { Faces = 2, Solids = 1, Shells = 1, Planes = 2 };
                ConfigurePreparedModelCandidate(88);

                var exporter = new RecordingOrthographicDxfExporter();
                var useCase = new ExportOrthographicViews(exporter);
                AppCompositionRoot.MainWindowServices services =
                    AppCompositionRoot.CreateMainWindowServices(exportOrthographicViews: useCase);
                var window = new MainWindow(services);
                Invoke(window, "LoadFile", sourcePath);

                Invoke(window, "ExportOrthographicViewsToPath", "orthographic.dxf");

                RecordedOrthographicExport export = Assert.Single(exporter.Calls);
                Assert.Equal("orthographic.dxf", export.Path);
                Assert.Equal(4, export.Views.Count);
                Assert.Contains("DXF 저장", window.StatusText.Text);
            });
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>패키지 저장·복원은 같은 프로젝트와 Face ID를 유지하고 실패는 기존 화면을 보존합니다.</summary>
    [Fact]
    public void SaveAndOpenProject_RestoresPreparedDocumentAndPreservesStateOnInvalidPackage()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".step"));
        string projectPath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".hmproj"));
        string invalidProjectPath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".hmproj"));
        File.WriteAllText(sourcePath, "테스트 STEP 원본");
        File.WriteAllText(invalidProjectPath, "잘못된 패키지");

        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                Bridge.ShapeInfo = new ShapeInfo { Faces = 2, Solids = 1, Shells = 1, Planes = 2 };
                ConfigurePreparedModelCandidate(84);
                var window = new MainWindow();
                Invoke(window, "LoadFile", sourcePath);

                ProjectSession projectSession = (ProjectSession)GetField(window, "_projectSession");
                DesignProject originalProject = Assert.IsType<DesignProject>(projectSession.Current.CurrentProject);
                Guid[] originalFaceIds = originalProject.FaceReferences.Select(reference => reference.Id.Value).ToArray();

                Invoke(window, "SaveProjectToPath", projectPath);

                Assert.True(File.Exists(projectPath));
                Assert.Equal(originalProject.Revision, projectSession.Current.LastSavedRevision);
                Assert.Contains("프로젝트 저장", window.StatusText.Text);

                ConfigurePreparedModelCandidate(85);
                Invoke(window, "OpenProjectFromPath", projectPath);

                DesignProject restoredProject = Assert.IsType<DesignProject>(projectSession.Current.CurrentProject);
                Assert.Equal(originalProject.Id, restoredProject.Id);
                Assert.Equal(originalProject.Revision.Next(), restoredProject.Revision);
                Assert.Equal(originalFaceIds, restoredProject.FaceReferences.Select(reference => reference.Id.Value));
                Assert.Equal(originalProject.WorkingCoordinateSystem, restoredProject.WorkingCoordinateSystem);
                Assert.Contains(originalProject.Name, window.StatusText.Text);
                Assert.Equal(2, Bridge.ClearOverlaysCount);

                RegionSelectionViewModel regionSelection = GetRegionSelection(window);
                FaceRefId selectedFaceReferenceId = FaceRefId.New();
                regionSelection.Add(selectedFaceReferenceId);
                window.SelectedFacesList.Items.Add("선택 면");
                string preparedPath = Bridge.LastPreparedStepCandidatePath;

                Invoke(window, "OpenProjectFromPath", invalidProjectPath);

                Assert.Same(restoredProject, projectSession.Current.CurrentProject);
                Assert.Contains(selectedFaceReferenceId, regionSelection.SelectedFaceReferences);
                Assert.Single(window.SelectedFacesList.Items);
                Assert.Equal(preparedPath, Bridge.LastPreparedStepCandidatePath);
                Assert.Contains("프로젝트 열기 실패", window.StatusText.Text);
            });
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(projectPath);
            File.Delete(invalidProjectPath);
        }
    }

    /// <summary>후보 로드 실패가 기존 작업 좌표계와 선택을 지우지 않습니다.</summary>
    [Fact]
    public void LoadFileFailure_PreservesCurrentCoordinateSystemAndSelection()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".step"));
        File.WriteAllText(sourcePath, "테스트 STEP 원본");
        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                var window = new MainWindow();
                Assert.True((bool)Invoke(window, "ApplyWorkingCoordinateSystem", new Vec3(0, 0, 1)));
                WorkingCoordinateSystemSession session = GetSession(window);
                HeatingMold.Core.Geometry.WorkingCoordinateSystem previous = session.Confirmed;
                RegionSelectionViewModel regionSelection = GetRegionSelection(window);
                FaceRefId selectedFaceReferenceId = FaceRefId.New();
                regionSelection.Add(selectedFaceReferenceId);
                window.SelectedFacesList.Items.Add("면 7");
                Bridge.NextPreparedStepCandidateResult = new ModelLoadCandidateResult
                {
                    Status = ModelLoadCandidateStatus.Failed,
                    Diagnostic = "StepReadFailed"
                };

                Invoke(window, "LoadFile", sourcePath);

                Assert.Same(previous, session.Confirmed);
                Assert.Contains(selectedFaceReferenceId, regionSelection.SelectedFaceReferences);
                Assert.Single(window.SelectedFacesList.Items);
                Assert.Contains("StepReadFailed", window.StatusText.Text);
                Assert.Null(((ProjectSession)GetField(window, "_projectSession")).Current.CurrentProject);
            });
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>새 모델 확정 뒤 이전 Face 선택을 버리고 새 문서 참조를 만듭니다.</summary>
    [Fact]
    public void LoadFileSuccess_ClearsOldSelectionAndCreatesNewFaceReferences()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".step"));
        File.WriteAllText(sourcePath, "테스트 STEP 원본");
        string expectedSourceSha256 = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();

        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                Bridge.ShapeInfo = new ShapeInfo { Faces = 2, Solids = 1, Shells = 1, Planes = 2 };
                Bridge.NextPreparedStepCandidateResult = new ModelLoadCandidateResult
                {
                    Status = ModelLoadCandidateStatus.Prepared,
                    BaseModelRevision = Bridge.GetModelRevision(),
                    CandidateId = 74
                };
                Bridge.NextPreparedStepPublishResult = new ModelLoadResult
                {
                    Status = ModelLoadStatus.CommittedPresentationOutOfSync,
                    Diagnostic = "ActiveModelPresentationFailed",
                    ModelRevision = Bridge.GetModelRevision() + 1
                };
                Bridge.PreparedCandidateFaceGeometryFeatures = new FaceGeometryFeaturesResult
                {
                    Status = FaceFeatureQueryStatus.Success,
                    Features = Enumerable.Range(0, 2).Select(faceIndex => new FaceGeometryFeatureData
                    {
                        FaceIndex = faceIndex,
                        SurfaceType = "Plane",
                        SurfaceAreaMm2 = 0.5,
                        Center = [faceIndex * 3d + 0.5, 0.5, 0],
                        AdjacentFaceIndices = Array.Empty<int>()
                    }).ToArray()
                };
                Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
                {
                    Id = 1200,
                    Revision = Bridge.GetModelRevision() + 1,
                    Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
                };
                var window = new MainWindow();
                Assert.True((bool)Invoke(window, "ApplyWorkingCoordinateSystem", new Vec3(0, 0, 1)));
                RegionSelectionViewModel regionSelection = GetRegionSelection(window);
                regionSelection.Add(FaceRefId.New());
                window.SelectedFacesList.Items.Add("이전 면");

                Invoke(window, "LoadFile", sourcePath);

                WorkingCoordinateSystemSession session = GetSession(window);
                var faceReferenceIndex = (FaceReferenceIndex)GetField(window, "_faceReferenceIndex");
                IReadOnlyDictionary<int, FaceRefId> references = faceReferenceIndex.ReferencesByNativeIndex;
                IReadOnlyDictionary<FaceRefId, int> nativeIndices = faceReferenceIndex.NativeIndicesByReference;
                Assert.Empty(regionSelection.SelectedFaceReferences);
                Assert.Empty(window.SelectedFacesList.Items);
                Assert.Equal(2, references.Count);
                Assert.Equal(2, nativeIndices.Count);
                Assert.Contains(0, references.Keys);
                Assert.Contains(1, references.Keys);
                Assert.All(references.Values, reference => Assert.False(reference.IsEmpty));
                Assert.Equal(0, nativeIndices[references[0]]);
                Assert.Equal(1, nativeIndices[references[1]]);
                Assert.Equal(1, session.Confirmed.Projection.Normal.Z);
                ProjectSession projectSession = (ProjectSession)GetField(window, "_projectSession");
                DesignProject project = Assert.IsType<DesignProject>(projectSession.Current.CurrentProject);
                Assert.Equal(expectedSourceSha256, project.SourceModel.SourceSha256);
                Assert.Equal(2, project.FaceReferences.Count);
                Assert.All(references.Values, referenceId =>
                    Assert.Contains(project.FaceReferences, reference => reference.Id == referenceId));
                Assert.Equal(project.WorkingCoordinateSystem.Origin, session.Confirmed.Projection.Origin);
                Assert.Contains("화면 표시를 다시 구성했습니다", window.StatusText.Text);
            });
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static WorkingCoordinateSystemSession GetSession(MainWindow window) =>
        ((MainWindowViewModel)GetField(window, "_mainWindowViewModel")).WorkingCoordinates.Session;

    private static RegionSelectionViewModel GetRegionSelection(MainWindow window)
        => ((MainWindowViewModel)GetField(window, "_mainWindowViewModel")).RegionSelection;

    private static void ConfigurePreparedModelCandidate(long candidateId)
    {
        long baseRevision = Bridge.GetModelRevision();
        Bridge.NextPreparedStepCandidateResult = new ModelLoadCandidateResult
        {
            Status = ModelLoadCandidateStatus.Prepared,
            BaseModelRevision = baseRevision,
            CandidateId = candidateId
        };
        Bridge.NextPreparedStepPublishResult = new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed,
            ModelRevision = baseRevision + 1
        };
        Bridge.PreparedCandidateFaceGeometryFeatures = new FaceGeometryFeaturesResult
        {
            Status = FaceFeatureQueryStatus.Success,
            Features = Enumerable.Range(0, 2).Select(faceIndex => new FaceGeometryFeatureData
            {
                FaceIndex = faceIndex,
                SurfaceType = "Plane",
                SurfaceAreaMm2 = 0.5,
                Center = [faceIndex * 3d + 0.5, 0.5, 0],
                AdjacentFaceIndices = Array.Empty<int>()
            }).ToArray()
        };
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 1200 + candidateId,
            Revision = baseRevision + 1,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
    }

    private static object Invoke(MainWindow window, string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"테스트 메서드를 찾지 못했습니다: {methodName}");
        return method.Invoke(window, arguments);
    }

    private static object GetField(MainWindow window, string fieldName) =>
        typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new InvalidOperationException($"테스트 필드를 찾지 못했습니다: {fieldName}");

    /// <summary>화면 테스트에서 지정한 내부 상태를 바꿉니다.</summary>
    private static void SetField(MainWindow window, string fieldName, object value)
    {
        FieldInfo field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"테스트 필드를 찾지 못했습니다: {fieldName}");
        field.SetValue(window, value);
    }

    /// <summary>설계 export가 확정 snapshot과 형식별 옵션을 출력기로 전달하는지 확인합니다.</summary>
    [Fact]
    public void ExportDesignToPath_UsesCommittedSnapshotAndRequestedFormatOptions()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".step"));
        File.WriteAllText(sourcePath, "시험 STEP 원본");

        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                Bridge.ShapeInfo = new ShapeInfo { Faces = 2, Solids = 1, Shells = 1, Planes = 2 };
                ConfigurePreparedModelCandidate(86);

                var exporter = new RecordingDesignExporter();
                AppCompositionRoot.MainWindowServices services =
                    AppCompositionRoot.CreateMainWindowServices(designExporter: exporter);
                var window = new MainWindow(services);
                Invoke(window, "LoadFile", sourcePath);

                DesignProject project = Assert.IsType<DesignProject>(services.ProjectSession.Current.CurrentProject);
                Invoke(window, "ExportDesignToPath", "design.dxf", ExportFormat.Dxf, false);
                Invoke(window, "ExportDesignToPath", "design.step", ExportFormat.Step, true);

                Assert.Equal(2, exporter.Calls.Count);
                Assert.Same(project, exporter.Calls[0].Snapshot.Project);
                Assert.Equal("design.dxf", exporter.Calls[0].Path);
                Assert.Equal(ExportFormat.Dxf, exporter.Calls[0].Options.Format);
                Assert.False(exporter.Calls[0].Options.IncludeSourceModel);
                Assert.Equal("design.step", exporter.Calls[1].Path);
                Assert.Equal(ExportFormat.Step, exporter.Calls[1].Options.Format);
                Assert.True(exporter.Calls[1].Options.IncludeSourceModel);
                Assert.Contains("설계 STEP 저장 완료", window.StatusText.Text);
            });
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>확정 프로젝트가 없으면 저장 포트를 호출하지 않고 실패를 표시합니다.</summary>
    [Fact]
    public void SaveProjectToPath_RequiresCommittedProject()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), string.Concat(Guid.NewGuid(), ".hmproj"));

        try
        {
            WpfTestThread.Run(() =>
            {
                Bridge.Reset();
                var window = new MainWindow();

                Invoke(window, "SaveProjectToPath", projectPath);

                ProjectSession session = (ProjectSession)GetField(window, "_projectSession");
                Assert.False(File.Exists(projectPath));
                Assert.Null(session.Current.CurrentProject);
                Assert.Null(session.Current.LastSavedRevision);
                Assert.Contains("프로젝트 저장 실패", window.StatusText.Text);
            });
        }
        finally
        {
            if (File.Exists(projectPath))
                File.Delete(projectPath);
        }
    }

    /// <summary>확정 프로젝트가 없으면 설계 출력기를 호출하지 않습니다.</summary>
    [Fact]
    public void ExportDesignToPath_RequiresCommittedProject()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var exporter = new RecordingDesignExporter();
            AppCompositionRoot.MainWindowServices services =
                AppCompositionRoot.CreateMainWindowServices(designExporter: exporter);
            var window = new MainWindow(services);

            Invoke(window, "ExportDesignToPath", "design.dxf", ExportFormat.Dxf, false);

            Assert.Empty(exporter.Calls);
            Assert.Contains("설계 DXF 저장 실패", window.StatusText.Text);
        });
    }

    /// <summary>WPF export 경계에서 전달된 snapshot과 설정을 기록하는 출력기입니다.</summary>
    private sealed class RecordingDesignExporter : IDesignExporter
    {
        public List<RecordedExport> Calls { get; } = new();

        /// <summary>호출 내용을 보관하고 테스트용 성공 receipt를 반환합니다.</summary>
        public OperationResult<ExportReceipt> Export(
            ExportSnapshot snapshot,
            ExportOptions options,
            string path,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedExport(snapshot, options, path));
            return OperationResult<ExportReceipt>.Success(
                new ExportReceipt(path, options.Format, 64, new string('a', 64)));
        }
    }

    /// <summary>정사영 DXF 출력 호출과 전달된 도면을 기록하는 대역입니다.</summary>
    private sealed class RecordingOrthographicDxfExporter : IOrthographicDxfExporter
    {
        public List<RecordedOrthographicExport> Calls { get; } = new();

        /// <summary>파일 경로와 정사영 목록을 기록하고 성공 영수증을 반환합니다.</summary>
        public OperationResult<ExportReceipt> ExportViews(
            string path,
            IReadOnlyList<OrthographicDxfView> views,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedOrthographicExport(path, views.ToArray()));
            return OperationResult<ExportReceipt>.Success(
                new ExportReceipt(path, ExportFormat.Dxf, 64, new string('b', 64)));
        }
    }

    /// <summary>출력 요청에서 관찰한 파일 경로와 네 장의 도면입니다.</summary>
    private sealed record RecordedOrthographicExport(
        string Path,
        IReadOnlyList<OrthographicDxfView> Views);

    /// <summary>설계 출력 호출에서 관찰한 snapshot, 형식 옵션과 경로입니다.</summary>
    private sealed record RecordedExport(ExportSnapshot Snapshot, ExportOptions Options, string Path);
}
