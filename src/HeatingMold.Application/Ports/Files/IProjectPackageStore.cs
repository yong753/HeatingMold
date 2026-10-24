using System.Collections.ObjectModel;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Electrical.Area;

namespace HeatingMold.Application.Ports.Files;

/// <summary>프로젝트 패키지 저장에 사용할 확정 snapshot과 원본 모델 바이트입니다.</summary>
public sealed class ProjectSaveSnapshot
{
    /// <summary>저장할 불변 도메인 프로젝트입니다.</summary>
    public DesignProject Project { get; }

    /// <summary>패키지에 함께 넣을 원본 STEP입니다.</summary>
    public ModelSource SourceModel { get; }

    /// <summary>저장 당시 사용할 최종 Native 기하 결과입니다.</summary>
    public GeometrySnapshot? Geometry { get; }

    /// <summary>유효한 경우 저장할 회로별 표면 면적 분할입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>현재 유효한 경우 저장할 면적·전기 파생 결과입니다.</summary>
    public IReadOnlyList<ElectricalResult> ElectricalResults { get; }

    /// <summary>현재 저장 revision에서 전기 조건을 통과한 면적입니다.</summary>
    public ElectricallyCompliantAreaResult? ElectricalAreaResult { get; }

    /// <summary>최신 입력 상태를 확인해 패키지 저장값을 만듭니다.</summary>
    public ProjectSaveSnapshot(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        DesignProject? project = session.CurrentProject;
        if (project is null)
            throw new InvalidOperationException("열린 확정 프로젝트가 없습니다.");
        if (session.CurrentInputDraft is not null)
            throw new InvalidOperationException("미적용 입력 초안이 있는 동안 저장할 수 없습니다.");
        if (session.SourceModel is null)
            throw new InvalidOperationException("패키지에 넣을 원본 모델 바이트가 없습니다.");
        if (session.SourceModel.Identity.SourceSha256 != project.SourceModel.SourceSha256)
            throw new InvalidOperationException("세션 원본 모델 바이트가 확정 프로젝트와 다릅니다.");
        if (session.Geometry is not null &&
            (session.Geometry.DependencyStamp.DesignRevision != project.Revision ||
             session.Geometry.DependencyStamp.SourceModelSha256 != project.SourceModel.SourceSha256))
        {
            throw new InvalidOperationException("현재 기하 결과가 확정 프로젝트 revision과 다릅니다.");
        }
        if (session.Geometry is null &&
            (session.ElectricalResults.Count > 0 || session.AreaAllocation is not null || session.ElectricalAreaResult is not null))
        {
            throw new InvalidOperationException("기하 결과 없이 저장할 수 없는 파생 결과가 있습니다.");
        }
        if (session.Geometry is { } geometry &&
            (session.ElectricalResults.Any(result => result.DependencyStamp != geometry.DependencyStamp) ||
             session.AreaAllocation is not null && session.AreaAllocation.DependencyStamp != geometry.DependencyStamp ||
             session.ElectricalAreaResult is not null &&
             (session.AreaAllocation is null ||
              session.ElectricalAreaResult.Allocation.DependencyStamp != geometry.DependencyStamp)))
        {
            throw new InvalidOperationException("저장할 파생 결과가 현재 기하 입력 지문과 다릅니다.");
        }

        Project = project;
        SourceModel = session.SourceModel;
        Geometry = session.Geometry;
        AreaAllocation = session.AreaAllocation;
        ElectricalResults = new ReadOnlyCollection<ElectricalResult>(session.ElectricalResults.ToArray());
        ElectricalAreaResult = session.ElectricalAreaResult;
    }
}

/// <summary>패키지에서 매핑·검증된 프로젝트와 원본 바이트입니다.</summary>
public sealed class ProjectPackageReadResult
{
    /// <summary>패키지에서 매핑하고 참조 검증한 영속 설계입니다.</summary>
    public DesignProject Project { get; }

    /// <summary>패키지에서 해시를 확인한 원본 모델 바이트입니다.</summary>
    public ModelSource SourceModel { get; }

    /// <summary>복원 후 stamp를 재확인해 재사용할 수 있는 면적 분할 cache입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>설계의 원본 해시와 바이트가 일치하는 복원 입력을 만듭니다.</summary>
    public ProjectPackageReadResult(
        DesignProject project,
        ModelSource sourceModel,
        AppliedAreaAllocation? areaAllocation = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        SourceModel = sourceModel ?? throw new ArgumentNullException(nameof(sourceModel));
        if (project.SourceModel.SourceSha256 != sourceModel.Identity.SourceSha256)
            throw new ArgumentException("패키지 원본 바이트와 프로젝트 원본 해시가 다릅니다.", nameof(sourceModel));
        if (!project.ValidateReferences().IsValid)
            throw new ArgumentException("패키지 프로젝트에 유효하지 않은 영속 참조가 있습니다.", nameof(project));
        AreaAllocation = areaAllocation;
    }
}

/// <summary>원자 저장한 경로·revision과 최종 패키지 해시입니다.</summary>
public sealed record ProjectPackageWriteReceipt
{
    /// <summary>원자 교체가 완료된 패키지 파일 경로입니다.</summary>
    public string Path { get; }

    /// <summary>패키지에 저장한 프로젝트 revision입니다.</summary>
    public DesignRevision SavedRevision { get; }

    /// <summary>완료된 패키지 파일 크기(byte)입니다.</summary>
    public long FileLengthBytes { get; }

    /// <summary>완료된 패키지 파일의 SHA-256입니다.</summary>
    public string PackageSha256 { get; }

    /// <summary>완료 파일의 경로, 크기, revision과 패키지 해시를 검증합니다.</summary>
    public ProjectPackageWriteReceipt(
        string path,
        DesignRevision savedRevision,
        long fileLengthBytes,
        string packageSha256)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("저장된 패키지 경로가 필요합니다.", nameof(path));
        if (fileLengthBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileLengthBytes), "저장된 패키지 크기는 양수여야 합니다.");
        if (packageSha256 is null || packageSha256.Length != 64 || packageSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("패키지 SHA-256은 16진수 64자여야 합니다.", nameof(packageSha256));

        Path = path.Trim();
        SavedRevision = savedRevision;
        FileLengthBytes = fileLengthBytes;
        PackageSha256 = packageSha256.ToLowerInvariant();
    }
}

/// <summary>프로젝트 패키지를 읽고 안전한 파일 교체로 저장하는 포트입니다.</summary>
public interface IProjectPackageStore
{
    /// <summary>ZIP·JSON·해시를 검사해 확정 설계 후보를 반환합니다.</summary>
    OperationResult<ProjectPackageReadResult> Read(string path, CancellationToken cancellationToken = default);

    /// <summary>완성·재검증한 패키지만 기존 파일과 원자 교체합니다.</summary>
    OperationResult<ProjectPackageWriteReceipt> Write(
        string path,
        ProjectSaveSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>외부 모델 파일을 읽어 해시가 붙은 원본 바이트를 제공합니다.</summary>
public interface IModelSourceStore
{
    /// <summary>외부 STEP를 읽고 가져오기 정보를 적용해 원본 모델을 캡처합니다.</summary>
    OperationResult<ModelSource> Capture(string path, CancellationToken cancellationToken = default);
}
