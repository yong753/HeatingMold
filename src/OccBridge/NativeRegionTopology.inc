enum class WorkingFaceAdjacencyStatusNative { Adjacent, NotAdjacent, InvalidFace, StaleContext, GeometryFailure };

bool EnsureFaceAdjacencyNative(NativeDocumentSession& document, long long revision)
{
    if (revision != document.modelRevision)
        return false;
    if (document.faceAdjacencyRevision == revision)
        return true;

    try
    {
        std::set<std::pair<int, int>> adjacency;
        TopTools_IndexedMapOfShape edgeMap;
        std::vector<std::set<int>> owners;

        for (int faceIndex = 0; faceIndex < static_cast<int>(document.faces.size()); ++faceIndex)
        {
            for (TopExp_Explorer edge(document.faces[faceIndex], TopAbs_EDGE); edge.More(); edge.Next())
            {
                const int edgeIndex = edgeMap.Add(edge.Current());
                if (static_cast<int>(owners.size()) < edgeMap.Extent())
                    owners.resize(edgeMap.Extent());
                owners[edgeIndex - 1].insert(faceIndex);
            }
        }

        for (const auto& faces : owners)
        {
            for (auto first = faces.begin(); first != faces.end(); ++first)
            {
                for (auto second = std::next(first); second != faces.end(); ++second)
                    adjacency.emplace(*first, *second);
            }
        }

        document.faceAdjacency.swap(adjacency);
        document.faceAdjacencyRevision = revision;
        return true;
    }
    catch (const Standard_Failure&)
    {
        return false;
    }
}

bool EnsureFaceAdjacencyNative(long long revision)
{
    return EnsureFaceAdjacencyNative(*g_documentSession, revision);
}

std::vector<int> GetAdjacentFaceIndicesNative(const NativeDocumentSession& document, int faceIndex)
{
    std::vector<int> adjacentFaces;
    for (const auto& pair : document.faceAdjacency)
    {
        if (pair.first == faceIndex)
            adjacentFaces.push_back(pair.second);
        else if (pair.second == faceIndex)
            adjacentFaces.push_back(pair.first);
    }
    return adjacentFaces;
}

std::vector<int> GetAdjacentFaceIndicesNative(int faceIndex)
{
    return GetAdjacentFaceIndicesNative(*g_documentSession, faceIndex);
}

WorkingFaceAdjacencyStatusNative QueryWorkingFacesAdjacencyNative(
    NativeDocumentSession& document, long long id, long long revision, int a, int b)
{
    if (!FindWorking(document, id, revision)) return WorkingFaceAdjacencyStatusNative::StaleContext;
    if (a<0 || b<0 || a>=static_cast<int>(document.faces.size()) || b>=static_cast<int>(document.faces.size()))
        return WorkingFaceAdjacencyStatusNative::InvalidFace;
    if (a==b) return WorkingFaceAdjacencyStatusNative::Adjacent;
    if (!EnsureFaceAdjacencyNative(document, revision))
        return WorkingFaceAdjacencyStatusNative::GeometryFailure;
    return document.faceAdjacency.count({std::min(a,b),std::max(a,b)})
        ? WorkingFaceAdjacencyStatusNative::Adjacent
        : WorkingFaceAdjacencyStatusNative::NotAdjacent;
}

WorkingFaceAdjacencyStatusNative QueryWorkingFacesAdjacencyNative(
    long long id, long long revision, int a, int b)
{
    return QueryWorkingFacesAdjacencyNative(*g_documentSession, id, revision, a, b);
}

enum class WorkingTopologyStatusNative
{
    Success,
    InvalidInput,
    InvalidFace,
    StaleContext,
    InvalidTopology,
    GeometryFailure,
    Unknown
};

struct WorkingTopologyBoundaryLoopNative
{
    // 닫힘 중복점을 뺀 XYZ 정점과 각 순환 구간의 소유 Face 번호입니다.
    std::vector<double> points;
    std::vector<int> segmentFaceIndices;
};

struct WorkingTopologyComponentNative
{
    std::vector<int> faceIndices;
    std::vector<WorkingTopologyBoundaryLoopNative> boundaryLoops;
};

struct WorkingTopologyAdjacencyNative
{
    int firstFaceIndex = -1;
    int secondFaceIndex = -1;
};

struct WorkingSelectedTopologyNative
{
    WorkingTopologyStatusNative status = WorkingTopologyStatusNative::InvalidInput;
    std::vector<WorkingTopologyComponentNative> components;
    std::vector<WorkingTopologyAdjacencyNative> adjacencies;
};

