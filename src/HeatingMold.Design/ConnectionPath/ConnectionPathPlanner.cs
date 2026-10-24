using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Validation;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.ConnectionPath;

/// <summary>사용자가 지정한 절점만 따라 표면 이탈과 외부 연결 경로를 만듭니다.</summary>
public sealed class ConnectionPathPlanner
{
    /// <summary>실제 발열사 끝점부터 연결부 좌표까지의 Native 충돌 검사와 경로 생성을 수행합니다.</summary>
    public OperationResult<CoreConnectionPath> Plan(
        ConnectionPathPlanningRequest request,
        IGeometryQueries geometryQueries,
        double connectionToleranceMm,
        string ruleVersion = "connection-path-v1",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        if (!double.IsFinite(connectionToleranceMm) || connectionToleranceMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(connectionToleranceMm));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("연결 경로 규칙 버전이 필요합니다.", nameof(ruleVersion));
        if (request.CoordinateSystem.Revision != request.ExpectedModelRevision)
            return Failure("StaleConnectionGeometry", "작업 좌표계 revision이 연결 경로 요청과 다릅니다.", ruleVersion, request);

        int[] transitionIndices = request.Legs
            .Select((leg, index) => (leg, index))
            .Where(item => item.leg.IsMoldExitTransition)
            .Select(item => item.index)
            .ToArray();
        if (transitionIndices.Length != 1)
            return Failure("InvalidMoldExitTransition", "연결 경로에는 금형 이탈 표식이 정확히 하나 필요합니다.", ruleVersion, request);

        int transitionIndex = transitionIndices[0];
        if (transitionIndex < 1 || request.Legs[transitionIndex].GeometryDomain != GeometryDomain.External)
        {
            return Failure(
                "InvalidMoldExitTransition",
                "금형 위 OnMold 구간 뒤에 사용자가 명시한 External 이탈 구간이 필요합니다.",
                ruleVersion,
                request);
        }

        Vec3 transitionPoint = request.Legs[transitionIndex - 1].EndPoint;
        double transitionGap = (transitionPoint - request.ConnectionPoint.SurfaceAnchor.ModelPoint).Length;
        if (transitionGap > connectionToleranceMm)
        {
            return Failure(
                "MoldExitAnchorMismatch",
                "금형 위 경로가 지정 표면 앵커에서 끝나지 않아 이탈 위치를 확인할 수 없습니다.",
                ruleVersion,
                request,
                transitionPoint,
                transitionGap,
                connectionToleranceMm);
        }

        double externalEndpointGap = (request.Legs[^1].EndPoint - request.ConnectionPoint.ExternalConnectionPoint).Length;
        if (externalEndpointGap > connectionToleranceMm)
        {
            return Failure(
                "ExternalConnectionPointMismatch",
                "마지막 사용자 구간이 지정한 외부 연결 좌표에서 끝나지 않습니다.",
                ruleVersion,
                request,
                request.Legs[^1].EndPoint,
                externalEndpointGap,
                connectionToleranceMm);
        }

        var segments = new List<ConnectionPathSegment>();
        Vec3 previous = request.HeatingWireEndpoint;
        for (int index = 0; index < request.Legs.Count; index++)
        {
            ConnectionPathLegRequest leg = request.Legs[index];
            double length = (leg.EndPoint - previous).Length;
            if (!double.IsFinite(length) || length <= connectionToleranceMm)
            {
                return Failure(
                    "DegenerateConnectionPathLeg",
                    "연결 경로의 연속 절점 사이 직선 길이는 접속 허용오차보다 커야 합니다.",
                    ruleVersion,
                    request,
                    leg.EndPoint,
                    length,
                    connectionToleranceMm);
            }

            if (index == 0 && leg.GeometryDomain != GeometryDomain.OnMold)
            {
                return Failure(
                    "ConnectionPathMustStartOnMold",
                    "연결 경로는 실제 발열사 끝점에서 금형 위 구간으로 시작해야 합니다.",
                    ruleVersion,
                    request);
            }

            if (index < transitionIndex && leg.GeometryDomain != GeometryDomain.OnMold ||
                index >= transitionIndex && leg.GeometryDomain != GeometryDomain.External)
            {
                return Failure(
                    "ConnectionPathDomainOrderInvalid",
                    "연결 구간은 OnMold에서 한 번만 External로 바뀌고 다시 금형으로 들어갈 수 없습니다.",
                    ruleVersion,
                    request);
            }

            segments.Add(new ConnectionPathSegment(
                ConnectionPathSegmentId.New(),
                index,
                leg.ConductorRole,
                leg.GeometryDomain,
                leg.AdditionalAllowanceMm,
                new LineCurveDefinition(previous, leg.EndPoint),
                leg.IsMoldExitTransition));
            previous = leg.EndPoint;
        }

        CoreConnectionPath path;
        try
        {
            path = new CoreConnectionPath(
                ConnectionPathId.New(),
                request.ConnectionPoint.Id,
                segments);
        }
        catch (ArgumentException exception)
        {
            return Failure("InvalidConnectionPath", exception.Message, ruleVersion, request);
        }

        GeometryQueryResult<ConnectionObstacleCheck> obstacleQuery = geometryQueries.CheckConnectionPathObstacles(
            path,
            request.CoordinateSystem,
            cancellationToken);
        if (obstacleQuery.Status != GeometryQueryStatus.Success || obstacleQuery.Value is null)
        {
            return OperationResult<CoreConnectionPath>.Failure(ValidationIssueFactory.QueryFailure(
                obstacleQuery,
                "ConnectionObstacleQuery",
                ruleVersion,
                path.Id.Value));
        }

        ConnectionObstacleCheck check = obstacleQuery.Value;
        var issues = new List<ValidationIssue>();
        if (!check.IsClear)
        {
            issues.Add(ValidationIssueFactory.Error(
                "ConnectionPathObstruction",
                "명시 연결 경로가 금형과 충돌하거나 금형 밖까지 안전하게 이어지지 않습니다.",
                ruleVersion,
                [path.Id.Value, request.ConnectionPoint.Id.Value],
                check.FirstObstruction,
                measuredValue: check.DistanceToObstruction?.Value,
                unit: "mm"));
        }
        if (!check.MoldExitTransitionFound)
        {
            issues.Add(ValidationIssueFactory.Error(
                "MoldExitTransitionUnresolved",
                "Native가 명시한 표면 이탈 전이를 확인하지 못했습니다.",
                ruleVersion,
                [path.Id.Value, request.ConnectionPoint.Id.Value]));
        }

        return issues.Count == 0
            ? OperationResult<CoreConnectionPath>.Success(path)
            : OperationResult<CoreConnectionPath>.Failure(issues);
    }

    private static OperationResult<CoreConnectionPath> Failure(
        string code,
        string evidence,
        string ruleVersion,
        ConnectionPathPlanningRequest request,
        Vec3? location = null,
        double? measuredValue = null,
        double? requiredValue = null) => OperationResult<CoreConnectionPath>.Failure(
        [ValidationIssueFactory.Error(
            code,
            evidence,
            ruleVersion,
            [request.Circuit.Id.Value, request.ConnectionPoint.Id.Value],
            location,
            measuredValue: measuredValue,
            requiredValue: requiredValue,
            unit: measuredValue.HasValue || requiredValue.HasValue ? "mm" : null)]);
}
