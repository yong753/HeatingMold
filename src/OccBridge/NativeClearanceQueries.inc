enum class NativeCurveClearanceStatus
{
    Success,
    InvalidInput,
    StaleContext,
    PairBudgetExceeded,
    GeometryFailure,
    Unresolved
};

/// <summary>곡선 간격 결과를 완전한 값으로 확정하지 못한 원인입니다.</summary>
enum class NativeCurveClearanceUnresolvedReason
{
    None,
    BSplineSelfContactNotCertified,
    CurvedEndpointContactNotCertified,
    UnclassifiedCurveEndpointContact,
    DeclaredCurvePairHasUncertifiedContact,
    DeclaredCurveEndpointsDoNotMeet,
    RepeatedArcSweepNotCertified,
    NoNonlocalCurvePair,
    NumericalErrorExceedsRequestedBound
};

/// <summary>OCCT 곡선 거리 계산의 최단점과 정밀도 상태입니다.</summary>
struct NativeCurveClearanceMeasurement final
{
    NativeCurveClearanceStatus status = NativeCurveClearanceStatus::Unresolved;
    NativeCurveClearanceUnresolvedReason unresolvedReason = NativeCurveClearanceUnresolvedReason::None;
    double minimumCenterlineDistanceMm = 0.0;
    double errorBoundMm = 0.0;
    gp_Pnt firstLocation;
    gp_Pnt secondLocation;
};

struct NativeCurveClearanceEdge final
{
    TopoDS_Edge edge;
    Bnd_Box bounds;
    gp_Pnt start;
    gp_Pnt end;
    int curveIndex = 0;
    int edgeIndex = 0;
    int edgeCount = 0;
    bool curveClosed = false;
    bool isLinear = false;
    double toleranceMm = 0.0;
};

/// <summary>1차 비주기 B-spline을 정확한 knot 구간 직선 edge로 분해합니다.</summary>
bool AppendLinearBSplineEdgesNative(
    const TopoDS_Wire& wire,
    bool isReversed,
    std::vector<TopoDS_Edge>& edges)
{
    BRepTools_WireExplorer wireExplorer(wire);
    if (!wireExplorer.More())
        return false;

    const TopoDS_Edge sourceEdge = TopoDS::Edge(wireExplorer.Current());
    wireExplorer.Next();
    if (wireExplorer.More())
        return false;

    Standard_Real firstParameter = 0.0;
    Standard_Real lastParameter = 0.0;
    const Handle(Geom_Curve) curve = BRep_Tool::Curve(sourceEdge, firstParameter, lastParameter);
    const Handle(Geom_BSplineCurve) spline = Handle(Geom_BSplineCurve)::DownCast(curve);
    if (spline.IsNull() || !std::isfinite(firstParameter) || !std::isfinite(lastParameter) ||
        firstParameter >= lastParameter)
    {
        return false;
    }

    std::vector<Standard_Real> breakpoints;
    breakpoints.reserve(static_cast<size_t>(spline->NbKnots()) + 2);
    breakpoints.push_back(firstParameter);
    for (Standard_Integer knotIndex = 1; knotIndex <= spline->NbKnots(); ++knotIndex)
    {
        const Standard_Real knot = spline->Knot(knotIndex);
        if (knot > firstParameter && knot < lastParameter)
            breakpoints.push_back(knot);
    }
    breakpoints.push_back(lastParameter);
    if (breakpoints.size() < 2)
        return false;

    std::vector<TopoDS_Edge> orderedEdges;
    orderedEdges.reserve(breakpoints.size() - 1);
    for (size_t intervalIndex = 0; intervalIndex + 1 < breakpoints.size(); ++intervalIndex)
    {
        const size_t orderedIndex = isReversed
            ? breakpoints.size() - 2 - intervalIndex
            : intervalIndex;
        const Standard_Real intervalStart = breakpoints[orderedIndex];
        const Standard_Real intervalEnd = breakpoints[orderedIndex + 1];
        if (!std::isfinite(intervalStart) || !std::isfinite(intervalEnd) || intervalStart >= intervalEnd)
            return false;

        BRepBuilderAPI_MakeEdge edgeBuilder(spline, intervalStart, intervalEnd);
        if (!edgeBuilder.IsDone())
            return false;

        TopoDS_Edge edge = edgeBuilder.Edge();
        if (isReversed)
            edge.Reverse();

        TopoDS_Vertex startVertex;
        TopoDS_Vertex endVertex;
        TopExp::Vertices(edge, startVertex, endVertex, Standard_True);
        if (startVertex.IsNull() || endVertex.IsNull() ||
            BRep_Tool::Pnt(startVertex).Distance(BRep_Tool::Pnt(endVertex)) <= Precision::Confusion() ||
            !BRepCheck_Analyzer(edge).IsValid())
        {
            return false;
        }

        orderedEdges.push_back(std::move(edge));
    }

    edges.insert(edges.end(), orderedEdges.begin(), orderedEdges.end());
    return !orderedEdges.empty();
}