WorkingSelectedTopologyNative QueryWorkingSelectedTopologyNative(
    NativeDocumentSession& document,
    long long id,
    long long revision,
    const std::vector<int>& requestedFaceIndices,
    double deflection)
{
    auto failure = [](WorkingTopologyStatusNative status)
    {
        WorkingSelectedTopologyNative result;
        result.status = status;
        return result;
    };

    if (!FindWorking(document, id, revision))
        return failure(WorkingTopologyStatusNative::StaleContext);
    if (requestedFaceIndices.empty() || !std::isfinite(deflection) || deflection <= 0.0)
        return failure(WorkingTopologyStatusNative::InvalidInput);

    std::set<int> selectedFaces;
    for (int faceIndex : requestedFaceIndices)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(document.faces.size()))
            return failure(WorkingTopologyStatusNative::InvalidFace);
        if (!selectedFaces.insert(faceIndex).second)
            return failure(WorkingTopologyStatusNative::InvalidInput);
    }

    try
    {
        // 원본 Face의 Edge 사용 수와 소유 Face를 따로 기록해 seam과 공유 Edge를 구분합니다.
        TopTools_IndexedMapOfShape edgeMap;
        std::vector<int> edgeUseCounts;
        std::vector<std::set<int>> edgeOwners;
        for (int faceIndex : selectedFaces)
        {
            for (TopExp_Explorer explorer(document.faces[faceIndex], TopAbs_EDGE); explorer.More(); explorer.Next())
            {
                TopoDS_Edge edge = TopoDS::Edge(explorer.Current());
                int edgeIndex = edgeMap.FindIndex(edge);
                if (edgeIndex == 0)
                {
                    edgeMap.Add(edge);
                    edgeUseCounts.push_back(0);
                    edgeOwners.emplace_back();
                    edgeIndex = edgeMap.Extent();
                }

                ++edgeUseCounts[edgeIndex - 1];
                edgeOwners[edgeIndex - 1].insert(faceIndex);
            }
        }

        std::vector<std::set<int>> adjacentFaces(document.faces.size());
        std::set<std::pair<int, int>> adjacencyPairs;
        for (int edgeIndex = 0; edgeIndex < edgeMap.Extent(); ++edgeIndex)
        {
            const int useCount = edgeUseCounts[edgeIndex];
            const std::set<int>& owners = edgeOwners[edgeIndex];
            if (useCount < 1 || useCount > 2 || owners.empty() || owners.size() > 2)
                return failure(WorkingTopologyStatusNative::InvalidTopology);

            if (useCount == 1 && owners.size() != 1)
                return failure(WorkingTopologyStatusNative::InvalidTopology);
            if (useCount == 2 && owners.size() == 2)
            {
                auto owner = owners.begin();
                const int first = *owner++;
                const int second = *owner;
                const auto orderedPair = std::minmax(first, second);
                adjacencyPairs.insert(orderedPair);
                adjacentFaces[first].insert(second);
                adjacentFaces[second].insert(first);
            }
        }

        WorkingSelectedTopologyNative result;
        result.status = WorkingTopologyStatusNative::Success;
        for (const auto& pair : adjacencyPairs)
            result.adjacencies.push_back({ pair.first, pair.second });

        // 선택 Face의 실제 공유 Edge 그래프를 따라 서로 떨어진 영역을 나눕니다.
        std::set<int> unvisitedFaces = selectedFaces;
        while (!unvisitedFaces.empty())
        {
            std::vector<int> pending{ *unvisitedFaces.begin() };
            std::set<int> componentFaces;
            unvisitedFaces.erase(pending.front());
            while (!pending.empty())
            {
                const int faceIndex = pending.back();
                pending.pop_back();
                componentFaces.insert(faceIndex);
                for (int adjacent : adjacentFaces[faceIndex])
                {
                    auto found = unvisitedFaces.find(adjacent);
                    if (found != unvisitedFaces.end())
                    {
                        pending.push_back(adjacent);
                        unvisitedFaces.erase(found);
                    }
                }
            }

            WorkingTopologyComponentNative component;
            component.faceIndices.assign(componentFaces.begin(), componentFaces.end());

            std::vector<TopoDS_Edge> boundaryEdges;
            for (int edgeIndex = 0; edgeIndex < edgeMap.Extent(); ++edgeIndex)
            {
                if (edgeUseCounts[edgeIndex] == 1 && componentFaces.count(*edgeOwners[edgeIndex].begin()) > 0)
                    boundaryEdges.push_back(TopoDS::Edge(edgeMap(edgeIndex + 1)));
            }
            if (boundaryEdges.empty())
                return failure(WorkingTopologyStatusNative::InvalidTopology);

            TopTools_IndexedMapOfShape vertexMap;
            std::vector<std::vector<int>> incidentEdges;
            auto addIncidentEdge = [&](const TopoDS_Vertex& vertex, int edgeIndex)
            {
                if (vertex.IsNull())
                    return false;
                int vertexIndex = vertexMap.FindIndex(vertex);
                if (vertexIndex == 0)
                {
                    vertexMap.Add(vertex);
                    incidentEdges.emplace_back();
                    vertexIndex = vertexMap.Extent();
                }
                incidentEdges[vertexIndex - 1].push_back(edgeIndex);
                return true;
            };

            for (int edgeIndex = 0; edgeIndex < static_cast<int>(boundaryEdges.size()); ++edgeIndex)
            {
                TopoDS_Vertex first, last;
                TopExp::Vertices(boundaryEdges[edgeIndex], first, last, Standard_True);
                if (!addIncidentEdge(first, edgeIndex) || !addIncidentEdge(last, edgeIndex))
                    return failure(WorkingTopologyStatusNative::InvalidTopology);
            }
            for (const auto& edgesAtVertex : incidentEdges)
            {
                if (edgesAtVertex.size() != 2)
                    return failure(WorkingTopologyStatusNative::InvalidTopology);
            }

            std::vector<bool> visitedEdges(boundaryEdges.size(), false);
            for (int seed = 0; seed < static_cast<int>(boundaryEdges.size()); ++seed)
            {
                if (visitedEdges[seed])
                    continue;

                std::vector<int> loopEdgeIndices;
                std::vector<int> pendingEdges{ seed };
                visitedEdges[seed] = true;
                while (!pendingEdges.empty())
                {
                    const int edgeIndex = pendingEdges.back();
                    pendingEdges.pop_back();
                    loopEdgeIndices.push_back(edgeIndex);

                    TopoDS_Vertex ends[2];
                    TopExp::Vertices(boundaryEdges[edgeIndex], ends[0], ends[1], Standard_True);
                    for (const TopoDS_Vertex& vertex : ends)
                    {
                        const int vertexIndex = vertexMap.FindIndex(vertex);
                        if (vertexIndex <= 0)
                            return failure(WorkingTopologyStatusNative::InvalidTopology);
                        for (int adjacentEdge : incidentEdges[vertexIndex - 1])
                        {
                            if (!visitedEdges[adjacentEdge])
                            {
                                visitedEdges[adjacentEdge] = true;
                                pendingEdges.push_back(adjacentEdge);
                            }
                        }
                    }
                }

                BRepBuilderAPI_MakeWire wireBuilder;
                for (int edgeIndex : loopEdgeIndices)
                    wireBuilder.Add(boundaryEdges[edgeIndex]);
                if (!wireBuilder.IsDone())
                    return failure(WorkingTopologyStatusNative::InvalidTopology);

                const TopoDS_Wire wire = wireBuilder.Wire();
                if (!BRep_Tool::IsClosed(wire))
                    return failure(WorkingTopologyStatusNative::InvalidTopology);

                size_t orderedEdgeCount = 0;
                for (BRepTools_WireExplorer check(wire); check.More(); check.Next())
                    ++orderedEdgeCount;
                if (orderedEdgeCount != loopEdgeIndices.size())
                    return failure(WorkingTopologyStatusNative::InvalidTopology);

                std::vector<gp_Pnt> loopPoints;
                WorkingTopologyBoundaryLoopNative loop;
                for (BRepTools_WireExplorer explorer(wire); explorer.More(); explorer.Next())
                {
                    const TopoDS_Edge edge = explorer.Current();
                    const int globalEdgeIndex = edgeMap.FindIndex(edge);
                    if (globalEdgeIndex <= 0 || edgeOwners[globalEdgeIndex - 1].size() != 1)
                        return failure(WorkingTopologyStatusNative::InvalidTopology);
                    const int sourceFaceIndex = *edgeOwners[globalEdgeIndex - 1].begin();

                    BRepAdaptor_Curve curve(edge);
                    GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                    if (!sampler.IsDone() || sampler.NbPoints() < 2 || sampler.NbPoints() > 100000)
                        return failure(WorkingTopologyStatusNative::GeometryFailure);

                    const bool reversed = edge.Orientation() == TopAbs_REVERSED;
                    for (int pointIndex = 1; pointIndex <= sampler.NbPoints(); ++pointIndex)
                    {
                        const gp_Pnt point = sampler.Value(
                            reversed ? sampler.NbPoints() - pointIndex + 1 : pointIndex);
                        if (!std::isfinite(point.X()) || !std::isfinite(point.Y()) || !std::isfinite(point.Z()))
                            return failure(WorkingTopologyStatusNative::GeometryFailure);

                        if (loopPoints.empty())
                        {
                            loopPoints.push_back(point);
                            continue;
                        }

                        const double distance = loopPoints.back().Distance(point);
                        if (distance <= Precision::Confusion())
                            continue;

                        loop.segmentFaceIndices.push_back(sourceFaceIndex);
                        loopPoints.push_back(point);
                    }
                }

                if (loopPoints.size() < 4 ||
                    loopPoints.back().Distance(loopPoints.front()) > std::max(1e-6, deflection * 0.01))
                {
                    return failure(WorkingTopologyStatusNative::InvalidTopology);
                }

                loopPoints.pop_back(); // 마지막 점은 첫 정점과 같은 닫힘 표식입니다.
                if (loopPoints.size() < 3 || loop.segmentFaceIndices.size() != loopPoints.size())
                    return failure(WorkingTopologyStatusNative::InvalidTopology);

                loop.points.reserve(loopPoints.size() * 3);
                for (const gp_Pnt& point : loopPoints)
                {
                    loop.points.push_back(point.X());
                    loop.points.push_back(point.Y());
                    loop.points.push_back(point.Z());
                }
                component.boundaryLoops.push_back(std::move(loop));
            }

            if (component.boundaryLoops.empty())
                return failure(WorkingTopologyStatusNative::InvalidTopology);
            result.components.push_back(std::move(component));
        }

        if (result.components.empty())
            return failure(WorkingTopologyStatusNative::InvalidTopology);
        return result;
    }
    catch (const Standard_Failure&)
    {
        return failure(WorkingTopologyStatusNative::GeometryFailure);
    }
    catch (const std::exception&)
    {
        return failure(WorkingTopologyStatusNative::GeometryFailure);
    }
}

