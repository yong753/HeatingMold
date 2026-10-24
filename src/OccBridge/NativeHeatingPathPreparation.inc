struct NativeCurveApproximation final
{
    std::vector<double> samples;
    double maximumDeviationMm = 0.0;
    double maximumSpacingMm = 0.0;
    int probeCount = 0;
    bool isComplete = false;
};

double DistanceToChord(const gp_Pnt& point, const gp_Pnt& start, const gp_Pnt& end)
{
    const gp_Vec chord(start, end);
    const double chordSquared = chord.SquareMagnitude();
    if (chordSquared <= Precision::Confusion() * Precision::Confusion())
        return point.Distance(start);

    const gp_Vec offset(start, point);
    const double rawParameter = offset.Dot(chord) / chordSquared;
    const double parameter = std::max(0.0, std::min(1.0, rawParameter));
    return point.Distance(start.Translated(chord * parameter));
}

bool TryReadUnitTangent(BRepAdaptor_Curve& curve, double parameter, gp_Vec& tangent)
{
    try
    {
        gp_Pnt point;
        curve.D1(parameter, point, tangent);
        if (!std::isfinite(tangent.SquareMagnitude()) ||
            tangent.SquareMagnitude() <= Precision::Confusion() * Precision::Confusion())
        {
            return false;
        }

        tangent.Normalize();
        return true;
    }
    catch (...)
    {
        return false;
    }
}

bool SampleCurveInterval(
    BRepAdaptor_Curve& curve,
    double startParameter,
    const gp_Pnt& start,
    double endParameter,
    const gp_Pnt& end,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount,
    int depth,
    NativeCurveApproximation& approximation)
{
    if (approximation.probeCount >= maximumProbeCount)
    {
        approximation.isComplete = false;
        approximation.maximumSpacingMm = std::max(approximation.maximumSpacingMm, start.Distance(end));
        approximation.samples.push_back(end.X());
        approximation.samples.push_back(end.Y());
        approximation.samples.push_back(end.Z());
        return true;
    }

    const double middleParameter = (startParameter + endParameter) * 0.5;
    gp_Pnt middle;
    curve.D0(middleParameter, middle);
    ++approximation.probeCount;

    const double deviation = DistanceToChord(middle, start, end);
    const double spacing = start.Distance(end);

    gp_Vec startTangent, middleTangent, endTangent;
    const bool hasTangents = TryReadUnitTangent(curve, startParameter, startTangent) &&
        TryReadUnitTangent(curve, middleParameter, middleTangent) &&
        TryReadUnitTangent(curve, endParameter, endTangent);
    double angularDeviation = 0.0;
    if (hasTangents)
    {
        angularDeviation = std::max(
            startTangent.Angle(middleTangent),
            middleTangent.Angle(endTangent));
    }

    const bool withinLimits = std::isfinite(deviation) &&
        deviation <= requestedDeflectionMm &&
        spacing <= maximumSampleSpacingMm &&
        hasTangents && angularDeviation <= requestedAngularToleranceRad;
    if (withinLimits)
    {
        approximation.maximumDeviationMm = std::max(approximation.maximumDeviationMm, deviation);
        approximation.maximumSpacingMm = std::max(approximation.maximumSpacingMm, spacing);
        approximation.samples.push_back(end.X());
        approximation.samples.push_back(end.Y());
        approximation.samples.push_back(end.Z());
        return true;
    }

    if (depth >= maximumSubdivisionDepth || approximation.probeCount >= maximumProbeCount)
    {
        approximation.isComplete = false;
        approximation.maximumDeviationMm = std::max(approximation.maximumDeviationMm, deviation);
        approximation.maximumSpacingMm = std::max(approximation.maximumSpacingMm, spacing);
        approximation.samples.push_back(end.X());
        approximation.samples.push_back(end.Y());
        approximation.samples.push_back(end.Z());
        return true;
    }

    return SampleCurveInterval(
            curve,
            startParameter,
            start,
            middleParameter,
            middle,
            requestedDeflectionMm,
            requestedAngularToleranceRad,
            maximumSampleSpacingMm,
            maximumSubdivisionDepth,
            maximumProbeCount,
            depth + 1,
            approximation) &&
        SampleCurveInterval(
            curve,
            middleParameter,
            middle,
            endParameter,
            end,
            requestedDeflectionMm,
            requestedAngularToleranceRad,
            maximumSampleSpacingMm,
            maximumSubdivisionDepth,
            maximumProbeCount,
            depth + 1,
            approximation);
}

