using System.Collections.ObjectModel;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Validation;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Electrical.Area;

namespace HeatingMold.Application.Ports.Export;

/// <summary>출력 파일 형식을 나타냅니다.</summary>
public enum ExportFormat
{
    /// <summary>최종 3차원 곡선이 포함된 STEP 형식입니다.</summary>
    Step,

    /// <summary>작업 좌표계 정사영과 사양표가 포함된 DXF 형식입니다.</summary>
    Dxf
}

/// <summary>형식과 원본 모델 포함 여부를 지정하는 출력 설정입니다.</summary>
public sealed record ExportOptions
{
    /// <summary>내보낼 파일 형식입니다.</summary>
    public ExportFormat Format { get; }

    /// <summary>STEP 출력에 원본 금형을 함께 포함할지 나타냅니다.</summary>
    public bool IncludeSourceModel { get; }

    /// <summary>지원 형식과 선택한 원본 포함 설정으로 출력 옵션을 만듭니다.</summary>
    public ExportOptions(ExportFormat format, bool includeSourceModel = false)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (format == ExportFormat.Dxf && includeSourceModel)
            throw new ArgumentException("DXF 출력에는 원본 STEP 모델을 포함할 수 없습니다.", nameof(includeSourceModel));

        Format = format;
        IncludeSourceModel = includeSourceModel;
    }
}

/// <summary>한 revision에서 확인한 프로젝트와 최종 형상·계산 결과입니다.</summary>
public sealed class ExportSnapshot
{
    /// <summary>출력에 사용할 확정 도메인 프로젝트입니다.</summary>
    public DesignProject Project { get; }

    /// <summary>영구 문서와 revision이 맞는 최종 기하입니다.</summary>
    public GeometrySnapshot Geometry { get; }

    /// <summary>STEP 내보내기나 원본 포함 내보내기에 사용할 검증된 원본 모델입니다.</summary>
    public ModelSource SourceModel { get; }

    /// <summary>기하와 같은 지문을 가진 회로별 전기 결과입니다.</summary>
    public IReadOnlyList<ElectricalResult> ElectricalResults { get; }

    /// <summary>기하 revision에서 분할한 표면 면적입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>전기 판정으로 나눈 회로별 면적입니다.</summary>
    public ElectricallyCompliantAreaResult? ElectricalAreaResult { get; }

    /// <summary>출력 좌표를 고정하는 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystemDefinition WorkingCoordinateSystem => Project.WorkingCoordinateSystem;

    /// <summary>초안·오래된 결과·누락 곡선을 검사해 출력 불변 스냅샷을 만듭니다.</summary>
    public ExportSnapshot(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.CurrentInputDraft is not null)
            throw new InvalidOperationException("미적용 입력 초안이 있어 최신 출력을 만들 수 없습니다.");

        DesignProject project = session.CurrentProject
            ?? throw new InvalidOperationException("출력할 확정 프로젝트가 없습니다.");
        GeometrySnapshot geometry = session.Geometry
            ?? throw new InvalidOperationException("출력할 최종 기하 스냅샷이 없습니다.");
        if (session.RuntimeDocument is null || session.SourceModel is null)
            throw new InvalidOperationException("출력할 Native 문서 또는 원본 모델이 없습니다.");
        if (geometry.DocumentId != session.RuntimeDocument.DocumentId ||
            geometry.ModelRevision != session.RuntimeDocument.ModelRevision)
        {
            throw new InvalidOperationException("기하 스냅샷이 현재 Native 문서와 일치하지 않습니다.");
        }
        if (geometry.DependencyStamp.DesignRevision != project.Revision ||
            geometry.DependencyStamp.SourceModelSha256 != project.SourceModel.SourceSha256 ||
            session.SourceModel.Identity.SourceSha256 != project.SourceModel.SourceSha256)
        {
            throw new InvalidOperationException("기하 또는 원본 모델 결과가 현재 프로젝트 revision과 다릅니다.");
        }

        ValidationReport referenceReport = project.ValidateReferences();
        if (!referenceReport.IsValid)
            throw new InvalidOperationException("확정 프로젝트에 출력할 수 없는 참조 오류가 있습니다.");

        EnsureGeometryMatchesProject(project, geometry);
        EnsureDerivedStateIsCurrent(geometry, session);

        Project = project;
        Geometry = geometry;
        SourceModel = session.SourceModel;
        ElectricalResults = new ReadOnlyCollection<ElectricalResult>(session.ElectricalResults.ToArray());
        AreaAllocation = session.AreaAllocation;
        ElectricalAreaResult = session.ElectricalAreaResult;
    }

    private static void EnsureGeometryMatchesProject(DesignProject project, GeometrySnapshot geometry)
    {
        HashSet<Guid> expectedHeatingSegmentIds = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive)
            .Select(segment => segment.Id.Value)
            .ToHashSet();
        HashSet<Guid> expectedConnectionSegmentIds = project.ConnectionPaths
            .SelectMany(path => path.Segments)
            .Select(segment => segment.Id.Value)
            .ToHashSet();
        HashSet<Guid> actualHeatingSegmentIds = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Select(curve => curve.HeatingPathSegmentId!.Value.Value)
            .ToHashSet();
        HashSet<Guid> actualConnectionSegmentIds = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .Select(curve => curve.ConnectionPathSegmentId!.Value.Value)
            .ToHashSet();

        if (!expectedHeatingSegmentIds.SetEquals(actualHeatingSegmentIds) ||
            !expectedConnectionSegmentIds.SetEquals(actualConnectionSegmentIds))
        {
            throw new InvalidOperationException("최종 기하 구간이 확정 프로젝트와 다르거나 비활성 구간을 포함합니다.");
        }
        if (geometry.Curves.Any(curve => !curve.Approximation.IsComplete))
            throw new InvalidOperationException("근사 측정이 완료되지 않은 기하 곡선이 있어 출력할 수 없습니다.");
    }

    private static void EnsureDerivedStateIsCurrent(GeometrySnapshot geometry, SessionSnapshot session)
    {
        if (session.ElectricalResults.Any(result => result.DependencyStamp != geometry.DependencyStamp))
            throw new InvalidOperationException("전기 계산이 현재 기하 입력 지문과 다릅니다.");
        if (session.AreaAllocation is not null &&
            session.AreaAllocation.DependencyStamp != geometry.DependencyStamp)
        {
            throw new InvalidOperationException("면적 분할이 현재 기하 입력 지문과 다릅니다.");
        }
        if (session.ElectricalAreaResult is not null &&
            (session.AreaAllocation is null ||
             session.ElectricalAreaResult.Allocation.DependencyStamp != geometry.DependencyStamp))
        {
            throw new InvalidOperationException("전기 만족 면적이 현재 기하 입력 지문과 다릅니다.");
        }
    }
}

/// <summary>파일 저장 완료 후 파일·revision·검증 상태를 돌려줍니다.</summary>
public sealed record ExportReceipt(string Path, ExportFormat Format, long FileLengthBytes, string Sha256);

/// <summary>불변 출력 스냅샷을 STEP 또는 DXF 파일로 기록하는 포트입니다.</summary>
public interface IDesignExporter
{
    /// <summary>스냅샷과 같은 형상 revision을 선택한 파일 형식으로 출력합니다.</summary>
    OperationResult<ExportReceipt> Export(
        ExportSnapshot snapshot,
        ExportOptions options,
        string path,
        CancellationToken cancellationToken = default);
}