enum class WorkingSurfaceElementsStatusNative
{
    Success,
    InvalidInput,
    InvalidFace,
    StaleContext,
    InvalidRegion,
    ElementBudgetExceeded,
    GeometryFailure,
    Unknown
};

struct WorkingRegionLoopNative
{
    std::vector<double> points;
    bool isHole = false;
};

struct WorkingSurfaceElementNative
{
    int faceIndex = -1;
    double surfaceAreaMm2 = 0.0;
    std::vector<double> modelCentroid;
    std::vector<double> faceUvCentroid;
    std::vector<double> workingUvCentroid;
    std::vector<double> modelVertices;
    double maximumMeasuredDeviationMm = 0.0;
};

struct WorkingSurfaceElementsNative
{
    WorkingSurfaceElementsStatusNative status = WorkingSurfaceElementsStatusNative::InvalidInput;
    std::vector<WorkingSurfaceElementNative> elements;
    double areaErrorBoundMm2 = 0.0;
};

enum class WorkingTriangleVisibilityNative
{
    Visible,
    Hidden,
    Boundary,
    Failure
};

double SignedArea2d(const std::vector<double>& points)
{
    double twiceArea = 0.0;
    const size_t count = points.size() / 2;
    for (size_t index = 0; index < count; ++index)
    {
        const size_t current = index * 2;
        const size_t next = ((index + 1) % count) * 2;
        twiceArea += points[current] * points[next + 1] - points[next] * points[current + 1];
    }
    return twiceArea * 0.5;
}