bool SampleWireForSnapshot(
    const TopoDS_Wire& wire,
    const gp_Pnt& expectedStart,
    const gp_Pnt& expectedEnd,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount,
    NativeCurveApproximation& approximation)
{
    if (wire.IsNull() || !std::isfinite(requestedDeflectionMm) || requestedDeflectionMm <= 0.0 ||
        !std::isfinite(requestedAngularToleranceRad) || requestedAngularToleranceRad <= 0.0 ||
        !std::isfinite(maximumSampleSpacingMm) || maximumSampleSpacingMm <= 0.0 ||
        maximumSubdivisionDepth < 1 || maximumProbeCount < 2)
    {
        return false;
    }

    // OCCT 표본을 초기 구간으로 삼고, 각 구간의 현 편차·접선 변화·간격을 다시 확인합니다.
    approximation = NativeCurveApproximation{};
    approximation.isComplete = true;
    size_t edgeCount = 0;
    for (BRepTools_WireExplorer edgeExplorer(wire); edgeExplorer.More(); edgeExplorer.Next())
    {
        const TopoDS_Edge edge = edgeExplorer.Current();
        BRepAdaptor_Curve curve(edge);
        const double firstParameter = curve.FirstParameter();
        const double lastParameter = curve.LastParameter();
        if (!std::isfinite(firstParameter) || !std::isfinite(lastParameter) || firstParameter == lastParameter)
            return false;

        GCPnts_QuasiUniformDeflection sampler(curve, requestedDeflectionMm);
        if (!sampler.IsDone() || sampler.NbPoints() < 2 ||
            sampler.NbPoints() > maximumProbeCount + 1)
            return false;

        std::vector<double> parameters;
        std::vector<gp_Pnt> points;
        parameters.reserve(static_cast<size_t>(sampler.NbPoints()));
        points.reserve(static_cast<size_t>(sampler.NbPoints()));
        for (int index = 1; index <= sampler.NbPoints(); ++index)
        {
            parameters.push_back(sampler.Parameter(index));
            points.push_back(sampler.Value(index));
        }

        TopoDS_Vertex edgeStart, edgeEnd;
        TopExp::Vertices(edge, edgeStart, edgeEnd, Standard_True);
        if (edgeStart.IsNull() || edgeEnd.IsNull())
            return false;
        const gp_Pnt orientedStart = BRep_Tool::Pnt(edgeStart);
        if (points.front().Distance(orientedStart) > points.back().Distance(orientedStart))
        {
            std::reverse(parameters.begin(), parameters.end());
            std::reverse(points.begin(), points.end());
        }

        if (approximation.samples.empty())
        {
            approximation.samples.push_back(points.front().X());
            approximation.samples.push_back(points.front().Y());
            approximation.samples.push_back(points.front().Z());
        }
        else
        {
            const size_t lastOffset = approximation.samples.size() - 3;
            const gp_Pnt previous(
                approximation.samples[lastOffset],
                approximation.samples[lastOffset + 1],
                approximation.samples[lastOffset + 2]);
            const double gap = previous.Distance(points.front());
            if (!std::isfinite(gap) || gap > 0.01)
                return false;
            if (gap > Precision::Confusion())
            {
                approximation.maximumSpacingMm = std::max(approximation.maximumSpacingMm, gap);
                approximation.samples.push_back(points.front().X());
                approximation.samples.push_back(points.front().Y());
                approximation.samples.push_back(points.front().Z());
                if (gap > maximumSampleSpacingMm)
                    approximation.isComplete = false;
            }
        }

        for (size_t index = 1; index < points.size(); ++index)
        {
            if (approximation.probeCount >= maximumProbeCount)
            {
                approximation.isComplete = false;
                for (size_t remaining = index; remaining < points.size(); ++remaining)
                {
                    approximation.samples.push_back(points[remaining].X());
                    approximation.samples.push_back(points[remaining].Y());
                    approximation.samples.push_back(points[remaining].Z());
                }
                break;
            }

            if (!SampleCurveInterval(
                    curve,
                    parameters[index - 1],
                    points[index - 1],
                    parameters[index],
                    points[index],
                    requestedDeflectionMm,
                    requestedAngularToleranceRad,
                    maximumSampleSpacingMm,
                    maximumSubdivisionDepth,
                    maximumProbeCount,
                    0,
                    approximation))
            {
                return false;
            }
        }

        ++edgeCount;
    }

    if (edgeCount == 0 || approximation.samples.size() < 6)
        return false;

    const gp_Pnt sampleStart(approximation.samples[0], approximation.samples[1], approximation.samples[2]);
    size_t lastOffset = approximation.samples.size() - 3;
    const gp_Pnt sampleEnd(
        approximation.samples[lastOffset],
        approximation.samples[lastOffset + 1],
        approximation.samples[lastOffset + 2]);
    if (sampleStart.Distance(expectedEnd) + sampleEnd.Distance(expectedStart) <
        sampleStart.Distance(expectedStart) + sampleEnd.Distance(expectedEnd))
    {
        for (size_t left = 0, right = approximation.samples.size() - 3; left < right; left += 3, right -= 3)
        {
            for (size_t axis = 0; axis < 3; ++axis)
                std::swap(approximation.samples[left + axis], approximation.samples[right + axis]);
        }
    }

    approximation.samples[0] = expectedStart.X();
    approximation.samples[1] = expectedStart.Y();
    approximation.samples[2] = expectedStart.Z();
    lastOffset = approximation.samples.size() - 3;
    approximation.samples[lastOffset] = expectedEnd.X();
    approximation.samples[lastOffset + 1] = expectedEnd.Y();
    approximation.samples[lastOffset + 2] = expectedEnd.Z();
    return true;
}

