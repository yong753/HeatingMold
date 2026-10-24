enum class NativeConnectionObstacleStatus
{
    Clear,
    Obstructed,
    InvalidInput,
    StaleContext,
    GeometryFailure,
    Unresolved
};

/// <summary>연결 경로의 첫 금형 충돌점과 경로 시작점부터의 거리를 보관합니다.</summary>
struct NativeConnectionObstacleMeasurement final
{
    NativeConnectionObstacleStatus status = NativeConnectionObstacleStatus::Unresolved;
    gp_Pnt firstObstruction;
    double distanceToObstructionMm = 0.0;
    std::string diagnostic;
};

struct NativeConnectionObstacleContact final
{
    gp_Pnt point;
    double distanceAlongEdgeMm = 0.0;
};

bool ReadOrderedWireEndpointsNative(const TopoDS_Wire& wire, gp_Pnt& start, gp_Pnt& end)
{
    bool hasEdge = false;
    gp_Pnt previousEnd;
    for (BRepTools_WireExplorer explorer(wire); explorer.More(); explorer.Next())
    {
        TopoDS_Vertex firstVertex, lastVertex;
        TopExp::Vertices(explorer.Current(), firstVertex, lastVertex, Standard_True);
        if (firstVertex.IsNull() || lastVertex.IsNull())
            return false;

        const gp_Pnt currentStart = BRep_Tool::Pnt(firstVertex);
        const gp_Pnt currentEnd = BRep_Tool::Pnt(lastVertex);
        if (hasEdge && previousEnd.Distance(currentStart) > 0.01)
            return false;
        if (!hasEdge)
            start = currentStart;
        previousEnd = currentEnd;
        hasEdge = true;
    }

    if (!hasEdge)
        return false;
    end = previousEnd;
    return true;
}

bool MeasureEdgeLengthNative(const TopoDS_Edge& edge, double& lengthMm)
{
    GProp_GProps properties;
    BRepGProp::LinearProperties(edge, properties);
    lengthMm = properties.Mass();
    return std::isfinite(lengthMm) && lengthMm > 0.0;
}

bool MeasurePointDistanceAlongEdgeNative(
    const TopoDS_Edge& edge,
    const TopoDS_Vertex& vertex,
    double toleranceMm,
    std::vector<NativeConnectionObstacleContact>& contacts)
{
    BRepExtrema_DistShapeShape projection(edge, vertex);
    projection.Perform();
    if (!projection.IsDone() || projection.NbSolution() < 1 ||
        !std::isfinite(projection.Value()) || projection.Value() > toleranceMm)
    {
        return false;
    }

    const BRepAdaptor_Curve curve(edge);
    const double firstParameter = curve.FirstParameter();
    const double lastParameter = curve.LastParameter();
    if (!std::isfinite(firstParameter) || !std::isfinite(lastParameter) || firstParameter == lastParameter)
        return false;

    const double startParameter = edge.Orientation() == TopAbs_REVERSED ? lastParameter : firstParameter;
    for (int solutionIndex = 1; solutionIndex <= projection.NbSolution(); ++solutionIndex)
    {
        const double parameter = projection.ParOnEdgeS1(solutionIndex);
        if (!std::isfinite(parameter) || parameter < std::min(firstParameter, lastParameter) - Precision::PConfusion() ||
            parameter > std::max(firstParameter, lastParameter) + Precision::PConfusion())
        {
            return false;
        }

        const double localLength = GCPnts_AbscissaPoint::Length(
            curve,
            std::min(startParameter, parameter),
            std::max(startParameter, parameter),
            Precision::Confusion());
        if (!std::isfinite(localLength) || localLength < 0.0)
            return false;
        contacts.push_back({projection.PointOnShape1(solutionIndex), localLength});
    }

    return true;
}