bool IsPointInsideLoop(double x, double y, const std::vector<double>& points)
{
    bool inside = false;
    const size_t count = points.size() / 2;
    for (size_t current = 0, previous = count - 1; current < count; previous = current++)
    {
        const double currentX = points[current * 2];
        const double currentY = points[current * 2 + 1];
        const double previousX = points[previous * 2];
        const double previousY = points[previous * 2 + 1];
        const bool crosses = (currentY > y) != (previousY > y);
        if (crosses && x < (previousX - currentX) * (y - currentY) /
                (previousY - currentY) + currentX)
        {
            inside = !inside;
        }
    }
    return inside;
}

double MeasureTriangleDeviation(
    const Handle(Geom_Surface)& surface,
    const TopLoc_Location& surfaceLocation,
    const gp_Pnt2d& firstUv,
    const gp_Pnt2d& secondUv,
    const gp_Pnt2d& thirdUv,
    const gp_Pnt& first,
    const gp_Pnt& second,
    const gp_Pnt& third)
{
    auto measure = [&surface, &surfaceLocation](double u, double v, const gp_Pnt& meshPoint)
    {
        gp_Pnt surfacePoint;
        surface->D0(u, v, surfacePoint);
        surfacePoint.Transform(surfaceLocation.Transformation());
        return surfacePoint.Distance(meshPoint);
    };

    const gp_Pnt firstSecondMidpoint(
        (first.X() + second.X()) * 0.5,
        (first.Y() + second.Y()) * 0.5,
        (first.Z() + second.Z()) * 0.5);
    const gp_Pnt secondThirdMidpoint(
        (second.X() + third.X()) * 0.5,
        (second.Y() + third.Y()) * 0.5,
        (second.Z() + third.Z()) * 0.5);
    const gp_Pnt thirdFirstMidpoint(
        (third.X() + first.X()) * 0.5,
        (third.Y() + first.Y()) * 0.5,
        (third.Z() + first.Z()) * 0.5);
    const gp_Pnt triangleCentroid(
        (first.X() + second.X() + third.X()) / 3.0,
        (first.Y() + second.Y() + third.Y()) / 3.0,
        (first.Z() + second.Z() + third.Z()) / 3.0);

    double maximumDeviation = 0.0;
    maximumDeviation = std::max(maximumDeviation, measure(
        (firstUv.X() + secondUv.X()) * 0.5,
        (firstUv.Y() + secondUv.Y()) * 0.5,
        firstSecondMidpoint));
    maximumDeviation = std::max(maximumDeviation, measure(
        (secondUv.X() + thirdUv.X()) * 0.5,
        (secondUv.Y() + thirdUv.Y()) * 0.5,
        secondThirdMidpoint));
    maximumDeviation = std::max(maximumDeviation, measure(
        (thirdUv.X() + firstUv.X()) * 0.5,
        (thirdUv.Y() + firstUv.Y()) * 0.5,
        thirdFirstMidpoint));
    maximumDeviation = std::max(maximumDeviation, measure(
        (firstUv.X() + secondUv.X() + thirdUv.X()) / 3.0,
        (firstUv.Y() + secondUv.Y() + thirdUv.Y()) / 3.0,
        triangleCentroid));
    return maximumDeviation;
}