/// <summary>비퇴화 단일 구간 2차 유리 B-spline의 자기교차가 없음을 확인합니다.</summary>
bool IsCertifiedSingleSpanQuadraticBSplineNative(const CurveDefinitionNative& definition)
{
    if (definition.kind != "BSpline" || definition.degree != 2 || definition.isPeriodic ||
        definition.poles.size() != 9 || definition.knots.size() != 2 ||
        definition.multiplicities.size() != 2)
    {
        return false;
    }

    const gp_Pnt firstPole(definition.poles[0], definition.poles[1], definition.poles[2]);
    const gp_Pnt controlPole(definition.poles[3], definition.poles[4], definition.poles[5]);
    const gp_Pnt lastPole(definition.poles[6], definition.poles[7], definition.poles[8]);
    const gp_Vec controlRay(firstPole, controlPole);
    const gp_Vec chord(firstPole, lastPole);
    const double controlLength = controlRay.Magnitude();
    const double chordLength = chord.Magnitude();
    if (!std::isfinite(controlLength) || !std::isfinite(chordLength) ||
        controlLength <= Precision::Confusion() || chordLength <= Precision::Confusion())
    {
        return false;
    }

    const double magnitudeProduct = controlLength * chordLength;
    if (!std::isfinite(magnitudeProduct) || magnitudeProduct <= Precision::SquareConfusion())
        return false;

    const double normalizedArea = controlRay.Crossed(chord).Magnitude() / magnitudeProduct;
    return std::isfinite(normalizedArea) && normalizedArea > 1e-9;
}

double AxisGap(double firstMinimum, double firstMaximum, double secondMinimum, double secondMaximum)
{
    if (firstMaximum < secondMinimum)
        return secondMinimum - firstMaximum;
    if (secondMaximum < firstMinimum)
        return firstMinimum - secondMaximum;
    return 0.0;
}

double AabbDistance(const Bnd_Box& first, const Bnd_Box& second)
{
    double firstXmin, firstYmin, firstZmin, firstXmax, firstYmax, firstZmax;
    double secondXmin, secondYmin, secondZmin, secondXmax, secondYmax, secondZmax;
    first.Get(firstXmin, firstYmin, firstZmin, firstXmax, firstYmax, firstZmax);
    second.Get(secondXmin, secondYmin, secondZmin, secondXmax, secondYmax, secondZmax);
    const double xGap = AxisGap(firstXmin, firstXmax, secondXmin, secondXmax);
    const double yGap = AxisGap(firstYmin, firstYmax, secondYmin, secondYmax);
    const double zGap = AxisGap(firstZmin, firstZmax, secondZmin, secondZmax);
    return std::sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
}