NativeConnectionObstacleMeasurement CheckConnectionPathObstaclesNative(
    NativeDocumentSession& document,
    const std::vector<CurveDefinitionNative>& definitions,
    int moldExitTransitionIndex,
    double connectionToleranceMm)
{
    NativeConnectionObstacleMeasurement result;
    constexpr size_t MaximumCurveCount = 10000;

    if (document.shape.IsNull() || definitions.size() < 2 || definitions.size() > MaximumCurveCount ||
        moldExitTransitionIndex < 1 || moldExitTransitionIndex >= static_cast<int>(definitions.size()) ||
        !std::isfinite(connectionToleranceMm) || connectionToleranceMm <= 0.0)
    {
        result.status = NativeConnectionObstacleStatus::InvalidInput;
        result.diagnostic = "연결 경로 곡선이나 금형 이탈 구간 번호가 유효하지 않습니다.";
        return result;
    }

    try
    {
        std::vector<TopoDS_Wire> wires;
        std::vector<gp_Pnt> starts;
        wires.reserve(definitions.size());
        starts.reserve(definitions.size());

        gp_Pnt previousEnd;
        for (size_t index = 0; index < definitions.size(); ++index)
        {
            TopoDS_Wire wire;
            if (!BuildCurveWireNative(definitions[index], wire))
            {
                result.status = NativeConnectionObstacleStatus::Unresolved;
                result.diagnostic = "연결 경로 곡선의 정확한 OCCT wire를 만들지 못했습니다.";
                return result;
            }

            gp_Pnt start, end;
            if (!ReadOrderedWireEndpointsNative(wire, start, end) ||
                (index > 0 && previousEnd.Distance(start) > connectionToleranceMm))
            {
                result.status = NativeConnectionObstacleStatus::Unresolved;
                result.diagnostic = "연결 경로의 이웃 곡선 끝점이 허용 오차 안에서 만나지 않습니다.";
                return result;
            }

            wires.push_back(wire);
            starts.push_back(start);
            previousEnd = end;
        }

        const TopoDS_Vertex exitVertex = BRepBuilderAPI_MakeVertex(starts[moldExitTransitionIndex]).Vertex();
        BRepExtrema_DistShapeShape exitDistance(exitVertex, document.shape);
        exitDistance.Perform();
        if (!exitDistance.IsDone())
        {
            result.status = NativeConnectionObstacleStatus::GeometryFailure;
            result.diagnostic = "금형 이탈점과 전체 모델 사이 거리를 계산하지 못했습니다.";
            return result;
        }
        if (exitDistance.Value() > connectionToleranceMm)
        {
            result.status = NativeConnectionObstacleStatus::Unresolved;
            result.diagnostic = "명시한 금형 이탈 곡선의 시작점이 전체 모델 표면에 닿지 않습니다.";
            return result;
        }

        double pathPrefixLengthMm = 0.0;
        for (size_t segmentIndex = 0; segmentIndex < wires.size(); ++segmentIndex)
        {
            double edgePrefixLengthMm = 0.0;
            for (BRepTools_WireExplorer explorer(wires[segmentIndex]); explorer.More(); explorer.Next())
            {
                const TopoDS_Edge sourceEdge = explorer.Current();
                double edgeLengthMm = 0.0;
                if (!MeasureEdgeLengthNative(sourceEdge, edgeLengthMm))
                {
                    result.status = NativeConnectionObstacleStatus::Unresolved;
                    result.diagnostic = "연결 경로 구간의 실제 3D 길이를 측정하지 못했습니다.";
                    return result;
                }

                if (segmentIndex >= static_cast<size_t>(moldExitTransitionIndex))
                {
                    BRepAlgoAPI_Common common(sourceEdge, document.shape);
                    common.Build();
                    if (!common.IsDone())
                    {
                        result.status = NativeConnectionObstacleStatus::GeometryFailure;
                        result.diagnostic = "외부 연결 곡선과 전체 금형의 교집합 연산에 실패했습니다.";
                        return result;
                    }

                    std::vector<NativeConnectionObstacleContact> contacts;
                    bool hasOverlappingEdge = false;
                    for (TopExp_Explorer commonEdgeExplorer(common.Shape(), TopAbs_EDGE);
                         commonEdgeExplorer.More(); commonEdgeExplorer.Next())
                    {
                        hasOverlappingEdge = true;
                        const TopoDS_Edge commonEdge = TopoDS::Edge(commonEdgeExplorer.Current());
                        bool hasEndpoint = false;
                        for (TopExp_Explorer vertexExplorer(commonEdge, TopAbs_VERTEX);
                             vertexExplorer.More(); vertexExplorer.Next())
                        {
                            hasEndpoint = true;
                            if (!MeasurePointDistanceAlongEdgeNative(
                                    sourceEdge,
                                    TopoDS::Vertex(vertexExplorer.Current()),
                                    connectionToleranceMm,
                                    contacts))
                            {
                                result.status = NativeConnectionObstacleStatus::Unresolved;
                                result.diagnostic = "외부 곡선과 금형 교집합의 위치를 곡선 매개변수에 대응하지 못했습니다.";
                                return result;
                            }
                        }
                        if (!hasEndpoint)
                        {
                            result.status = NativeConnectionObstacleStatus::Unresolved;
                            result.diagnostic = "외부 곡선과 금형의 닫힌 교집합을 시작 위치로 분류할 수 없습니다.";
                            return result;
                        }
                    }

                    for (TopExp_Explorer commonVertexExplorer(common.Shape(), TopAbs_VERTEX);
                         commonVertexExplorer.More(); commonVertexExplorer.Next())
                    {
                        if (!MeasurePointDistanceAlongEdgeNative(
                                sourceEdge,
                                TopoDS::Vertex(commonVertexExplorer.Current()),
                                connectionToleranceMm,
                                contacts))
                        {
                            result.status = NativeConnectionObstacleStatus::Unresolved;
                            result.diagnostic = "금형 접촉점을 외부 곡선의 거리로 환산하지 못했습니다.";
                            return result;
                        }
                    }

                    std::sort(contacts.begin(), contacts.end(), [](const auto& left, const auto& right)
                    {
                        return left.distanceAlongEdgeMm < right.distanceAlongEdgeMm;
                    });
                    if (hasOverlappingEdge && !contacts.empty())
                    {
                        result.status = NativeConnectionObstacleStatus::Obstructed;
                        result.firstObstruction = contacts.front().point;
                        result.distanceToObstructionMm = pathPrefixLengthMm + edgePrefixLengthMm +
                            contacts.front().distanceAlongEdgeMm;
                        return result;
                    }

                    for (const NativeConnectionObstacleContact& contact : contacts)
                    {
                        const double distanceFromTransitionStartMm = edgePrefixLengthMm + contact.distanceAlongEdgeMm;
                        const bool isAllowedExitContact =
                            segmentIndex == static_cast<size_t>(moldExitTransitionIndex) &&
                            distanceFromTransitionStartMm <= connectionToleranceMm &&
                            contact.point.Distance(starts[moldExitTransitionIndex]) <= connectionToleranceMm;
                        if (isAllowedExitContact)
                            continue;

                        result.status = NativeConnectionObstacleStatus::Obstructed;
                        result.firstObstruction = contact.point;
                        result.distanceToObstructionMm = pathPrefixLengthMm + edgePrefixLengthMm +
                            contact.distanceAlongEdgeMm;
                        return result;
                    }
                }

                edgePrefixLengthMm += edgeLengthMm;
            }

            if (!std::isfinite(edgePrefixLengthMm) || edgePrefixLengthMm <= 0.0)
            {
                result.status = NativeConnectionObstacleStatus::Unresolved;
                result.diagnostic = "연결 경로의 누적 길이를 계산할 수 없습니다.";
                return result;
            }
            pathPrefixLengthMm += edgePrefixLengthMm;
        }

        result.status = NativeConnectionObstacleStatus::Clear;
        return result;
    }
    catch (const Standard_Failure& failure)
    {
        result.status = NativeConnectionObstacleStatus::GeometryFailure;
        const char* failureMessage = failure.GetMessageString();
        result.diagnostic = failureMessage == nullptr
            ? "OCCT 연결 경로 충돌 질의에서 기하 예외가 발생했습니다."
            : failureMessage;
        return result;
    }
    catch (...)
    {
        result.status = NativeConnectionObstacleStatus::GeometryFailure;
        result.diagnostic = "연결 경로 충돌 질의 중 알 수 없는 OCCT 예외가 발생했습니다.";
        return result;
    }
}