bool BuildProjectedWorkingWireFromSource(NativeDocumentSession& document,
    long long id,long long revision,const TopoDS_Wire& sourceWire,
    const gp_Pnt& sourceStart,const gp_Pnt& sourceEnd,const std::vector<int>& faceIds,
    const gp_Dir& direction,TopoDS_Wire& wire,double* result,
    NativeCurveApproximation* approximation = nullptr,
    double requestedDeflectionMm = 0.05,
    double requestedAngularToleranceRad = 0.1,
    double maximumSampleSpacingMm = 2.0,
    int maximumSubdivisionDepth = 18,
    int maximumProbeCount = 100000)
{
    if (!FindWorking(document, id, revision) || sourceWire.IsNull() || faceIds.empty() || !result)
        return false;

    try
    {
        BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
        std::set<int> uniqueFaceIds;
        for (int faceId : faceIds)
        {
            if (faceId < 0 || faceId >= static_cast<int>(document.faces.size()))
                return false;

            if (uniqueFaceIds.insert(faceId).second)
                sewing.Add(document.faces[faceId]);
        }

        sewing.Perform();
        const TopoDS_Shape target = sewing.SewedShape();
        if (target.IsNull())
            return false;

        // 각 원본 edge의 전체 구간이 선택 면으로 투영 가능한지 따로 확인한다.
        IntCurvesFace_ShapeIntersector coverage;
        coverage.Load(target, 0.01);
        size_t sourceEdgeCount = 0;
        for (BRepTools_WireExplorer sourceEdge(sourceWire); sourceEdge.More(); sourceEdge.Next())
        {
            BRepBuilderAPI_MakeWire singleEdge;
            singleEdge.Add(sourceEdge.Current());
            if (!singleEdge.IsDone())
                return false;

            BRepProj_Projection edgeProjection(singleEdge.Wire(), target, direction);
            if (!edgeProjection.IsDone() || !edgeProjection.More())
                return false;

            BRepAdaptor_Curve curve(sourceEdge.Current());
            const double firstParameter = curve.FirstParameter();
            const double lastParameter = curve.LastParameter();
            if (!std::isfinite(firstParameter) || !std::isfinite(lastParameter) ||
                lastParameter <= firstParameter)
                return false;

            for (double fraction : { 0.0, 0.5, 1.0 })
            {
                const gp_Pnt point = curve.Value(firstParameter +
                    (lastParameter - firstParameter) * fraction);
                coverage.Perform(gp_Lin(point, direction), 0.0, 1.0e12);

                bool hasSelectedFaceHit = false;
                if (coverage.IsDone())
                {
                    for (Standard_Integer hitIndex = 1; hitIndex <= coverage.NbPnt(); ++hitIndex)
                    {
                        const auto state = coverage.State(hitIndex);
                        if (state == TopAbs_IN || state == TopAbs_ON)
                        {
                            hasSelectedFaceHit = true;
                            break;
                        }
                    }
                }

                if (!hasSelectedFaceHit)
                    return false;
            }

            ++sourceEdgeCount;
        }

        if (sourceEdgeCount == 0)
            return false;

        BRepProj_Projection projection(sourceWire, target, direction);
        std::vector<TopoDS_Wire> projectedWires;
        for (projection.Init(); projection.More(); projection.Next())
            projectedWires.push_back(projection.Current());

        if (projectedWires.empty())
            return false;

        Handle(TopTools_HSequenceOfShape) projectedEdges = new TopTools_HSequenceOfShape();
        for (const TopoDS_Wire& projectedWire : projectedWires)
        {
            for (BRepTools_WireExplorer edge(projectedWire); edge.More(); edge.Next())
                projectedEdges->Append(edge.Current());
        }

        Handle(TopTools_HSequenceOfShape) connectedWires = new TopTools_HSequenceOfShape();
        ShapeAnalysis_FreeBounds::ConnectEdgesToWires(
            projectedEdges, 0.01, Standard_False, connectedWires);
        if (connectedWires->Length() != 1)
            return false;

        wire = TopoDS::Wire(connectedWires->Value(1));
        if (!BRepCheck_Analyzer(wire).IsValid())
            return false;

        size_t orderedEdgeCount = 0;
        size_t totalEdgeCount = 0;
        for (BRepTools_WireExplorer edge(wire); edge.More(); edge.Next())
            ++orderedEdgeCount;
        for (TopExp_Explorer edge(wire, TopAbs_EDGE); edge.More(); edge.Next())
            ++totalEdgeCount;
        if (orderedEdgeCount == 0 || orderedEdgeCount != totalEdgeCount)
            return false;

        GProp_GProps properties;
        BRepGProp::LinearProperties(wire, properties);
        if (!std::isfinite(properties.Mass()) || properties.Mass() <= Precision::Confusion())
            return false;

        TopoDS_Vertex firstVertex, lastVertex;
        for (BRepTools_WireExplorer edge(wire); edge.More(); edge.Next())
        {
            TopoDS_Vertex edgeStart, edgeEnd;
            TopExp::Vertices(edge.Current(), edgeStart, edgeEnd, Standard_True);
            if (firstVertex.IsNull())
                firstVertex = edgeStart;
            lastVertex = edgeEnd;
        }

        if (firstVertex.IsNull() || lastVertex.IsNull())
            return false;

        gp_Pnt projectedStart = BRep_Tool::Pnt(firstVertex);
        gp_Pnt projectedEnd = BRep_Tool::Pnt(lastVertex);
        const auto transverseDistance = [&direction](const gp_Pnt& point, const gp_Pnt& sourcePoint)
        {
            const gp_Vec offset(point, sourcePoint);
            const gp_Vec projectionDirection(direction);
            return (offset - projectionDirection * offset.Dot(projectionDirection)).Magnitude();
        };

        if (transverseDistance(projectedStart, sourceStart) > 0.01 ||
            transverseDistance(projectedEnd, sourceEnd) > 0.01)
            std::swap(projectedStart, projectedEnd);

        if (transverseDistance(projectedStart, sourceStart) > 0.01 ||
            transverseDistance(projectedEnd, sourceEnd) > 0.01)
            return false;

        result[0] = properties.Mass();
        result[1] = projectedStart.X();
        result[2] = projectedStart.Y();
        result[3] = projectedStart.Z();
        result[4] = projectedEnd.X();
        result[5] = projectedEnd.Y();
        result[6] = projectedEnd.Z();
        if (approximation != nullptr && !SampleWireForSnapshot(
                wire,
                projectedStart,
                projectedEnd,
                requestedDeflectionMm,
                requestedAngularToleranceRad,
                maximumSampleSpacingMm,
                maximumSubdivisionDepth,
                maximumProbeCount,
                *approximation))
        {
            return false;
        }
        return true;
    }
    catch (...)
    {
        return false;
    }
}