bool ShareEndpoint(
    const NativeCurveClearanceEdge& first,
    const NativeCurveClearanceEdge& second,
    double toleranceMm,
    gp_Pnt& sharedPoint)
{
    const gp_Pnt firstPoints[] = { first.start, first.end };
    const gp_Pnt secondPoints[] = { second.start, second.end };
    for (const gp_Pnt& firstPoint : firstPoints)
    {
        for (const gp_Pnt& secondPoint : secondPoints)
        {
            if (firstPoint.Distance(secondPoint) <= toleranceMm)
            {
                sharedPoint = firstPoint;
                return true;
            }
        }
    }

    return false;
}

bool IsAdjacentCurvePair(
    const NativeCurveClearanceEdge& first,
    const NativeCurveClearanceEdge& second,
    double toleranceMm,
    gp_Pnt& sharedPoint)
{
    if (!ShareEndpoint(first, second, toleranceMm, sharedPoint))
        return false;

    if (first.curveIndex != second.curveIndex)
        return true;

    const int indexDifference = std::abs(first.edgeIndex - second.edgeIndex);
    return indexDifference == 1 ||
        (first.curveClosed && indexDifference == first.edgeCount - 1);
}

bool ContainsCurveIndexPair(
    const std::vector<std::pair<int, int>>& curvePairs,
    int firstCurveIndex,
    int secondCurveIndex)
{
    return std::any_of(curvePairs.begin(), curvePairs.end(), [&](const auto& pair)
    {
        return pair.first == firstCurveIndex && pair.second == secondCurveIndex ||
            pair.first == secondCurveIndex && pair.second == firstCurveIndex;
    });
}

bool IsDeclaredCurveJoint(
    const std::vector<std::pair<int, int>>& curvePairs,
    const std::vector<gp_Pnt>& curveStarts,
    const std::vector<gp_Pnt>& curveEnds,
    int firstCurveIndex,
    int secondCurveIndex,
    const gp_Pnt& sharedPoint,
    double toleranceMm)
{
    for (const auto& pair : curvePairs)
    {
        if (pair.first == firstCurveIndex && pair.second == secondCurveIndex)
        {
            return sharedPoint.Distance(curveEnds[pair.first]) <= toleranceMm &&
                sharedPoint.Distance(curveStarts[pair.second]) <= toleranceMm;
        }

        if (pair.first == secondCurveIndex && pair.second == firstCurveIndex)
        {
            return sharedPoint.Distance(curveEnds[pair.first]) <= toleranceMm &&
                sharedPoint.Distance(curveStarts[pair.second]) <= toleranceMm;
        }
    }

    return false;
}

bool IsCollinearBacktrackAtJoint(
    const NativeCurveClearanceEdge& first,
    const NativeCurveClearanceEdge& second,
    const gp_Pnt& sharedPoint)
{
    const gp_Pnt firstOther = first.start.Distance(sharedPoint) <= first.end.Distance(sharedPoint)
        ? first.end
        : first.start;
    const gp_Pnt secondOther = second.start.Distance(sharedPoint) <= second.end.Distance(sharedPoint)
        ? second.end
        : second.start;
    const gp_Vec firstRay(sharedPoint, firstOther);
    const gp_Vec secondRay(sharedPoint, secondOther);
    const double magnitudeProduct = firstRay.Magnitude() * secondRay.Magnitude();
    if (!std::isfinite(magnitudeProduct) || magnitudeProduct <= Precision::Confusion())
        return true;

    const double dot = firstRay.Dot(secondRay);
    const double sine = firstRay.Crossed(secondRay).Magnitude() / magnitudeProduct;
    return dot > 0.0 && sine <= 1e-9;
}

