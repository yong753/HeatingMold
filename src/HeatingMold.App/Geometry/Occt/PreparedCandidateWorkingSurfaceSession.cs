using System;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>준비 후보의 Native 작업 context를 소유하고 후보 폐기 전에 해제합니다.</summary>
public sealed class PreparedCandidateWorkingSurfaceSession : IDisposable
{
    private const double FrameTolerance = 1e-8;

    private bool _isDisposed;

    /// <summary>이 context가 속한 STEP 후보 식별자입니다.</summary>
    public long CandidateId { get; }

    /// <summary>후보 Native context ID와 model revision이 결합된 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystem CoordinateSystem { get; }

    private PreparedCandidateWorkingSurfaceSession(long candidateId, WorkingCoordinateSystem coordinateSystem)
    {
        CandidateId = candidateId;
        CoordinateSystem = coordinateSystem;
    }

    /// <summary>후보 형상에서 좌표계를 만들고 frame이 유효할 때만 소유 session을 반환합니다.</summary>
    public static bool TryCreate(
        long candidateId,
        Vec3 normal,
        Vec3 preferredX,
        out PreparedCandidateWorkingSurfaceSession session,
        out string diagnostic)
    {
        session = null;
        diagnostic = null;
        if (candidateId <= 0)
        {
            diagnostic = "준비 후보 ID는 양수여야 합니다.";
            return false;
        }

        WorkingSurfaceContext nativeContext = Bridge.CreatePreparedCandidateWorkingSurfaceContext(
            candidateId,
            normal.X,
            normal.Y,
            normal.Z,
            preferredX.X,
            preferredX.Y,
            preferredX.Z);
        if (nativeContext is null)
        {
            diagnostic = "후보 작업 좌표계 context를 만들지 못했습니다.";
            return false;
        }

        try
        {
            WorkingCoordinateSystem coordinateSystem = ReadCoordinateSystem(nativeContext);
            session = new PreparedCandidateWorkingSurfaceSession(candidateId, coordinateSystem);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            Bridge.ReleasePreparedCandidateWorkingSurfaceContext(candidateId, nativeContext.Id);
            diagnostic = string.Concat("후보 작업 좌표계 frame이 유효하지 않습니다: ", exception.Message);
            return false;
        }
    }

    /// <summary>소유한 후보 context를 한 번만 해제합니다.</summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Bridge.ReleasePreparedCandidateWorkingSurfaceContext(CandidateId, CoordinateSystem.Id.Value);
    }

    /// <summary>해제된 후보 context를 사용하는 호출을 Native 경계 전에 거부합니다.</summary>
    internal void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(PreparedCandidateWorkingSurfaceSession));
    }

    private static WorkingCoordinateSystem ReadCoordinateSystem(WorkingSurfaceContext nativeContext)
    {
        if (nativeContext.Id <= 0 || nativeContext.Revision < 0)
            throw new ArgumentException("Native context ID와 revision이 유효하지 않습니다.");

        double[] frame = nativeContext.Frame;
        if (frame is null || frame.Length != 12)
            throw new ArgumentException("Native 작업 좌표계 frame에는 12개 좌표가 필요합니다.");

        var origin = new Vec3(frame[0], frame[1], frame[2]);
        var axisX = new Vec3(frame[3], frame[4], frame[5]);
        var axisY = new Vec3(frame[6], frame[7], frame[8]);
        var normal = new Vec3(frame[9], frame[10], frame[11]);

        if (!IsUnit(axisX) || !IsUnit(axisY) || !IsUnit(normal))
            throw new ArgumentException("Native frame의 세 축은 단위 벡터여야 합니다.");

        if (Math.Abs(Vec3.Dot(axisX, axisY)) > FrameTolerance ||
            Math.Abs(Vec3.Dot(axisY, normal)) > FrameTolerance ||
            Math.Abs(Vec3.Dot(normal, axisX)) > FrameTolerance)
        {
            throw new ArgumentException("Native frame의 세 축은 서로 직교해야 합니다.");
        }

        var projection = new ProjectionFrame(origin, normal, axisX);
        if ((projection.AxisX - axisX).Length > FrameTolerance ||
            (projection.AxisY - axisY).Length > FrameTolerance ||
            (projection.Normal - normal).Length > FrameTolerance)
        {
            throw new ArgumentException("Native frame의 축이 오른손 정규 직교 frame과 일치하지 않습니다.");
        }

        return new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(nativeContext.Id),
            new ModelRevision(nativeContext.Revision),
            projection);
    }

    /// <summary>축 길이가 허용 오차 안에서 1인지 확인합니다.</summary>
    private static bool IsUnit(Vec3 axis) => Math.Abs(axis.Length - 1) <= FrameTolerance;
}