bool BuildProjectedWorkingWire(NativeDocumentSession& document,
    long long id,long long revision,const std::vector<double>& xyz,
    const std::vector<int>& faceIds,const gp_Dir& direction,TopoDS_Wire& wire,double* result)
{
    if (xyz.size() < 6 || xyz.size() % 3 != 0)
        return false;

    try
    {
        BRepBuilderAPI_MakePolygon source;
        for (size_t index = 0; index < xyz.size(); index += 3)
        {
            const double x = xyz[index];
            const double y = xyz[index + 1];
            const double z = xyz[index + 2];
            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z))
                return false;

            source.Add(gp_Pnt(x, y, z));
        }
        if (!source.IsDone())
            return false;

        const gp_Pnt sourceStart(xyz[0], xyz[1], xyz[2]);
        const size_t lastPoint = xyz.size() - 3;
        const gp_Pnt sourceEnd(xyz[lastPoint], xyz[lastPoint + 1], xyz[lastPoint + 2]);
        return BuildProjectedWorkingWireFromSource(
            document, id, revision, source.Wire(), sourceStart, sourceEnd, faceIds, direction, wire, result);
    }
    catch (...)
    {
        return false;
    }
}

bool BuildProjectedWorkingCurveWire(NativeDocumentSession& document,
    long long id,long long revision,const CurveDefinitionNative& definition,
    const std::vector<int>& faceIds,const gp_Dir& direction,TopoDS_Wire& wire,double* result,
    NativeCurveApproximation* approximation = nullptr,
    double requestedDeflectionMm = 0.05,
    double requestedAngularToleranceRad = 0.1,
    double maximumSampleSpacingMm = 2.0,
    int maximumSubdivisionDepth = 18,
    int maximumProbeCount = 100000)
{
    if (faceIds.empty() || !result)
        return false;

    try
    {
        TopoDS_Wire source;
        if (!BuildCurveWireNative(definition, source))
            return false;

        TopoDS_Vertex firstVertex, lastVertex;
        for (BRepTools_WireExplorer edge(source); edge.More(); edge.Next())
        {
            TopoDS_Vertex edgeStart, edgeEnd;
            TopExp::Vertices(edge.Current(), edgeStart, edgeEnd, Standard_True);
            if (edgeStart.IsNull() || edgeEnd.IsNull())
                return false;

            if (firstVertex.IsNull())
                firstVertex = edgeStart;
            lastVertex = edgeEnd;
        }

        if (firstVertex.IsNull() || lastVertex.IsNull())
            return false;

        const gp_Pnt sourceStart = BRep_Tool::Pnt(firstVertex);
        const gp_Pnt sourceEnd = BRep_Tool::Pnt(lastVertex);
        return BuildProjectedWorkingWireFromSource(
            document, id, revision, source, sourceStart, sourceEnd, faceIds, direction, wire, result,
            approximation, requestedDeflectionMm, requestedAngularToleranceRad,
            maximumSampleSpacingMm, maximumSubdivisionDepth, maximumProbeCount);
    }
    catch (...)
    {
        return false;
    }
}