/// <summary>정확 곡선 wire 사이의 비국소 최소 간격을 OCCT 최단거리로 계산합니다.</summary>
NativeCurveClearanceMeasurement MeasureCurveClearanceNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    const std::vector<CurveDefinitionNative>& definitions,
    const std::vector<std::pair<int, int>>& localCurvePairs,
    double wireDiameterMm,
    double maximumErrorMm,
    int maximumPairChecks)
{
    NativeCurveClearanceMeasurement result;
    if (!FindWorking(document, contextId, revision))
    {
        result.status = NativeCurveClearanceStatus::StaleContext;
        return result;
    }
    if (definitions.empty() || !std::isfinite(wireDiameterMm) || wireDiameterMm <= 0.0 ||
        !std::isfinite(maximumErrorMm) || maximumErrorMm <= 0.0 || maximumPairChecks <= 0)
    {
        result.status = NativeCurveClearanceStatus::InvalidInput;
        return result;
    }

    try
    {
        std::vector<NativeCurveClearanceEdge> edges;
        std::vector<gp_Pnt> curveStarts;
        std::vector<gp_Pnt> curveEnds;
        double coordinateScale = 1.0;
        double maximumShapeToleranceMm = 0.0;

        for (size_t curveIndex = 0; curveIndex < definitions.size(); ++curveIndex)
        {
            const CurveDefinitionNative& definition = definitions[curveIndex];
            if (definition.kind == "Arc" &&
                std::isfinite(definition.startAngleRad) && std::isfinite(definition.endAngleRad) &&
                std::fabs(definition.endAngleRad - definition.startAngleRad) >
                    2.0 * std::acos(-1.0) + Precision::Angular())
            {
                result.status = NativeCurveClearanceStatus::Unresolved;
                result.unresolvedReason = NativeCurveClearanceUnresolvedReason::RepeatedArcSweepNotCertified;
                return result;
            }

            TopoDS_Wire wire;
            if (!BuildCurveWireNative(definition, wire))
            {
                result.status = NativeCurveClearanceStatus::InvalidInput;
                return result;
            }

            const bool isLinearBSpline = definition.kind == "BSpline" &&
                definition.degree == 1 && !definition.isPeriodic;
            const bool isSingleSpanQuadraticBSpline =
                IsCertifiedSingleSpanQuadraticBSplineNative(definition);
            const bool canMeasureBSplineExactly =
                isLinearBSpline || isSingleSpanQuadraticBSpline;
            if (definition.kind == "BSpline" && !canMeasureBSplineExactly)
            {
                result.status = NativeCurveClearanceStatus::Unresolved;
                result.unresolvedReason = NativeCurveClearanceUnresolvedReason::BSplineSelfContactNotCertified;
                return result;
            }

            std::vector<NativeCurveClearanceEdge> curveEdges;
            std::vector<TopoDS_Edge> exactEdges;
            if (isLinearBSpline)
            {
                if (!AppendLinearBSplineEdgesNative(
                        wire,
                        definition.direction == "Reversed",
                        exactEdges))
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }
            }
            else
            {
                for (BRepTools_WireExplorer explorer(wire); explorer.More(); explorer.Next())
                    exactEdges.push_back(TopoDS::Edge(explorer.Current()));
            }

            for (const TopoDS_Edge& edge : exactEdges)
            {
                TopoDS_Vertex startVertex;
                TopoDS_Vertex endVertex;
                TopExp::Vertices(edge, startVertex, endVertex, Standard_True);
                if (startVertex.IsNull() || endVertex.IsNull())
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }

                NativeCurveClearanceEdge measuredEdge;
                measuredEdge.edge = edge;
                measuredEdge.start = BRep_Tool::Pnt(startVertex);
                measuredEdge.end = BRep_Tool::Pnt(endVertex);
                measuredEdge.curveIndex = static_cast<int>(curveIndex);
                measuredEdge.edgeIndex = static_cast<int>(curveEdges.size());
                measuredEdge.toleranceMm = BRep_Tool::Tolerance(edge);
                measuredEdge.isLinear = definition.kind == "Line" ||
                    definition.kind == "Polyline" ||
                    isLinearBSpline;
                BRepBndLib::Add(edge, measuredEdge.bounds, Standard_False);
                if (measuredEdge.bounds.IsVoid() || !std::isfinite(measuredEdge.toleranceMm) ||
                    measuredEdge.toleranceMm < 0.0 ||
                    !std::isfinite(measuredEdge.start.X()) || !std::isfinite(measuredEdge.start.Y()) ||
                    !std::isfinite(measuredEdge.start.Z()) || !std::isfinite(measuredEdge.end.X()) ||
                    !std::isfinite(measuredEdge.end.Y()) || !std::isfinite(measuredEdge.end.Z()))
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }

                measuredEdge.bounds.Enlarge(measuredEdge.toleranceMm);
                maximumShapeToleranceMm = std::max(maximumShapeToleranceMm, measuredEdge.toleranceMm);
                coordinateScale = std::max(coordinateScale, std::max(
                    std::max(std::fabs(measuredEdge.start.X()), std::fabs(measuredEdge.end.X())),
                    std::max(
                        std::max(std::fabs(measuredEdge.start.Y()), std::fabs(measuredEdge.end.Y())),
                        std::max(std::fabs(measuredEdge.start.Z()), std::fabs(measuredEdge.end.Z())))));
                curveEdges.push_back(std::move(measuredEdge));
            }

            if (curveEdges.empty())
            {
                result.status = NativeCurveClearanceStatus::GeometryFailure;
                return result;
            }

            curveStarts.push_back(curveEdges.front().start);
            curveEnds.push_back(curveEdges.back().end);

            const bool isClosed = curveEdges.front().start.Distance(curveEdges.back().end) <= maximumErrorMm;
            for (NativeCurveClearanceEdge& edge : curveEdges)
            {
                edge.edgeCount = static_cast<int>(curveEdges.size());
                edge.curveClosed = isClosed;
                edges.push_back(std::move(edge));
            }
        }

        std::set<std::pair<int, int>> uniqueLocalPairs;
        for (const auto& pair : localCurvePairs)
        {
            if (pair.first < 0 || pair.second < 0 || pair.first == pair.second ||
                pair.first >= static_cast<int>(definitions.size()) ||
                pair.second >= static_cast<int>(definitions.size()) ||
                !uniqueLocalPairs.insert(pair).second)
            {
                result.status = NativeCurveClearanceStatus::InvalidInput;
                return result;
            }

            if (curveEnds[pair.first].Distance(curveStarts[pair.second]) > maximumErrorMm)
            {
                result.status = NativeCurveClearanceStatus::Unresolved;
                result.unresolvedReason = NativeCurveClearanceUnresolvedReason::DeclaredCurveEndpointsDoNotMeet;
                return result;
            }
        }

        const double floatingErrorMm = (coordinateScale + 1.0) *
            std::numeric_limits<double>::epsilon() * 1024.0;
        const double errorBoundMm = std::max(
            std::max(Precision::Confusion(), maximumShapeToleranceMm),
            floatingErrorMm);
        if (!std::isfinite(errorBoundMm) || errorBoundMm > maximumErrorMm)
        {
            result.status = NativeCurveClearanceStatus::Unresolved;
            result.unresolvedReason = NativeCurveClearanceUnresolvedReason::NumericalErrorExceedsRequestedBound;
            result.errorBoundMm = errorBoundMm;
            return result;
        }

        double bestDistanceMm = std::numeric_limits<double>::infinity();
        gp_Pnt bestFirst;
        gp_Pnt bestSecond;
        int visitedPairs = 0;
        for (size_t firstIndex = 0; firstIndex + 1 < edges.size(); ++firstIndex)
        {
            for (size_t secondIndex = firstIndex + 1; secondIndex < edges.size(); ++secondIndex)
            {
                if (++visitedPairs > maximumPairChecks)
                {
                    result.status = NativeCurveClearanceStatus::PairBudgetExceeded;
                    return result;
                }

                const NativeCurveClearanceEdge& first = edges[firstIndex];
                const NativeCurveClearanceEdge& second = edges[secondIndex];
                gp_Pnt sharedPoint;
                if (IsAdjacentCurvePair(first, second, maximumErrorMm, sharedPoint))
                {
                    // 설계가 선언한 이전 끝점과 다음 시작점의 접촉만 국소 이웃으로 제외합니다.
                    if (first.curveIndex != second.curveIndex)
                    {
                        if (!ContainsCurveIndexPair(localCurvePairs, first.curveIndex, second.curveIndex))
                        {
                            result.status = NativeCurveClearanceStatus::Unresolved;
                            result.unresolvedReason = NativeCurveClearanceUnresolvedReason::UnclassifiedCurveEndpointContact;
                            return result;
                        }

                        if (!IsDeclaredCurveJoint(
                            localCurvePairs,
                            curveStarts,
                            curveEnds,
                            first.curveIndex,
                            second.curveIndex,
                            sharedPoint,
                            maximumErrorMm))
                        {
                            result.status = NativeCurveClearanceStatus::Unresolved;
                            result.unresolvedReason = NativeCurveClearanceUnresolvedReason::DeclaredCurvePairHasUncertifiedContact;
                            return result;
                        }
                    }

                    if (!first.isLinear || !second.isLinear)
                    {
                        result.status = NativeCurveClearanceStatus::Unresolved;
                        result.unresolvedReason = NativeCurveClearanceUnresolvedReason::CurvedEndpointContactNotCertified;
                        return result;
                    }

                    if (!IsCollinearBacktrackAtJoint(first, second, sharedPoint))
                        continue;
                }

                const double lowerBoundMm = AabbDistance(first.bounds, second.bounds);
                if (!std::isfinite(lowerBoundMm))
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }
                if (std::isfinite(bestDistanceMm) && lowerBoundMm - errorBoundMm > bestDistanceMm + errorBoundMm)
                    continue;

                BRepExtrema_DistShapeShape distance(first.edge, second.edge);
                if (!distance.IsDone())
                    distance.Perform();
                if (!distance.IsDone() || distance.NbSolution() < 1 ||
                    !std::isfinite(distance.Value()) || distance.Value() < 0.0)
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }

                const gp_Pnt firstPoint = distance.PointOnShape1(1);
                const gp_Pnt secondPoint = distance.PointOnShape2(1);
                if (!std::isfinite(firstPoint.X()) || !std::isfinite(firstPoint.Y()) ||
                    !std::isfinite(firstPoint.Z()) || !std::isfinite(secondPoint.X()) ||
                    !std::isfinite(secondPoint.Y()) || !std::isfinite(secondPoint.Z()))
                {
                    result.status = NativeCurveClearanceStatus::GeometryFailure;
                    return result;
                }

                if (distance.Value() < bestDistanceMm)
                {
                    bestDistanceMm = distance.Value();
                    bestFirst = firstPoint;
                    bestSecond = secondPoint;
                }
            }
        }

        if (!std::isfinite(bestDistanceMm))
        {
            result.status = NativeCurveClearanceStatus::Unresolved;
            result.unresolvedReason = NativeCurveClearanceUnresolvedReason::NoNonlocalCurvePair;
            return result;
        }

        result.status = NativeCurveClearanceStatus::Success;
        result.minimumCenterlineDistanceMm = bestDistanceMm;
        result.errorBoundMm = errorBoundMm;
        result.firstLocation = bestFirst;
        result.secondLocation = bestSecond;
        return result;
    }
    catch (const Standard_Failure&)
    {
        result.status = NativeCurveClearanceStatus::GeometryFailure;
        return result;
    }
}

/// <summary>후보 곡선 표본과 근사 검사의 Native 측정 근거입니다.</summary>