gp_Pnt EvaluateSurfacePoint(
    const Handle(Geom_Surface)& surface,
    const TopLoc_Location& surfaceLocation,
    const gp_Pnt2d& uv)
{
    gp_Pnt point;
    surface->D0(uv.X(), uv.Y(), point);
    point.Transform(surfaceLocation.Transformation());
    return point;
}

WorkingTriangleVisibilityNative ClassifyWorkingTriangleVisibility(
    long long id,
    long long revision,
    const WorkingNative& context,
    int sourceFaceIndex,
    const Handle(Geom_Surface)& surface,
    const TopLoc_Location& surfaceLocation,
    const gp_Pnt2d& firstUv,
    const gp_Pnt2d& secondUv,
    const gp_Pnt2d& thirdUv)
{
    constexpr double probes[7][3] = {
        {0.8, 0.1, 0.1}, {0.1, 0.8, 0.1}, {0.1, 0.1, 0.8},
        {0.5, 0.5, 0.0}, {0.0, 0.5, 0.5}, {0.5, 0.0, 0.5},
        {1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0}
    };
    bool foundVisible = false;
    bool foundHidden = false;
    bool foundTangent = false;
    for (const auto& weights : probes)
    {
        const gp_Pnt2d sampleUv(
            firstUv.X() * weights[0] + secondUv.X() * weights[1] + thirdUv.X() * weights[2],
            firstUv.Y() * weights[0] + secondUv.Y() * weights[1] + thirdUv.Y() * weights[2]);
        const gp_Pnt samplePoint = EvaluateSurfacePoint(surface, surfaceLocation, sampleUv);
        const gp_Vec offset(context.origin, samplePoint);
        const double workingU = offset.Dot(gp_Vec(context.x));
        const double workingV = offset.Dot(gp_Vec(context.y));
        double hitPoint[3] = {};
        int hitFaceIndex = -1;
        const WorkingStatusNative hitStatus = QueryWorkingNative(
            id,
            revision,
            workingU,
            workingV,
            hitPoint,
            hitFaceIndex);
        if (hitStatus != WorkingStatusNative::Hit && hitStatus != WorkingStatusNative::Tangent)
            return WorkingTriangleVisibilityNative::Failure;
        foundTangent = foundTangent || hitStatus == WorkingStatusNative::Tangent;

        if (hitFaceIndex == sourceFaceIndex)
            foundVisible = true;
        else
            foundHidden = true;
        if (foundVisible && foundHidden)
            return WorkingTriangleVisibilityNative::Boundary;
    }

    // 접선 표본은 곡면 가시 경계일 수 있으므로 실패가 아닌 세분화 대상으로 둔다.
    if (foundTangent)
        return WorkingTriangleVisibilityNative::Boundary;

    return foundVisible
        ? WorkingTriangleVisibilityNative::Visible
        : WorkingTriangleVisibilityNative::Hidden;
}