bool IsTurnSegmentKind(const std::string& kind)
{
    std::string normalizedKind = kind;
    std::transform(normalizedKind.begin(), normalizedKind.end(), normalizedKind.begin(),
        [](unsigned char character) { return static_cast<char>(std::tolower(character)); });
    return normalizedKind == "turnsegment" || normalizedKind == "turn";
}

bool IsConnectionPathSegmentKind(const std::string& kind)
{
    std::string normalizedKind = kind;
    std::transform(normalizedKind.begin(), normalizedKind.end(), normalizedKind.begin(),
        [](unsigned char character) { return static_cast<char>(std::tolower(character)); });
    return normalizedKind == "connectionpathsegment" || normalizedKind == "connectionsegment";
}

bool StagePreparedCandidateWorkingCurveSegmentNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    int stageId,
    const std::string& kind,
    const CurveDefinitionNative& definition,
    const std::vector<int>& faceIds,
    const gp_Dir& direction,
    double* result,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount,
    NativeCurveApproximation* approximation)
{
    if (stageId >= 0 || !result || !FindWorking(document, contextId, revision) ||
        document.stagedPreparedPathSegments.count(stageId))
    {
        return false;
    }

    TopoDS_Wire projectedWire;
    if (!BuildProjectedWorkingCurveWire(
            document,
            contextId,
            revision,
            definition,
            faceIds,
            direction,
            projectedWire,
            result,
            approximation,
            requestedDeflectionMm,
            requestedAngularToleranceRad,
            maximumSampleSpacingMm,
            maximumSubdivisionDepth,
            maximumProbeCount))
    {
        return false;
    }

    StagedPreparedPathSegmentGeometry staged;
    staged.wire = projectedWire;
    staged.contextId = contextId;
    staged.revision = revision;
    staged.turnSegment = IsTurnSegmentKind(kind);
    staged.connectionPathSegment = IsConnectionPathSegmentKind(kind);
    return document.stagedPreparedPathSegments.emplace(stageId, std::move(staged)).second;
}

/// <summary>모델 좌표 곡선을 재투영하지 않고 측정해 후보 전용 staging으로 보관합니다.</summary>
bool StagePreparedCandidateExactCurveSegmentNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    int stageId,
    const std::string& kind,
    const CurveDefinitionNative& definition,
    double* result,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount,
    NativeCurveApproximation* approximation)
{
    if (stageId >= 0 || result == nullptr || approximation == nullptr ||
        !FindWorking(document, contextId, revision) ||
        document.stagedPreparedPathSegments.count(stageId) != 0)
    {
        return false;
    }

    try
    {
        TopoDS_Wire wire;
        if (!BuildCurveWireNative(definition, wire))
            return false;

        TopoDS_Vertex firstVertex;
        TopoDS_Vertex lastVertex;
        size_t orderedEdgeCount = 0;
        size_t totalEdgeCount = 0;
        for (BRepTools_WireExplorer edge(wire); edge.More(); edge.Next())
        {
            TopoDS_Vertex edgeStart;
            TopoDS_Vertex edgeEnd;
            TopExp::Vertices(edge.Current(), edgeStart, edgeEnd, Standard_True);
            if (edgeStart.IsNull() || edgeEnd.IsNull())
                return false;

            if (firstVertex.IsNull())
                firstVertex = edgeStart;
            lastVertex = edgeEnd;
            ++orderedEdgeCount;
        }
        for (TopExp_Explorer edge(wire, TopAbs_EDGE); edge.More(); edge.Next())
            ++totalEdgeCount;

        if (firstVertex.IsNull() || lastVertex.IsNull() || orderedEdgeCount == 0 ||
            orderedEdgeCount != totalEdgeCount || !BRepCheck_Analyzer(wire).IsValid())
        {
            return false;
        }

        GProp_GProps properties;
        BRepGProp::LinearProperties(wire, properties);
        const double lengthMm = properties.Mass();
        if (!std::isfinite(lengthMm) || lengthMm <= Precision::Confusion())
            return false;

        const gp_Pnt start = BRep_Tool::Pnt(firstVertex);
        const gp_Pnt end = BRep_Tool::Pnt(lastVertex);
        if (!std::isfinite(start.X()) || !std::isfinite(start.Y()) || !std::isfinite(start.Z()) ||
            !std::isfinite(end.X()) || !std::isfinite(end.Y()) || !std::isfinite(end.Z()))
        {
            return false;
        }

        if (!SampleWireForSnapshot(
                wire,
                start,
                end,
                requestedDeflectionMm,
                requestedAngularToleranceRad,
                maximumSampleSpacingMm,
                maximumSubdivisionDepth,
                maximumProbeCount,
                *approximation))
        {
            return false;
        }

        result[0] = lengthMm;
        result[1] = start.X();
        result[2] = start.Y();
        result[3] = start.Z();
        result[4] = end.X();
        result[5] = end.Y();
        result[6] = end.Z();

        StagedPreparedPathSegmentGeometry staged;
        staged.wire = wire;
        staged.contextId = contextId;
        staged.revision = revision;
        staged.turnSegment = IsTurnSegmentKind(kind);
        staged.connectionPathSegment = IsConnectionPathSegmentKind(kind);
        return document.stagedPreparedPathSegments.emplace(stageId, std::move(staged)).second;
    }
    catch (...)
    {
        return false;
    }
}

