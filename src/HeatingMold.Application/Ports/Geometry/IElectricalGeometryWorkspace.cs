using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Application.Ports.Geometry;

/// <summary>전기 입력만 바뀐 후보에 기존 Native 형상 측정값을 다시 연결합니다.</summary>
public interface IElectricalGeometryWorkspace
{
    /// <summary>현재 확정 곡선과 Native 문서를 유지하며 후보 설계용 snapshot을 준비합니다.</summary>
    IPreparedGeometry PrepareElectricalUpdate(
        DesignProject candidate,
        GeometrySnapshot currentGeometry,
        RuntimeDocument currentRuntimeDocument,
        CancellationToken cancellationToken = default);
}