bool BuildRegionPrisms(
    const WorkingNative& context,
    const std::vector<WorkingRegionLoopNative>& loops,
    std::vector<TopoDS_Shape>& prisms)
{
    std::vector<size_t> outerIndices;
    std::vector<size_t> holeIndices;
    for (size_t index = 0; index < loops.size(); ++index)
        (loops[index].isHole ? holeIndices : outerIndices).push_back(index);
    if (outerIndices.empty())
        return false;

    std::vector<std::vector<size_t>> holesByOuter(outerIndices.size());
    for (size_t holeIndex : holeIndices)
    {
        const std::vector<double>& hole = loops[holeIndex].points;
        const double sampleX = hole[0];
        const double sampleY = hole[1];
        size_t smallestContainingOuter = outerIndices.size();
        double smallestArea = std::numeric_limits<double>::max();
        for (size_t outerPosition = 0; outerPosition < outerIndices.size(); ++outerPosition)
        {
            const std::vector<double>& outer = loops[outerIndices[outerPosition]].points;
            const double area = std::fabs(SignedArea2d(outer));
            if (area < smallestArea && IsPointInsideLoop(sampleX, sampleY, outer))
            {
                smallestContainingOuter = outerPosition;
                smallestArea = area;
            }
        }
        if (smallestContainingOuter == outerIndices.size())
            return false;
        holesByOuter[smallestContainingOuter].push_back(holeIndex);
    }

    const gp_Ax3 workPlane(context.origin, context.n, context.x);
    const gp_Pln plane(workPlane);
    const gp_Vec extrusionDirection = gp_Vec(context.n.Reversed()) * context.rayLength;
    if (extrusionDirection.SquareMagnitude() <= Precision::SquareConfusion())
        return false;

    auto makeWire = [&context](const WorkingRegionLoopNative& loop, bool wantCounterClockwise)
    {
        std::vector<size_t> order(loop.points.size() / 2);
        for (size_t index = 0; index < order.size(); ++index)
            order[index] = index;
        const bool isCounterClockwise = SignedArea2d(loop.points) > 0.0;
        if (isCounterClockwise != wantCounterClockwise)
            std::reverse(order.begin(), order.end());

        BRepBuilderAPI_MakePolygon polygon;
        for (size_t index : order)
        {
            const gp_Pnt point = context.origin.Translated(
                gp_Vec(context.x) * loop.points[index * 2] +
                gp_Vec(context.y) * loop.points[index * 2 + 1]);
            polygon.Add(point);
        }
        polygon.Close();
        return polygon;
    };

    for (size_t outerPosition = 0; outerPosition < outerIndices.size(); ++outerPosition)
    {
        BRepBuilderAPI_MakePolygon outerPolygon = makeWire(loops[outerIndices[outerPosition]], true);
        if (!outerPolygon.IsDone() || !BRep_Tool::IsClosed(outerPolygon.Wire()))
            return false;

        BRepBuilderAPI_MakeFace faceBuilder(plane, outerPolygon.Wire(), Standard_True);
        if (!faceBuilder.IsDone())
            return false;
        for (size_t holeIndex : holesByOuter[outerPosition])
        {
            BRepBuilderAPI_MakePolygon holePolygon = makeWire(loops[holeIndex], false);
            if (!holePolygon.IsDone() || !BRep_Tool::IsClosed(holePolygon.Wire()))
                return false;
            faceBuilder.Add(holePolygon.Wire());
            if (!faceBuilder.IsDone())
                return false;
        }

        if (!BRepCheck_Analyzer(faceBuilder.Face(), Standard_True).IsValid())
            return false;

        BRepPrimAPI_MakePrism prismBuilder(faceBuilder.Face(), extrusionDirection, Standard_False, Standard_True);
        if (!prismBuilder.IsDone() || prismBuilder.Shape().IsNull())
            return false;
        prisms.push_back(prismBuilder.Shape());
    }
    return !prisms.empty();
}