bool CommitPreparedCandidateWorkingPathGeometryNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    const std::vector<int>& stagedSegmentIds,
    const std::vector<int>& finalSegmentIds)
{
    if (!FindWorking(document, contextId, revision) || stagedSegmentIds.size() != finalSegmentIds.size())
        return false;

    std::set<int> uniqueStagedIds;
    std::set<int> uniqueFinalIds;
    for (size_t index = 0; index < stagedSegmentIds.size(); ++index)
    {
        const int stagedId = stagedSegmentIds[index];
        const int finalId = finalSegmentIds[index];
        auto staged = document.stagedPreparedPathSegments.find(stagedId);
        if (stagedId >= 0 || finalId < 0 || !uniqueStagedIds.insert(stagedId).second ||
            !uniqueFinalIds.insert(finalId).second || staged == document.stagedPreparedPathSegments.end() ||
            staged->second.contextId != contextId || staged->second.revision != revision ||
            staged->second.wire.IsNull())
        {
            return false;
        }
    }

    std::map<int, PreparedPathSegmentGeometry> nextPreparedSegments;
    try
    {
        for (size_t index = 0; index < stagedSegmentIds.size(); ++index)
        {
            const auto& staged = document.stagedPreparedPathSegments.at(stagedSegmentIds[index]);
            PreparedPathSegmentGeometry prepared;
            prepared.wire = staged.wire;
            prepared.turnSegment = staged.turnSegment;
            prepared.connectionPathSegment = staged.connectionPathSegment;
            nextPreparedSegments.emplace(finalSegmentIds[index], std::move(prepared));
        }
    }
    catch (...)
    {
        return false;
    }

    document.preparedPathSegments.swap(nextPreparedSegments);
    for (int stagedId : stagedSegmentIds)
        document.stagedPreparedPathSegments.erase(stagedId);

    return true;
}

void DiscardPreparedCandidateWorkingPathGeometryNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    const std::vector<int>& stagedSegmentIds)
{
    for (int stagedId : stagedSegmentIds)
    {
        auto staged = document.stagedPreparedPathSegments.find(stagedId);
        if (staged == document.stagedPreparedPathSegments.end() ||
            staged->second.contextId != contextId || staged->second.revision != revision)
        {
            continue;
        }

        document.stagedPreparedPathSegments.erase(staged);
    }
}

bool CanStageProjectedWorkingPathNative(int stageId)
{
    return stageId < 0 && !g_ctx.IsNull() && !g_documentSession->presentation.pathSegments.count(stageId) &&
        !g_documentSession->stagedWorkingPathSegmentOwners.count(stageId) && !g_documentSession->stagedWorkingPolylines.count(stageId);
}

bool StageProjectedWorkingPathWireNative(long long id,long long revision,int stageId,const std::string& kind,
    const TopoDS_Wire& wire)
{
    if (!CanStageProjectedWorkingPathNative(stageId) || wire.IsNull())
        return false;

    Handle(AIS_Shape) ais;
    try
    {
        ais = new AIS_Shape(wire);

        const bool isTurnSegment = IsTurnSegmentKind(kind);
        const Quantity_Color color = isTurnSegment
            ? Quantity_Color(0.1, 0.65, 1.0, Quantity_TOC_RGB)
            : Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB);

        g_ctx->SetColor(ais, color, Standard_False);
        if (isTurnSegment)
            ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
        g_ctx->Display(ais, Standard_False);
        g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
        g_ctx->Activate(ais, Standard_False);

        const auto pathInsert = g_documentSession->presentation.pathSegments.emplace(
            stageId, HeatingPathSegmentOverlay{ ais, isTurnSegment, true, color, 1.0 });
        if (!pathInsert.second)
        {
            g_ctx->Remove(ais, Standard_False);
            return false;
        }

        const auto ownerInsert = g_documentSession->stagedWorkingPathSegmentOwners.emplace(
            stageId, std::make_pair(id, revision));
        if (!ownerInsert.second)
        {
            g_ctx->Remove(ais, Standard_False);
            g_documentSession->presentation.pathSegments.erase(stageId);
            return false;
        }

        return true;
    }
    catch (...)
    {
        if (!ais.IsNull() && !g_ctx.IsNull())
        {
            try
            {
                g_ctx->Remove(ais, Standard_False);
            }
            catch (...) { }
        }

        const auto owner = g_documentSession->stagedWorkingPathSegmentOwners.find(stageId);
        if (owner != g_documentSession->stagedWorkingPathSegmentOwners.end() && owner->second == std::make_pair(id, revision))
            g_documentSession->stagedWorkingPathSegmentOwners.erase(owner);

        const auto path = g_documentSession->presentation.pathSegments.find(stageId);
        if (path != g_documentSession->presentation.pathSegments.end() && path->second.ais == ais)
            g_documentSession->presentation.pathSegments.erase(path);

        return false;
    }
}

bool StageProjectedWorkingPathSegmentNative(long long id,long long revision,int stageId,const std::string& kind,
    const std::vector<double>& xyz,const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if (!CanStageProjectedWorkingPathNative(stageId))
        return false;

    TopoDS_Wire wire;
    if (!BuildProjectedWorkingWire(*g_documentSession, id, revision, xyz, faces, direction, wire, result))
        return false;

    return StageProjectedWorkingPathWireNative(id, revision, stageId, kind, wire);
}

bool StageProjectedWorkingCurveSegmentNative(long long id,long long revision,int stageId,const std::string& kind,
    const CurveDefinitionNative& definition,const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if (!CanStageProjectedWorkingPathNative(stageId))
        return false;

    TopoDS_Wire wire;
    if (!BuildProjectedWorkingCurveWire(*g_documentSession, id, revision, definition, faces, direction, wire, result))
        return false;

    return StageProjectedWorkingPathWireNative(id, revision, stageId, kind, wire);
}

bool StageProjectedWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz,
    const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if(stageId>=0||g_ctx.IsNull()||g_documentSession->stagedWorkingPolylines.count(stageId)||g_documentSession->stagedWorkingPathSegmentOwners.count(stageId)||g_documentSession->presentation.pathSegments.count(stageId)) return false;
    TopoDS_Wire wire; if(!BuildProjectedWorkingWire(*g_documentSession,id,revision,xyz,faces,direction,wire,result)) return false;
    Handle(AIS_Shape) ais;
    try { ais=new AIS_Shape(wire);g_ctx->SetColor(ais,Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB),Standard_False);
        g_ctx->Display(ais,Standard_False);g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);g_ctx->Deactivate(ais,Standard_False);
        if(!g_documentSession->stagedWorkingPolylines.emplace(stageId,StagedWorkingPolyline{ais,id,revision}).second) { g_ctx->Remove(ais,Standard_False); return false; }
        return true;
    } catch(...) { if(!ais.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} } g_documentSession->stagedWorkingPolylines.erase(stageId); return false; }
}

void DiscardWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& paths,const std::vector<int>& lines)
{
    for (int id : paths) {
        auto owner=g_documentSession->stagedWorkingPathSegmentOwners.find(id);
        if (id>=0 || owner==g_documentSession->stagedWorkingPathSegmentOwners.end() || owner->second!=std::make_pair(contextId,revision)) continue;
        auto it=g_documentSession->presentation.pathSegments.find(id);
        if (it!=g_documentSession->presentation.pathSegments.end()) { if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False); g_documentSession->presentation.pathSegments.erase(it); }
        g_documentSession->stagedWorkingPathSegmentOwners.erase(owner);
    }
    for (int id : lines) {
        auto it=g_documentSession->stagedWorkingPolylines.find(id);
        if (it==g_documentSession->stagedWorkingPolylines.end() || it->second.contextId!=contextId || it->second.revision!=revision) continue;
        if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False);
        g_documentSession->stagedWorkingPolylines.erase(it);
    }
}