WorkingSurfaceElementsNative QueryWorkingSurfaceElementsNative(
    NativeDocumentSession& document,
    long long id,
    long long revision,
    const std::vector<int>& requestedFaceIndices,
    const std::vector<WorkingRegionLoopNative>& effectiveBoundary,
    double deflectionMm,
    double angularToleranceRad)
{
    auto failure = [](WorkingSurfaceElementsStatusNative status)
    {
        WorkingSurfaceElementsNative result;
        result.status = status;
        return result;
    };

    WorkingNative* context = FindWorking(document, id, revision);
    if (!context)
        return failure(WorkingSurfaceElementsStatusNative::StaleContext);
    if (requestedFaceIndices.empty() || effectiveBoundary.empty() ||
        !std::isfinite(deflectionMm) || deflectionMm <= 0.0 ||
        !std::isfinite(angularToleranceRad) || angularToleranceRad <= 0.0)
    {
        return failure(WorkingSurfaceElementsStatusNative::InvalidInput);
    }

    std::set<int> selectedFaces;
    for (int faceIndex : requestedFaceIndices)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(document.faces.size()))
            return failure(WorkingSurfaceElementsStatusNative::InvalidFace);
        if (!selectedFaces.insert(faceIndex).second)
            return failure(WorkingSurfaceElementsStatusNative::InvalidInput);
    }
    for (const WorkingRegionLoopNative& loop : effectiveBoundary)
    {
        if (loop.points.size() < 6 || loop.points.size() % 2 != 0)
            return failure(WorkingSurfaceElementsStatusNative::InvalidInput);
        for (double value : loop.points)
            if (!std::isfinite(value))
                return failure(WorkingSurfaceElementsStatusNative::InvalidInput);
        if (std::fabs(SignedArea2d(loop.points)) <= Precision::SquareConfusion())
            return failure(WorkingSurfaceElementsStatusNative::InvalidRegion);
    }

    try
    {
        std::vector<TopoDS_Shape> regionPrisms;
        if (!BuildRegionPrisms(*context, effectiveBoundary, regionPrisms))
            return failure(WorkingSurfaceElementsStatusNative::InvalidRegion);

        WorkingSurfaceElementsNative result;
        result.status = WorkingSurfaceElementsStatusNative::Success;
        std::map<int, double> exactAreaByFace;
        std::map<int, double> refinedMeshAreaByFace;
        constexpr size_t MaximumSurfaceElementCount = 500000;
        constexpr size_t MaximumTriangleEvaluationCount = 4000000;
        constexpr int MinimumHiddenInspectionDepth = 4;
        constexpr int MaximumVisibilitySubdivisionDepth = 12;
        size_t evaluatedTriangleCount = 0;
        double visibilityBoundaryErrorAreaMm2 = 0.0;
        bool geometryFailure = false;
        bool elementBudgetExceeded = false;

        for (int faceIndex : selectedFaces)
        {
            const TopoDS_Face& sourceFace = document.faces[faceIndex];
            for (const TopoDS_Shape& regionPrism : regionPrisms)
            {
                BRepAlgoAPI_Common intersection(sourceFace, regionPrism);
                intersection.Build();
                if (!intersection.IsDone())
                    return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);

                const TopoDS_Shape clippedShape = intersection.Shape();
                if (clippedShape.IsNull())
                    continue;
                for (TopExp_Explorer faceExplorer(clippedShape, TopAbs_FACE); faceExplorer.More(); faceExplorer.Next())
                {
                    const TopoDS_Face clippedFace = TopoDS::Face(faceExplorer.Current());
                    BRepMesh_IncrementalMesh mesher(
                        clippedFace,
                        deflectionMm,
                        Standard_False,
                        angularToleranceRad,
                        Standard_True);
                    if (!mesher.IsDone())
                        return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);

                    TopLoc_Location location;
                    Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(clippedFace, location);
                    if (triangulation.IsNull() || !triangulation->HasUVNodes())
                        return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);
                    TopLoc_Location surfaceLocation;
                    Handle(Geom_Surface) surface = BRep_Tool::Surface(clippedFace, surfaceLocation);
                    if (surface.IsNull())
                        return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);

                    GProp_GProps exactProperties;
                    BRepGProp::SurfaceProperties(clippedFace, exactProperties, Standard_True, Standard_False);
                    exactAreaByFace[faceIndex] += std::fabs(exactProperties.Mass());

                    auto makePoint = [&surface, &surfaceLocation](const gp_Pnt2d& uv)
                    {
                        return EvaluateSurfacePoint(surface, surfaceLocation, uv);
                    };
                    std::function<void(
                        const gp_Pnt2d&, const gp_Pnt2d&, const gp_Pnt2d&,
                        const gp_Pnt&, const gp_Pnt&, const gp_Pnt&, int)> processTriangle;
                    processTriangle = [&](
                        const gp_Pnt2d& firstUv,
                        const gp_Pnt2d& secondUv,
                        const gp_Pnt2d& thirdUv,
                        const gp_Pnt& first,
                        const gp_Pnt& second,
                        const gp_Pnt& third,
                        int subdivisionDepth)
                    {
                        if (geometryFailure || elementBudgetExceeded)
                            return;
                        if (++evaluatedTriangleCount > MaximumTriangleEvaluationCount)
                        {
                            elementBudgetExceeded = true;
                            return;
                        }

                        const double triangleArea = 0.5 *
                            gp_Vec(first, second).Crossed(gp_Vec(first, third)).Magnitude();
                        if (!std::isfinite(triangleArea))
                        {
                            geometryFailure = true;
                            return;
                        }
                        if (triangleArea <= Precision::SquareConfusion())
                            return;

                        const WorkingTriangleVisibilityNative visibility = ClassifyWorkingTriangleVisibility(
                            id,
                            revision,
                            *context,
                            faceIndex,
                            surface,
                            surfaceLocation,
                            firstUv,
                            secondUv,
                            thirdUv);
                        if (visibility == WorkingTriangleVisibilityNative::Failure)
                        {
                            geometryFailure = true;
                            return;
                        }

                        const bool refineVisibilityBoundary =
                            visibility == WorkingTriangleVisibilityNative::Boundary &&
                            subdivisionDepth < MaximumVisibilitySubdivisionDepth;
                        const bool inspectHiddenInterior =
                            visibility == WorkingTriangleVisibilityNative::Hidden &&
                            subdivisionDepth < MinimumHiddenInspectionDepth;
                        if (refineVisibilityBoundary || inspectHiddenInterior)
                        {
                            const gp_Pnt2d firstSecondUv(
                                (firstUv.X() + secondUv.X()) * 0.5,
                                (firstUv.Y() + secondUv.Y()) * 0.5);
                            const gp_Pnt2d secondThirdUv(
                                (secondUv.X() + thirdUv.X()) * 0.5,
                                (secondUv.Y() + thirdUv.Y()) * 0.5);
                            const gp_Pnt2d thirdFirstUv(
                                (thirdUv.X() + firstUv.X()) * 0.5,
                                (thirdUv.Y() + firstUv.Y()) * 0.5);
                            const gp_Pnt firstSecond = makePoint(firstSecondUv);
                            const gp_Pnt secondThird = makePoint(secondThirdUv);
                            const gp_Pnt thirdFirst = makePoint(thirdFirstUv);

                            processTriangle(firstUv, firstSecondUv, thirdFirstUv,
                                first, firstSecond, thirdFirst, subdivisionDepth + 1);
                            processTriangle(firstSecondUv, secondUv, secondThirdUv,
                                firstSecond, second, secondThird, subdivisionDepth + 1);
                            processTriangle(thirdFirstUv, secondThirdUv, thirdUv,
                                thirdFirst, secondThird, third, subdivisionDepth + 1);
                            processTriangle(firstSecondUv, secondThirdUv, thirdFirstUv,
                                firstSecond, secondThird, thirdFirst, subdivisionDepth + 1);
                            return;
                        }

                        refinedMeshAreaByFace[faceIndex] += triangleArea;
                        if (visibility == WorkingTriangleVisibilityNative::Boundary)
                        {
                            visibilityBoundaryErrorAreaMm2 += triangleArea;
                            return;
                        }
                        if (visibility == WorkingTriangleVisibilityNative::Hidden)
                            return;
                        if (result.elements.size() >= MaximumSurfaceElementCount)
                        {
                            elementBudgetExceeded = true;
                            return;
                        }

                        const double measuredDeviation = MeasureTriangleDeviation(
                            surface,
                            surfaceLocation,
                            firstUv,
                            secondUv,
                            thirdUv,
                            first,
                            second,
                            third);
                        if (!std::isfinite(measuredDeviation))
                        {
                            geometryFailure = true;
                            return;
                        }

                        const gp_Pnt centroid(
                            (first.X() + second.X() + third.X()) / 3.0,
                            (first.Y() + second.Y() + third.Y()) / 3.0,
                            (first.Z() + second.Z() + third.Z()) / 3.0);
                        const gp_Vec fromOrigin(context->origin, centroid);
                        WorkingSurfaceElementNative element;
                        element.faceIndex = faceIndex;
                        element.surfaceAreaMm2 = triangleArea;
                        element.modelCentroid = {centroid.X(), centroid.Y(), centroid.Z()};
                        element.faceUvCentroid = {
                            (firstUv.X() + secondUv.X() + thirdUv.X()) / 3.0,
                            (firstUv.Y() + secondUv.Y() + thirdUv.Y()) / 3.0};
                        element.workingUvCentroid = {
                            fromOrigin.Dot(gp_Vec(context->x)),
                            fromOrigin.Dot(gp_Vec(context->y))};
                        element.modelVertices = {
                            first.X(), first.Y(), first.Z(),
                            second.X(), second.Y(), second.Z(),
                            third.X(), third.Y(), third.Z()};
                        element.maximumMeasuredDeviationMm = measuredDeviation;
                        result.elements.push_back(std::move(element));
                    };

                    for (int triangleIndex = 1; triangleIndex <= triangulation->NbTriangles(); ++triangleIndex)
                    {
                        int firstIndex = 0;
                        int secondIndex = 0;
                        int thirdIndex = 0;
                        triangulation->Triangle(triangleIndex).Get(firstIndex, secondIndex, thirdIndex);
                        const gp_Pnt first = triangulation->Node(firstIndex).Transformed(location.Transformation());
                        const gp_Pnt second = triangulation->Node(secondIndex).Transformed(location.Transformation());
                        const gp_Pnt third = triangulation->Node(thirdIndex).Transformed(location.Transformation());
                        const gp_Pnt2d firstUv = triangulation->UVNode(firstIndex);
                        const gp_Pnt2d secondUv = triangulation->UVNode(secondIndex);
                        const gp_Pnt2d thirdUv = triangulation->UVNode(thirdIndex);
                        processTriangle(firstUv, secondUv, thirdUv,
                            first, second, third, 0);
                        if (geometryFailure)
                            return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);
                        if (elementBudgetExceeded)
                            return failure(WorkingSurfaceElementsStatusNative::ElementBudgetExceeded);
                    }
                }
            }
        }

        if (result.elements.empty())
            return failure(WorkingSurfaceElementsStatusNative::InvalidRegion);
        for (int faceIndex : selectedFaces)
        {
            const double exactArea = exactAreaByFace[faceIndex];
            const double refinedMeshArea = refinedMeshAreaByFace[faceIndex];
            result.areaErrorBoundMm2 += std::fabs(exactArea - refinedMeshArea);
        }
        result.areaErrorBoundMm2 += visibilityBoundaryErrorAreaMm2;
        if (!std::isfinite(result.areaErrorBoundMm2))
            return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);
        return result;
    }
    catch (const Standard_Failure&)
    {
        return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);
    }
    catch (const std::exception&)
    {
        return failure(WorkingSurfaceElementsStatusNative::GeometryFailure);
    }
}