bool CommitWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& stagedSegments,const std::vector<int>& finalSegments,
    const std::vector<int>& stagedLines)
{
    if (!FindWorking(contextId,revision)) return false;
    if (stagedSegments.size()!=finalSegments.size()) return false;
    std::set<int> finalSet, stagedSegmentSet, stagedLineSet;
    for (size_t i=0;i<stagedSegments.size();++i)
        if (stagedSegments[i]>=0 || !stagedSegmentSet.insert(stagedSegments[i]).second || !g_documentSession->presentation.pathSegments.count(stagedSegments[i]) || !g_documentSession->stagedWorkingPathSegmentOwners.count(stagedSegments[i]) ||
            g_documentSession->stagedWorkingPathSegmentOwners.at(stagedSegments[i])!=std::make_pair(contextId,revision) || finalSegments[i]<0 || !finalSet.insert(finalSegments[i]).second) return false;
    for (int id : stagedLines) if (!stagedLineSet.insert(id).second || !g_documentSession->stagedWorkingPolylines.count(id) ||
        g_documentSession->stagedWorkingPolylines.at(id).contextId!=contextId || g_documentSession->stagedWorkingPolylines.at(id).revision!=revision) return false;
    std::map<int,HeatingPathSegmentOverlay> nextPathSegments;
    std::vector<Handle(AIS_Shape)> nextWorld;
    std::vector<Handle(AIS_Shape)> removed;
    std::set<int> stagedSet;
    try {
        for (size_t i=0;i<stagedSegments.size();++i) nextPathSegments.emplace(finalSegments[i],g_documentSession->presentation.pathSegments.at(stagedSegments[i]));
        for (const auto& path:g_documentSession->presentation.pathSegments) {
            if(path.first>=0||stagedSegmentSet.count(path.first)) continue;
            auto owner=g_documentSession->stagedWorkingPathSegmentOwners.find(path.first);
            if(owner!=g_documentSession->stagedWorkingPathSegmentOwners.end()&&owner->second!=std::make_pair(contextId,revision))
                nextPathSegments.emplace(path.first,path.second);
        }
        nextWorld.reserve(stagedLines.size());
        for (int id : stagedLines) nextWorld.push_back(g_documentSession->stagedWorkingPolylines.at(id).ais);
        removed.reserve(g_documentSession->presentation.pathSegments.size()+g_documentSession->presentation.worldOverlays.size());
        stagedSet.insert(stagedSegments.begin(),stagedSegments.end());
    } catch (...) { return false; }
    try {
        // Candidate geometry is already displayed and selectable. All validation precedes destructive swap.
        HighlightCircuitNative(std::vector<int>());
        for (const auto& old : g_documentSession->presentation.pathSegments) {
            // Keep private candidates belonging to other live contexts. Replace committed
            // positive paths and only this transaction's staged candidates.
            if (old.first<0 && !stagedSet.count(old.first)) continue;
            removed.push_back(old.second.ais);
            if (!g_ctx.IsNull()) g_ctx->Remove(old.second.ais,Standard_False);
        }
        for (const auto& old : g_documentSession->presentation.worldOverlays) {
            removed.push_back(old);
            if (!g_ctx.IsNull()) g_ctx->Remove(old,Standard_False);
        }
        g_documentSession->presentation.pathSegments.swap(nextPathSegments); g_documentSession->presentation.worldOverlays.swap(nextWorld);
        for (int id : stagedSegments) g_documentSession->stagedWorkingPathSegmentOwners.erase(id);
        for (int id : stagedLines) g_documentSession->stagedWorkingPolylines.erase(id);
        if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
        return true;
    } catch (...) {
        if (!g_ctx.IsNull()) for (const auto& old : removed) { try { g_ctx->Display(old,Standard_False); } catch (...) {} }
        return false;
    }
}

void ShowProjectedModelBoundaryNative(const std::vector<std::vector<double>>& loops, const std::vector<bool>& holes)
{
    ClearProjectedModelBoundaryNative(*g_documentSession);
    if (g_ctx.IsNull()) return;
    for (size_t index=0; index<loops.size(); ++index) {
        const auto& loop=loops[index];
        if (loop.size()<9) continue;
        BRepBuilderAPI_MakePolygon polygon;
        for (size_t i=0;i+2<loop.size();i+=3) polygon.Add(gp_Pnt(loop[i],loop[i+1],loop[i+2]));
        polygon.Close();
        if (!polygon.IsDone()) continue;
        Handle(AIS_Shape) ais = new AIS_Shape(polygon.Wire());
        const bool hole=index<holes.size() && holes[index];
        ais->SetColor(hole ? Quantity_Color(0.2,0.4,1,Quantity_TOC_RGB) : Quantity_Color(1,0.55,0,Quantity_TOC_RGB));
        ais->SetWidth(2);
        g_ctx->Display(ais,Standard_False); g_ctx->Deactivate(ais);
        g_documentSession->workingBoundaryOverlays.push_back(ais);
    }
    g_view->Redraw();
}

long long CaptureWorkingCoordinateSystemPresentationNative(double* bounds, bool& hasBounds)
{
    try {
        WorkingCoordinateSystemPresentationNative snapshot;
        snapshot.revision=g_documentSession->modelRevision;
        snapshot.boundaries=g_documentSession->workingBoundaryOverlays;
        if (!g_view.IsNull()) snapshot.camera=new Graphic3d_Camera(g_view->Camera());
        Bnd_Box box;
        for (const auto& boundary : snapshot.boundaries) BRepBndLib::Add(boundary->Shape(),box);
        hasBounds=!box.IsVoid();
        if (hasBounds) box.Get(bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]);
        const long long id=g_nextWorkingCoordinateSystemPresentation++;
        g_documentSession->workingPresentations.emplace(id,std::move(snapshot));
        return id;
    } catch (const Standard_Failure&) { return 0; }
}

bool RestoreWorkingCoordinateSystemPresentationNative(long long id)
{
    const auto it=g_documentSession->workingPresentations.find(id);
    if (it==g_documentSession->workingPresentations.end() || it->second.revision!=g_documentSession->modelRevision) return false;
    const auto& snapshot=it->second;
    if (g_ctx.IsNull()) return snapshot.boundaries.empty() && snapshot.camera.IsNull();
    try {
        ClearProjectedModelBoundaryNative(*g_documentSession);
        g_documentSession->workingBoundaryOverlays=snapshot.boundaries;
        for (const auto& boundary : g_documentSession->workingBoundaryOverlays) {
            g_ctx->Display(boundary,Standard_False); g_ctx->Deactivate(boundary);
        }
        if (!snapshot.camera.IsNull() && !g_view.IsNull()) g_view->Camera()->Copy(snapshot.camera);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    } catch (const Standard_Failure&) { return false; }
}
