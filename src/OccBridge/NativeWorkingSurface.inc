struct CurveDefinitionNative
{
    std::string kind;
    std::string direction;
    std::vector<double> start;
    std::vector<double> end;
    std::vector<double> points;
    std::vector<double> frame;
    std::vector<double> center;
    double radiusMm = 0;
    double startAngleRad = 0;
    double endAngleRad = 0;
    int degree = 0;
    std::vector<double> poles;
    std::vector<double> weights;
    std::vector<double> knots;
    std::vector<int> multiplicities;
    bool isPeriodic = false;
    double trimStart = 0;
    double trimEnd = 0;
};

long long g_nextWorkingId = 1;
long long g_nextWorkingCoordinateSystemPresentation = 1;

void ClearProjectedModelBoundaryNative(NativeDocumentSession& document)
{
    if (!g_ctx.IsNull())
    {
        for (const Handle(AIS_Shape)& shape : document.workingBoundaryOverlays)
            g_ctx->Remove(shape, Standard_False);
    }

    document.workingBoundaryOverlays.clear();
}

void InvalidateWorkingSurfaceContexts(NativeDocumentSession& document)
{
    for (const auto& staged : document.stagedWorkingPolylines)
        if (!g_ctx.IsNull()) g_ctx->Remove(staged.second.ais,Standard_False);
    document.stagedWorkingPolylines.clear();
    document.stagedWorkingPathSegmentOwners.clear();
    document.workingContexts.clear();
    document.workingPresentations.clear();
    ClearProjectedModelBoundaryNative(document);
}

void ReleaseWorkingNative(long long id)
{
    const auto context=g_documentSession->workingContexts.find(id);
    if (context==g_documentSession->workingContexts.end()) return;
    const long long revision=context->second->revision;
    bool removedSegment=false;
    for (auto it=g_documentSession->stagedWorkingPolylines.begin();it!=g_documentSession->stagedWorkingPolylines.end();) {
        if (it->second.contextId!=id || it->second.revision!=revision) { ++it; continue; }
        if (!g_ctx.IsNull()) { try { g_ctx->Remove(it->second.ais,Standard_False); } catch (...) {} }
        it=g_documentSession->stagedWorkingPolylines.erase(it);
    }
    for (auto owner=g_documentSession->stagedWorkingPathSegmentOwners.begin();owner!=g_documentSession->stagedWorkingPathSegmentOwners.end();) {
        if (owner->second.first!=id || owner->second.second!=revision) { ++owner; continue; }
        auto path=g_documentSession->presentation.pathSegments.find(owner->first);
        if (path!=g_documentSession->presentation.pathSegments.end()) {
            if (!g_ctx.IsNull()) { try { g_ctx->Remove(path->second.ais,Standard_False); } catch (...) {} }
            g_documentSession->presentation.pathSegments.erase(path); removedSegment=true;
        }
        owner=g_documentSession->stagedWorkingPathSegmentOwners.erase(owner);
    }
    g_documentSession->workingContexts.erase(context);
    if (removedSegment) HighlightCircuitNative(std::vector<int>());
    if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
}

WorkingNative* FindWorking(NativeDocumentSession& document, long long id, long long revision);
long long CreateWorkingNative(NativeDocumentSession& document,
    double nx, double ny, double nz, double xx, double xy, double xz,
    bool updateActivePresentation);

WorkingNative* FindWorking(long long id, long long revision)
{
    return FindWorking(*g_documentSession, id, revision);
}

WorkingNative* FindWorking(NativeDocumentSession& document, long long id, long long revision)
{
    const auto it = document.workingContexts.find(id);
    return revision == document.modelRevision && it != document.workingContexts.end() && it->second->revision == revision
        ? it->second.get() : nullptr;
}

long long CreateWorkingNative(double nx, double ny, double nz, double xx, double xy, double xz)
{
    return CreateWorkingNative(*g_documentSession, nx, ny, nz, xx, xy, xz, true);
}

long long CreateWorkingNative(NativeDocumentSession& document,
    double nx, double ny, double nz, double xx, double xy, double xz,
    bool updateActivePresentation)
{
    if (document.shape.IsNull() || !std::isfinite(nx) || !std::isfinite(ny) || !std::isfinite(nz) ||
        !std::isfinite(xx) || !std::isfinite(xy) || !std::isfinite(xz)) return 0;
    try {
        gp_Vec n(nx, ny, nz), x(xx, xy, xz);
        if (n.SquareMagnitude() < 1e-18) return 0;
        n.Normalize(); x -= n * x.Dot(n);
        if (x.SquareMagnitude() < 1e-18) return 0;
        x.Normalize();
        auto ctx = std::make_unique<WorkingNative>();
        ctx->n = gp_Dir(n); ctx->x = gp_Dir(x); ctx->y = gp_Dir(n.Crossed(x));
        double b[6];
        if (!GetModelBoundsInFrameNative(document, ctx->x, ctx->y, ctx->n, b)) return 0;
        const double margin = std::max(1.0, (b[5] - b[4]) * 0.01);
        ctx->rayLength = b[5]-b[4]+margin+1;
        ctx->origin = gp::Origin().Translated(x * ((b[0]+b[1])/2) +
            gp_Vec(ctx->y) * ((b[2]+b[3])/2) + n * (b[5]+margin));
        ctx->intersector = std::make_unique<IntCurvesFace_ShapeIntersector>();
        ctx->intersector->Load(document.shape, Precision::Confusion());
        // Rebuild once per preview, never per hover. Absolute deflection, explicit angular control.
        BRepTools::Clean(document.shape);
        BRepMesh_IncrementalMesh mesh(document.shape, ctx->deflection, Standard_False, 0.1, Standard_True);
        if (!mesh.IsDone()) return 0;
        for (const auto& face : document.faces) {
            TopLoc_Location loc;
            Handle(Poly_Triangulation) tri = BRep_Tool::Triangulation(face, loc);
            if (tri.IsNull()) return 0; // incomplete silhouette is not a successful preview
            for (int i = 1; i <= tri->NbTriangles(); ++i) {
                int a,b,c; tri->Triangle(i).Get(a,b,c);
                for (int index : {a,b,c}) {
                    const gp_Pnt p = tri->Node(index).Transformed(loc.Transformation());
                    ctx->triangles.insert(ctx->triangles.end(), {p.X(),p.Y(),p.Z()});
                }
            }
        }
        if (updateActivePresentation && !g_ctx.IsNull() && !document.presentation.aisShape.IsNull())
            g_ctx->Redisplay(document.presentation.aisShape, Standard_False);
        ctx->id = g_nextWorkingId++; ctx->revision = document.modelRevision;
        const long long id = ctx->id;
        document.workingContexts.emplace(id, std::move(ctx));
        return id;
    } catch (const Standard_Failure&) { return 0; }
}

// Matches the managed enum without exposing flat numeric result arrays.
enum class WorkingStatusNative { Hit, Miss, Tangent, InvalidInput, StaleContext, GeometryFailure };
// Never discard a nearer tangent to use a back hit.
WorkingStatusNative QueryWorkingNative(
    NativeDocumentSession& document,
    long long id,
    long long revision,
    double u,
    double v,
    double* out,
    int& faceId,
    double* outNormal = nullptr,
    double* outFaceUv = nullptr)
{
    WorkingNative* ctx = FindWorking(document, id, revision);
    if (!ctx) return WorkingStatusNative::StaleContext;
    if (!std::isfinite(u) || !std::isfinite(v)) return WorkingStatusNative::InvalidInput;
    try {
        const gp_Pnt origin = ctx->origin.Translated(gp_Vec(ctx->x)*u + gp_Vec(ctx->y)*v);
        const gp_Dir d = ctx->n.Reversed();
        auto& hits = *ctx->intersector;
        hits.Perform(gp_Lin(origin,d), 0, ctx->rayLength);
        if (!hits.IsDone()) return WorkingStatusNative::GeometryFailure;
        int first = 0; double nearest = 1e100;
        for (int i=1; i<=hits.NbPnt(); ++i) {
            const double t = hits.WParameter(i);
            if (!std::isfinite(t)) return WorkingStatusNative::GeometryFailure;
            if (t >= 0 && t < nearest && (hits.State(i)==TopAbs_IN || hits.State(i)==TopAbs_ON))
            { nearest=t; first=i; }
        }
        if (!first) return WorkingStatusNative::Miss;
        const gp_Pnt p = hits.Pnt(first);
        if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z()))
            return WorkingStatusNative::GeometryFailure;
        const double faceU = hits.UParameter(first);
        const double faceV = hits.VParameter(first);
        if (!std::isfinite(faceU) || !std::isfinite(faceV))
            return WorkingStatusNative::GeometryFailure;
        out[0]=p.X(); out[1]=p.Y(); out[2]=p.Z();
        if (outFaceUv) { outFaceUv[0]=faceU; outFaceUv[1]=faceV; }
        faceId = document.faceMap.FindIndex(hits.Face(first))-1;
        if (faceId<0) return WorkingStatusNative::GeometryFailure;
        BRepAdaptor_Surface surface(hits.Face(first));
        gp_Vec normal;
        if (surface.GetType()==GeomAbs_Sphere) normal=gp_Vec(surface.Sphere().Location(),p);
        else {
            gp_Pnt q; gp_Vec du,dv;
            surface.D1(hits.UParameter(first),hits.VParameter(first),q,du,dv);
            normal=du.Crossed(dv);
        }
        if (!std::isfinite(normal.SquareMagnitude()) || normal.SquareMagnitude()<1e-24)
            return WorkingStatusNative::GeometryFailure;
        normal.Normalize();
        if (outNormal) { outNormal[0]=normal.X(); outNormal[1]=normal.Y(); outNormal[2]=normal.Z(); }
        return std::fabs(normal.Dot(gp_Vec(d))) < 1e-7 ? WorkingStatusNative::Tangent : WorkingStatusNative::Hit;
    } catch (const Standard_Failure&) { return WorkingStatusNative::GeometryFailure; }
}

WorkingStatusNative QueryWorkingNative(
    long long id, long long revision, double u, double v, double* out, int& faceId,
    double* outNormal = nullptr, double* outFaceUv = nullptr)
{
    return QueryWorkingNative(*g_documentSession, id, revision, u, v, out, faceId, outNormal, outFaceUv);
}

bool AllFinite(const std::vector<double>& values);

enum class CurveDepthEnvelopeStatusNative
{
    Success,
    InvalidInput,
    InvalidFace,
    CandidateNotFound,
    StaleContext,
    Miss,
    Tangent,
    ProbeBudgetExceeded,
    GeometryFailure,
    Unresolved,
    Unknown
};

struct NativeCurveDepthStation
{
    gp_Pnt centerlinePoint;
    gp_Pnt surfacePoint;
    gp_Pnt wireOutsidePoint;
    double depthMm = 0.0;
    double axisSupport = 0.0;
};

struct NativeCurveDepthEnvelopeMeasurement
{
    CurveDepthEnvelopeStatusNative status = CurveDepthEnvelopeStatusNative::Unknown;
    double measuredDepthMm = 0.0;
    double minimumDepthMm = 0.0;
    double maximumDepthMm = 0.0;
    double errorBoundMm = 0.0;
    gp_Pnt surfacePoint;
    gp_Pnt minimumSurfacePoint;
    gp_Pnt maximumSurfacePoint;
    gp_Pnt minimumWireOutsidePoint;
    gp_Pnt maximumWireOutsidePoint;
    int probeCount = 0;
    std::string diagnostic;
};

struct NativeCurveDepthAdjustment
{
    CurveDepthEnvelopeStatusNative status = CurveDepthEnvelopeStatusNative::Unknown;
    std::vector<double> centerlineSamples;
    double maximumDepthResidualMm = 0.0;
    double errorBoundMm = 0.0;
    int probeCount = 0;
    std::string diagnostic;
};

/// <summary>접선 각도 공차가 축방향 ray 깊이에 주는 최악 오차를 계산합니다.</summary>
bool TryEstimateAxisRayAngularError(
    double radiusMm,
    double axisSupport,
    double angularToleranceRad,
    double& errorMm)
{
    errorMm = 0.0;
    if (!std::isfinite(radiusMm) || radiusMm <= 0.0 ||
        !std::isfinite(axisSupport) || axisSupport <= 0.0 ||
        !std::isfinite(angularToleranceRad) || angularToleranceRad <= 0.0)
    {
        return false;
    }

    const double tangentAngleRad = std::asin(std::max(0.0, std::min(1.0, axisSupport)));
    const double minimumTangentAngleRad = tangentAngleRad - angularToleranceRad;
    if (minimumTangentAngleRad <= 0.0)
        return false;

    const double minimumAxisSupport = std::sin(minimumTangentAngleRad);
    errorMm = radiusMm * (1.0 / minimumAxisSupport - 1.0 / axisSupport);
    return std::isfinite(errorMm) && errorMm >= 0.0;
}

CurveDepthEnvelopeStatusNative MapWorkingStatusToDepthNative(WorkingStatusNative status)
{
    switch (status)
    {
    case WorkingStatusNative::Hit: return CurveDepthEnvelopeStatusNative::Success;
    case WorkingStatusNative::Miss: return CurveDepthEnvelopeStatusNative::Miss;
    case WorkingStatusNative::Tangent: return CurveDepthEnvelopeStatusNative::Tangent;
    case WorkingStatusNative::InvalidInput: return CurveDepthEnvelopeStatusNative::InvalidInput;
    case WorkingStatusNative::StaleContext: return CurveDepthEnvelopeStatusNative::StaleContext;
    case WorkingStatusNative::GeometryFailure: return CurveDepthEnvelopeStatusNative::GeometryFailure;
    default: return CurveDepthEnvelopeStatusNative::Unknown;
    }
}

/// <summary>곡선 표본을 세분해 전체 모델 first-hit과 원형 단면 깊이 envelope를 계산합니다.</summary>
NativeCurveDepthEnvelopeMeasurement MeasureCurveDepthEnvelopeNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    const std::vector<double>& centerlineSamples,
    const std::vector<int>& selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    const std::vector<double>& anchorPointValues,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumMeasuredDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    NativeCurveDepthEnvelopeMeasurement result;
    WorkingNative* context = FindWorking(document, contextId, revision);
    if (!context)
    {
        result.status = CurveDepthEnvelopeStatusNative::StaleContext;
        result.diagnostic = "깊이 질의의 작업 좌표계가 현재 모델 revision에 속하지 않습니다.";
        return result;
    }

    if (centerlineSamples.size() < 6 || centerlineSamples.size() % 3 != 0 || selectedFaceIds.empty() ||
        anchorPointValues.size() != 3 || !AllFinite(centerlineSamples) || !AllFinite(anchorPointValues) ||
        !std::isfinite(anchorU) || !std::isfinite(anchorV) ||
        !std::isfinite(wireDiameterMm) || wireDiameterMm <= 0.0 ||
        !std::isfinite(requiredInsertionDepthMm) || requiredInsertionDepthMm < 0.0 ||
        !std::isfinite(maximumSampleSpacingMm) || maximumSampleSpacingMm <= 0.0 ||
        !std::isfinite(requestedAngularToleranceRad) || requestedAngularToleranceRad <= 0.0 ||
        !std::isfinite(maximumMeasuredDeviationMm) || maximumMeasuredDeviationMm < 0.0 ||
        !std::isfinite(connectionToleranceMm) || connectionToleranceMm <= 0.0 || maximumProbeCount < 2)
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        result.diagnostic = "깊이 곡선 또는 질의 설정이 유효하지 않습니다.";
        return result;
    }

    std::set<int> uniqueFaceIds;
    bool containsAnchorFace = false;
    for (int faceId : selectedFaceIds)
    {
        if (faceId < 0 || faceId >= static_cast<int>(document.faces.size()))
        {
            result.status = CurveDepthEnvelopeStatusNative::InvalidFace;
            result.diagnostic = "선택 깊이 Face 번호가 현재 문서에 없습니다.";
            return result;
        }
        if (!uniqueFaceIds.insert(faceId).second)
        {
            result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
            result.diagnostic = "선택 깊이 Face 번호에 중복 항목이 있습니다.";
            return result;
        }
        containsAnchorFace |= faceId == anchorFaceId;
    }
    if (anchorFaceId < 0 || anchorFaceId >= static_cast<int>(document.faces.size()))
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidFace;
        result.diagnostic = "깊이 앵커 Face 번호가 현재 문서에 없습니다.";
        return result;
    }
    if (!containsAnchorFace)
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        result.diagnostic = "깊이 앵커 Face가 선택 Face 집합에 포함되지 않았습니다.";
        return result;
    }

    const double stationSpacingMm = maximumSampleSpacingMm / 2.0;
    if (!std::isfinite(stationSpacingMm) || stationSpacingMm <= 0.0)
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        result.diagnostic = "깊이 표본 간격이 유효하지 않습니다.";
        return result;
    }

    std::vector<gp_Pnt> stations;
    stations.reserve(std::min(static_cast<size_t>(maximumProbeCount), centerlineSamples.size()));
    stations.emplace_back(centerlineSamples[0], centerlineSamples[1], centerlineSamples[2]);
    for (size_t sampleIndex = 0; sampleIndex + 3 < centerlineSamples.size(); sampleIndex += 3)
    {
        const gp_Pnt start(
            centerlineSamples[sampleIndex], centerlineSamples[sampleIndex + 1], centerlineSamples[sampleIndex + 2]);
        const gp_Pnt end(
            centerlineSamples[sampleIndex + 3], centerlineSamples[sampleIndex + 4], centerlineSamples[sampleIndex + 5]);
        const double segmentLengthMm = start.Distance(end);
        if (!std::isfinite(segmentLengthMm) || segmentLengthMm <= Precision::Confusion())
        {
            result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
            result.diagnostic = "깊이 곡선 표본에 길이가 0이거나 유효하지 않은 구간이 있습니다.";
            return result;
        }

        const double divisionCountValue = std::ceil(segmentLengthMm / stationSpacingMm);
        if (!std::isfinite(divisionCountValue) || divisionCountValue < 1.0 ||
            divisionCountValue > maximumProbeCount ||
            stations.size() + static_cast<size_t>(divisionCountValue) > static_cast<size_t>(maximumProbeCount))
        {
            result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
            result.diagnostic = "깊이 envelope 표본 생성이 설정된 질의 한도를 넘습니다.";
            return result;
        }

        const int divisionCount = static_cast<int>(divisionCountValue);
        const gp_Vec segment(start, end);
        for (int division = 1; division <= divisionCount; ++division)
        {
            gp_Vec offset = segment;
            offset *= static_cast<double>(division) / divisionCount;
            stations.push_back(start.Translated(offset));
        }
    }
    if (stations.size() + 1 > static_cast<size_t>(maximumProbeCount))
    {
        result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
        result.diagnostic = "깊이 envelope 앵커와 초기 표본이 설정된 질의 한도를 넘습니다.";
        return result;
    }

    const double maximumTurnRad = requestedAngularToleranceRad + 1e-6;
    for (size_t stationIndex = 1; stationIndex + 1 < stations.size(); ++stationIndex)
    {
        gp_Vec incoming(stations[stationIndex - 1], stations[stationIndex]);
        gp_Vec outgoing(stations[stationIndex], stations[stationIndex + 1]);
        if (incoming.Magnitude() <= Precision::Confusion() || outgoing.Magnitude() <= Precision::Confusion())
        {
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "깊이 표본에서 유효한 곡선 접선을 만들 수 없습니다.";
            return result;
        }
        incoming.Normalize();
        outgoing.Normalize();
        const double cosine = std::max(-1.0, std::min(1.0, incoming.Dot(outgoing)));
        if (std::acos(cosine) > maximumTurnRad)
        {
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "깊이 표본에 허용 각도를 넘는 접선 꺾임이 있습니다.";
            return result;
        }
    }

    double anchorCoordinates[3] = {};
    int actualAnchorFaceId = -1;
    const WorkingStatusNative anchorStatus = QueryWorkingNative(
        document,
        contextId,
        revision,
        anchorU,
        anchorV,
        anchorCoordinates,
        actualAnchorFaceId);
    if (anchorStatus != WorkingStatusNative::Hit)
    {
        result.status = MapWorkingStatusToDepthNative(anchorStatus);
        result.diagnostic = "깊이 앵커의 전체 모델 first-hit을 확정하지 못했습니다.";
        return result;
    }
    if (actualAnchorFaceId != anchorFaceId)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 앵커 first-hit Face가 저장된 안정 Face와 다릅니다.";
        return result;
    }
    const gp_Pnt expectedAnchor(anchorPointValues[0], anchorPointValues[1], anchorPointValues[2]);
    const gp_Pnt measuredAnchor(anchorCoordinates[0], anchorCoordinates[1], anchorCoordinates[2]);
    if (expectedAnchor.Distance(measuredAnchor) > connectionToleranceMm)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 앵커 first-hit 표면점이 저장 위치에서 허용오차 밖으로 이동했습니다.";
        return result;
    }

    const double radiusMm = wireDiameterMm / 2.0;
    const gp_Vec axis(context->n);
    auto measureStation = [&](const gp_Pnt& centerlinePoint, const gp_Vec& tangentVector,
        NativeCurveDepthStation& measurement) -> CurveDepthEnvelopeStatusNative
    {
        if (!std::isfinite(tangentVector.SquareMagnitude()) || tangentVector.Magnitude() <= Precision::Confusion())
        {
            result.diagnostic = "깊이 표본의 진행 접선을 계산할 수 없습니다.";
            return CurveDepthEnvelopeStatusNative::Unresolved;
        }

        gp_Vec tangent = tangentVector;
        tangent.Normalize();
        const double tangentAlongAxis = std::max(-1.0, std::min(1.0, tangent.Dot(axis)));
        const gp_Vec projectedAxis = axis - tangent * tangentAlongAxis;
        const double axisSupport = projectedAxis.Magnitude();
        if (!std::isfinite(axisSupport) || axisSupport <= 1e-12)
        {
            result.diagnostic = "작업축과 곡선 접선이 평행해 원형 단면 깊이를 정할 수 없습니다.";
            return CurveDepthEnvelopeStatusNative::Unresolved;
        }

        const gp_Pnt wireOutsidePoint = centerlinePoint.Translated(axis * (radiusMm / axisSupport));
        const gp_Vec offset(context->origin, centerlinePoint);
        const gp_Vec xAxis(context->x);
        const gp_Vec yAxis(context->y);
        const double u = offset.Dot(xAxis);
        const double v = offset.Dot(yAxis);
        double surfaceCoordinates[3] = {};
        int surfaceFaceId = -1;
        const WorkingStatusNative surfaceStatus = QueryWorkingNative(
            document,
            contextId,
            revision,
            u,
            v,
            surfaceCoordinates,
            surfaceFaceId);
        if (surfaceStatus != WorkingStatusNative::Hit)
        {
            result.diagnostic = "깊이 표본의 전체 모델 first-hit을 계산하지 못했습니다.";
            return MapWorkingStatusToDepthNative(surfaceStatus);
        }
        if (uniqueFaceIds.find(surfaceFaceId) == uniqueFaceIds.end())
        {
            result.diagnostic = "깊이 표본의 전체 모델 first-hit이 선택 Face 집합 밖에 있습니다.";
            return CurveDepthEnvelopeStatusNative::Unresolved;
        }

        const gp_Pnt surfacePoint(surfaceCoordinates[0], surfaceCoordinates[1], surfaceCoordinates[2]);
        const double depthMm = gp_Vec(wireOutsidePoint, surfacePoint).Dot(axis);
        if (!std::isfinite(depthMm) || depthMm < 0.0)
        {
            result.diagnostic = "도체 바깥면이 표면보다 바깥에 있거나 깊이가 유효하지 않습니다.";
            return CurveDepthEnvelopeStatusNative::Unresolved;
        }

        measurement.centerlinePoint = centerlinePoint;
        measurement.surfacePoint = surfacePoint;
        measurement.wireOutsidePoint = wireOutsidePoint;
        measurement.depthMm = depthMm;
        measurement.axisSupport = axisSupport;
        return CurveDepthEnvelopeStatusNative::Success;
    };

    std::vector<NativeCurveDepthStation> initialMeasurements;
    initialMeasurements.reserve(stations.size());
    for (size_t stationIndex = 0; stationIndex < stations.size(); ++stationIndex)
    {
        gp_Vec tangent;
        if (stationIndex == 0)
            tangent = gp_Vec(stations[0], stations[1]);
        else if (stationIndex + 1 == stations.size())
            tangent = gp_Vec(stations[stationIndex - 1], stations[stationIndex]);
        else
            tangent = gp_Vec(stations[stationIndex - 1], stations[stationIndex + 1]);

        NativeCurveDepthStation measurement;
        const CurveDepthEnvelopeStatusNative status = measureStation(stations[stationIndex], tangent, measurement);
        if (status != CurveDepthEnvelopeStatusNative::Success)
        {
            result.status = status;
            return result;
        }
        initialMeasurements.push_back(measurement);
    }

    int probeCount = static_cast<int>(initialMeasurements.size()) + 1;
    if (probeCount > maximumProbeCount)
    {
        result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
        result.diagnostic = "앵커 first-hit을 포함한 깊이 질의가 설정된 한도를 넘습니다.";
        return result;
    }

    double maximumInterpolationResidualMm = 0.0;
    std::vector<NativeCurveDepthStation> refinedMeasurements;
    refinedMeasurements.reserve(initialMeasurements.size() * 2);
    refinedMeasurements.push_back(initialMeasurements.front());
    std::function<bool(const NativeCurveDepthStation&, const NativeCurveDepthStation&, int)> refineInterval;
    refineInterval = [&](const NativeCurveDepthStation& start, const NativeCurveDepthStation& end,
        int refinementDepth) -> bool
    {
        if (probeCount >= maximumProbeCount)
        {
            result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
            result.diagnostic = "중점 깊이 정밀화가 설정된 표면 질의 한도를 넘습니다.";
            return false;
        }
        const gp_Pnt midpointPoint(
            HeatingMoldNativeGeometry::StableMidpoint(
                start.centerlinePoint.X(), end.centerlinePoint.X()),
            HeatingMoldNativeGeometry::StableMidpoint(
                start.centerlinePoint.Y(), end.centerlinePoint.Y()),
            HeatingMoldNativeGeometry::StableMidpoint(
                start.centerlinePoint.Z(), end.centerlinePoint.Z()));
        const gp_Vec midpointTangent(start.centerlinePoint, end.centerlinePoint);
        NativeCurveDepthStation midpointMeasurement;
        const CurveDepthEnvelopeStatusNative midpointStatus = measureStation(
            midpointPoint,
            midpointTangent,
            midpointMeasurement);
        if (midpointStatus != CurveDepthEnvelopeStatusNative::Success)
        {
            result.status = midpointStatus;
            return false;
        }
        ++probeCount;

        const double linearDepthMm = HeatingMoldNativeGeometry::StableMidpoint(
            start.depthMm,
            end.depthMm);
        const double residualMm = std::fabs(midpointMeasurement.depthMm - linearDepthMm);
        if (!std::isfinite(residualMm))
        {
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "중점 깊이의 보간 잔차가 유효하지 않습니다.";
            return false;
        }
        if (residualMm > connectionToleranceMm / 4.0)
        {
            if (refinementDepth >= 18)
            {
                result.status = CurveDepthEnvelopeStatusNative::Unresolved;
                result.diagnostic = "깊이 중점 잔차가 정밀화 깊이 한도보다 커 전체 범위를 확정하지 못했습니다.";
                return false;
            }
            return refineInterval(start, midpointMeasurement, refinementDepth + 1) &&
                refineInterval(midpointMeasurement, end, refinementDepth + 1);
        }

        maximumInterpolationResidualMm = std::max(maximumInterpolationResidualMm, residualMm);
        refinedMeasurements.push_back(midpointMeasurement);
        refinedMeasurements.push_back(end);
        return true;
    };

    for (size_t stationIndex = 0; stationIndex + 1 < initialMeasurements.size(); ++stationIndex)
    {
        if (!refineInterval(initialMeasurements[stationIndex], initialMeasurements[stationIndex + 1], 0))
            return result;
    }
    if (refinedMeasurements.size() < 2)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 envelope 표본이 두 개보다 적습니다.";
        return result;
    }

    size_t minimumIndex = 0;
    size_t maximumIndex = 0;
    for (size_t stationIndex = 1; stationIndex < refinedMeasurements.size(); ++stationIndex)
    {
        if (refinedMeasurements[stationIndex].depthMm < refinedMeasurements[minimumIndex].depthMm)
            minimumIndex = stationIndex;
        if (refinedMeasurements[stationIndex].depthMm > refinedMeasurements[maximumIndex].depthMm)
            maximumIndex = stationIndex;
    }

    const NativeCurveDepthStation& minimum = refinedMeasurements[minimumIndex];
    const NativeCurveDepthStation& maximum = refinedMeasurements[maximumIndex];
    double coordinateScale = 1.0;
    for (const NativeCurveDepthStation& measurement : refinedMeasurements)
    {
        const gp_Pnt points[] = { measurement.surfacePoint, measurement.wireOutsidePoint };
        for (const gp_Pnt& point : points)
        {
            coordinateScale = std::max(coordinateScale, std::fabs(point.X()));
            coordinateScale = std::max(coordinateScale, std::fabs(point.Y()));
            coordinateScale = std::max(coordinateScale, std::fabs(point.Z()));
        }
    }
    const double numericErrorMm = (coordinateScale + radiusMm + maximum.depthMm + 1.0) *
        std::numeric_limits<double>::epsilon() * 256.0;
    double minimumAxisSupport = 1.0;
    for (const NativeCurveDepthStation& measurement : refinedMeasurements)
        minimumAxisSupport = std::min(minimumAxisSupport, measurement.axisSupport);
    double tangentSupportErrorMm = 0.0;
    if (!TryEstimateAxisRayAngularError(
            radiusMm,
            minimumAxisSupport,
            requestedAngularToleranceRad,
            tangentSupportErrorMm))
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "접선 각도 공차 범위에 작업축 평행 방향이 포함되어 깊이 오차를 제한할 수 없습니다.";
        return result;
    }
    const double errorBoundMm = std::max(
        connectionToleranceMm,
        maximumMeasuredDeviationMm + tangentSupportErrorMm +
        maximumInterpolationResidualMm * 2.0 + numericErrorMm);
    if (!std::isfinite(errorBoundMm))
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 envelope 오차 상한이 유효하지 않습니다.";
        return result;
    }

    const bool minimumIsWorst = std::fabs(minimum.depthMm - requiredInsertionDepthMm) >=
        std::fabs(maximum.depthMm - requiredInsertionDepthMm);
    result.status = CurveDepthEnvelopeStatusNative::Success;
    result.measuredDepthMm = (minimum.depthMm + maximum.depthMm) / 2.0;
    result.minimumDepthMm = minimum.depthMm;
    result.maximumDepthMm = maximum.depthMm;
    result.errorBoundMm = errorBoundMm;
    result.surfacePoint = minimumIsWorst ? minimum.surfacePoint : maximum.surfacePoint;
    result.minimumSurfacePoint = minimum.surfacePoint;
    result.maximumSurfacePoint = maximum.surfacePoint;
    result.minimumWireOutsidePoint = minimum.wireOutsidePoint;
    result.maximumWireOutsidePoint = maximum.wireOutsidePoint;
    result.probeCount = probeCount;
    return result;
}

/// <summary>전체 모델 first-hit 표면 경로에서 제한 구간 root 보정으로 최종 중심선을 만듭니다.</summary>
NativeCurveDepthAdjustment AdjustCurveInsertionDepthNative(
    NativeDocumentSession& document,
    long long contextId,
    long long revision,
    const std::vector<double>& workingPathSamples,
    const std::vector<int>& selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    const std::vector<double>& anchorPointValues,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumSurfaceDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    NativeCurveDepthAdjustment result;
    WorkingNative* context = FindWorking(document, contextId, revision);
    if (!context)
    {
        result.status = CurveDepthEnvelopeStatusNative::StaleContext;
        result.diagnostic = "깊이 보정 작업 좌표계가 현재 모델 revision에 속하지 않습니다.";
        return result;
    }

    if (workingPathSamples.size() < 4 || workingPathSamples.size() % 2 != 0 ||
        selectedFaceIds.empty() || anchorPointValues.size() != 3 ||
        !AllFinite(workingPathSamples) || !AllFinite(anchorPointValues) ||
        !std::isfinite(anchorU) || !std::isfinite(anchorV) ||
        !std::isfinite(wireDiameterMm) || wireDiameterMm <= 0.0 ||
        !std::isfinite(requiredInsertionDepthMm) || requiredInsertionDepthMm < 0.0 ||
        !std::isfinite(maximumSampleSpacingMm) || maximumSampleSpacingMm <= 0.0 ||
        !std::isfinite(requestedAngularToleranceRad) || requestedAngularToleranceRad <= 0.0 ||
        !std::isfinite(maximumSurfaceDeviationMm) || maximumSurfaceDeviationMm < 0.0 ||
        !std::isfinite(connectionToleranceMm) || connectionToleranceMm <= 0.0 || maximumProbeCount < 2)
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        result.diagnostic = "깊이 보정 경로·치수·허용오차 또는 작업 한도가 유효하지 않습니다.";
        return result;
    }

    std::set<int> uniqueFaceIds;
    bool containsAnchorFace = false;
    for (int faceId : selectedFaceIds)
    {
        if (faceId < 0 || faceId >= static_cast<int>(document.faces.size()))
        {
            result.status = CurveDepthEnvelopeStatusNative::InvalidFace;
            result.diagnostic = "깊이 보정 선택 Face 번호가 현재 문서에 없습니다.";
            return result;
        }
        if (!uniqueFaceIds.insert(faceId).second)
        {
            result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
            result.diagnostic = "깊이 보정 선택 Face 집합에 중복 항목이 있습니다.";
            return result;
        }
        containsAnchorFace |= faceId == anchorFaceId;
    }
    if (anchorFaceId < 0 || anchorFaceId >= static_cast<int>(document.faces.size()))
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidFace;
        result.diagnostic = "깊이 보정 앵커 Face 번호가 현재 문서에 없습니다.";
        return result;
    }
    if (!containsAnchorFace)
    {
        result.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        result.diagnostic = "깊이 보정 선택 Face 집합에 앵커 Face가 없습니다.";
        return result;
    }

    double anchorCoordinates[3] = {};
    int actualAnchorFaceId = -1;
    const WorkingStatusNative anchorStatus = QueryWorkingNative(
        document, contextId, revision, anchorU, anchorV, anchorCoordinates, actualAnchorFaceId);
    if (anchorStatus != WorkingStatusNative::Hit)
    {
        result.status = MapWorkingStatusToDepthNative(anchorStatus);
        result.diagnostic = "깊이 보정 앵커의 전체 모델 first-hit을 확인하지 못했습니다.";
        return result;
    }
    if (actualAnchorFaceId != anchorFaceId)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 보정 앵커의 first-hit Face가 저장된 Face와 다릅니다.";
        return result;
    }
    const gp_Pnt expectedAnchor(anchorPointValues[0], anchorPointValues[1], anchorPointValues[2]);
    const gp_Pnt measuredAnchor(anchorCoordinates[0], anchorCoordinates[1], anchorCoordinates[2]);
    if (expectedAnchor.Distance(measuredAnchor) > connectionToleranceMm)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 보정 앵커가 현재 표면에서 연결 공차 밖으로 이동했습니다.";
        return result;
    }

    const double stationSpacingMm = maximumSampleSpacingMm / 2.0;
    std::vector<std::pair<double, double>> workingStations;
    workingStations.reserve(std::min(static_cast<size_t>(maximumProbeCount), workingPathSamples.size()));
    workingStations.emplace_back(workingPathSamples[0], workingPathSamples[1]);
    for (size_t sampleIndex = 0; sampleIndex + 2 < workingPathSamples.size(); sampleIndex += 2)
    {
        const double u0 = workingPathSamples[sampleIndex];
        const double v0 = workingPathSamples[sampleIndex + 1];
        const double u1 = workingPathSamples[sampleIndex + 2];
        const double v1 = workingPathSamples[sampleIndex + 3];
        const double segmentLengthMm = std::hypot(u1 - u0, v1 - v0);
        const double divisionCountValue = std::ceil(segmentLengthMm / stationSpacingMm);
        if (!std::isfinite(segmentLengthMm) || segmentLengthMm <= Precision::Confusion() ||
            !std::isfinite(divisionCountValue) || divisionCountValue < 1.0 ||
            divisionCountValue > maximumProbeCount ||
            workingStations.size() + static_cast<size_t>(divisionCountValue) > static_cast<size_t>(maximumProbeCount))
        {
            result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
            result.diagnostic = "깊이 root 보정의 표면 표본 생성이 질의 한도를 넘었습니다.";
            return result;
        }

        const int divisionCount = static_cast<int>(divisionCountValue);
        for (int division = 1; division <= divisionCount; ++division)
        {
            const double fraction = static_cast<double>(division) / divisionCount;
            workingStations.emplace_back(
                u0 + (u1 - u0) * fraction,
                v0 + (v1 - v0) * fraction);
        }
    }

    if (workingStations.size() < 2 || workingStations.size() + 1 > static_cast<size_t>(maximumProbeCount))
    {
        result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
        result.diagnostic = "앵커와 깊이 경로 표면 질의가 설정된 작업 한도를 넘었습니다.";
        return result;
    }

    std::vector<gp_Pnt> surfacePoints;
    surfacePoints.reserve(workingStations.size());
    result.probeCount = 1;
    for (const auto& station : workingStations)
    {
        double coordinates[3] = {};
        int faceId = -1;
        const WorkingStatusNative status = QueryWorkingNative(
            document, contextId, revision, station.first, station.second, coordinates, faceId);
        if (status != WorkingStatusNative::Hit)
        {
            result.status = MapWorkingStatusToDepthNative(status);
            result.diagnostic = "깊이 경로 표본의 전체 모델 first-hit을 계산하지 못했습니다.";
            return result;
        }
        if (uniqueFaceIds.find(faceId) == uniqueFaceIds.end())
        {
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "깊이 경로 표본의 전체 모델 first-hit이 선택 Face 집합 밖에 있습니다.";
            return result;
        }

        surfacePoints.emplace_back(coordinates[0], coordinates[1], coordinates[2]);
        ++result.probeCount;
    }

    const double radiusMm = wireDiameterMm / 2.0;
    const double lowerOffsetMm = requiredInsertionDepthMm;
    const double rootToleranceMm = std::max(1e-8, connectionToleranceMm * 0.1);
    constexpr long long MaximumRootEvaluations = 2'000'000;
    long long rootEvaluationCount = 0;
    bool rootEvaluationBudgetExceeded = false;
    const gp_Vec workingAxis(context->n);
    std::vector<double> offsetsMm(surfacePoints.size(), lowerOffsetMm + radiusMm);

    auto pointAtOffset = [&](size_t index, double offsetMm) -> gp_Pnt
    {
        return surfacePoints[index].Translated(workingAxis * -offsetMm);
    };
    auto residualAtOffset = [&](size_t index, double offsetMm, double& residualMm) -> bool
    {
        if (++rootEvaluationCount > MaximumRootEvaluations)
        {
            rootEvaluationBudgetExceeded = true;
            return false;
        }
        const gp_Pnt center = pointAtOffset(index, offsetMm);
        gp_Vec tangent;
        if (index == 0)
            tangent = gp_Vec(center, pointAtOffset(1, offsetsMm[1]));
        else if (index + 1 == surfacePoints.size())
            tangent = gp_Vec(pointAtOffset(index - 1, offsetsMm[index - 1]), center);
        else
            tangent = gp_Vec(pointAtOffset(index - 1, offsetsMm[index - 1]),
                pointAtOffset(index + 1, offsetsMm[index + 1]));

        if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= Precision::Confusion())
            return false;
        tangent.Normalize();
        const double tangentAlongAxis = std::max(-1.0, std::min(1.0, tangent.Dot(workingAxis)));
        const double axisSupport = std::sqrt(std::max(0.0, 1.0 - tangentAlongAxis * tangentAlongAxis));
        if (!std::isfinite(axisSupport) || axisSupport <= 1e-10)
            return false;

        const double targetOffsetMm = requiredInsertionDepthMm + radiusMm / axisSupport;
        residualMm = offsetMm - targetOffsetMm;
        return std::isfinite(residualMm);
    };

    auto solveStationOffset = [&](size_t index, double& solvedOffsetMm) -> bool
    {
        double lowerResidualMm = 0.0;
        if (index > 0 && index + 1 < offsetsMm.size())
        {
            if (!residualAtOffset(index, lowerOffsetMm, lowerResidualMm))
                return false;

            solvedOffsetMm = lowerOffsetMm - lowerResidualMm;
            if (!std::isfinite(solvedOffsetMm) || solvedOffsetMm < lowerOffsetMm)
                return false;

            double solvedResidualMm = 0.0;
            return residualAtOffset(index, solvedOffsetMm, solvedResidualMm) &&
                std::fabs(solvedResidualMm) <= rootToleranceMm;
        }

        double upperResidualMm = 0.0;
        if (!residualAtOffset(index, lowerOffsetMm, lowerResidualMm) ||
            lowerResidualMm > rootToleranceMm)
        {
            return false;
        }

        double lower = lowerOffsetMm;
        double upper = std::max(lowerOffsetMm + radiusMm, offsetsMm[index]);
        if (!std::isfinite(upper) || upper <= lower ||
            !residualAtOffset(index, upper, upperResidualMm))
        {
            return false;
        }

        for (int bracketIteration = 0;
            upperResidualMm < -rootToleranceMm && bracketIteration < 64;
            ++bracketIteration)
        {
            const double bracketWidth = upper - lower;
            if (!std::isfinite(bracketWidth) || bracketWidth <= 0.0 ||
                bracketWidth > std::numeric_limits<double>::max() / 2.0)
            {
                return false;
            }
            upper = lower + bracketWidth * 2.0;
            if (!std::isfinite(upper) || !residualAtOffset(index, upper, upperResidualMm))
                return false;
        }
        if (upperResidualMm < -rootToleranceMm)
            return false;
        if (std::fabs(lowerResidualMm) <= rootToleranceMm)
        {
            solvedOffsetMm = lower;
            return true;
        }
        if (std::fabs(upperResidualMm) <= rootToleranceMm)
        {
            solvedOffsetMm = upper;
            return true;
        }

        double lowerValue = lowerResidualMm;
        for (int rootIteration = 0; rootIteration < 96; ++rootIteration)
        {
            const double midpoint = lower + (upper - lower) / 2.0;
            double midpointResidual = 0.0;
            if (!residualAtOffset(index, midpoint, midpointResidual))
                return false;
            if (std::fabs(midpointResidual) <= rootToleranceMm)
            {
                solvedOffsetMm = midpoint;
                return true;
            }

            if ((midpointResidual < 0.0) == (lowerValue < 0.0))
            {
                lower = midpoint;
                lowerValue = midpointResidual;
            }
            else
            {
                upper = midpoint;
            }
        }

        solvedOffsetMm = lower + (upper - lower) / 2.0;
        double finalResidualMm = 0.0;
        return residualAtOffset(index, solvedOffsetMm, finalResidualMm) &&
            std::fabs(finalResidualMm) <= rootToleranceMm;
    };

    bool converged = false;
    double maximumResidualMm = std::numeric_limits<double>::infinity();
    for (int sweep = 0; sweep < 64; ++sweep)
    {
        for (size_t index = 0; index < offsetsMm.size(); ++index)
        {
            double rootOffsetMm = 0.0;
            if (!solveStationOffset(index, rootOffsetMm))
            {
                result.status = rootEvaluationBudgetExceeded
                    ? CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded
                    : CurveDepthEnvelopeStatusNative::Unresolved;
                result.diagnostic = rootEvaluationBudgetExceeded
                    ? "깊이 root 계산이 설정된 함수 평가 한도를 넘었습니다."
                    : "깊이 방정식의 root가 제한 구간 안에서 안정적으로 bracket 되지 않았습니다.";
                return result;
            }
            offsetsMm[index] = HeatingMoldNativeGeometry::StableMidpoint(
                offsetsMm[index],
                rootOffsetMm);
        }

        maximumResidualMm = 0.0;
        for (size_t index = 0; index < offsetsMm.size(); ++index)
        {
            double residualMm = 0.0;
            if (!residualAtOffset(index, offsetsMm[index], residualMm))
            {
                result.status = rootEvaluationBudgetExceeded
                    ? CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded
                    : CurveDepthEnvelopeStatusNative::Unresolved;
                result.diagnostic = rootEvaluationBudgetExceeded
                    ? "깊이 root 계산이 설정된 함수 평가 한도를 넘었습니다."
                    : "깊이 보정 뒤 최종 곡선 접선의 축방향 ray 교점을 계산하지 못했습니다.";
                return result;
            }
            maximumResidualMm = std::max(maximumResidualMm, std::fabs(residualMm));
        }
        if (maximumResidualMm <= rootToleranceMm)
        {
            converged = true;
            break;
        }
    }
    if (!converged)
    {
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 root 보정이 제한 반복 안에서 수렴하지 않았습니다.";
        return result;
    }

    std::vector<gp_Pnt> centerline;
    centerline.reserve(surfacePoints.size());
    for (size_t index = 0; index < surfacePoints.size(); ++index)
        centerline.push_back(pointAtOffset(index, offsetsMm[index]));

    std::vector<double> cumulativeLengthMm(centerline.size(), 0.0);
    for (size_t index = 1; index < centerline.size(); ++index)
    {
        const double segmentLengthMm = centerline[index - 1].Distance(centerline[index]);
        if (!std::isfinite(segmentLengthMm) || segmentLengthMm <= Precision::Confusion())
        {
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "깊이 보정 결과에 길이가 0이거나 유효하지 않은 중심선 구간이 있습니다.";
            return result;
        }
        cumulativeLengthMm[index] = cumulativeLengthMm[index - 1] + segmentLengthMm;
    }

    auto segmentDistanceSquared = [](const gp_Pnt& firstStart, const gp_Pnt& firstEnd,
        const gp_Pnt& secondStart, const gp_Pnt& secondEnd) -> double
    {
        const gp_Vec first(firstStart, firstEnd);
        const gp_Vec second(secondStart, secondEnd);
        const gp_Vec between(firstStart, secondStart);
        const double a = first.Dot(first);
        const double b = first.Dot(second);
        const double c = second.Dot(second);
        const double d = first.Dot(between);
        const double e = second.Dot(between);
        const double denominator = a * c - b * b;
        double firstNumerator = 0.0;
        double firstDenominator = denominator;
        double secondNumerator = 0.0;
        double secondDenominator = denominator;
        if (denominator <= Precision::Confusion() * Precision::Confusion())
        {
            firstNumerator = 0.0;
            firstDenominator = 1.0;
            secondNumerator = e;
            secondDenominator = c;
        }
        else
        {
            firstNumerator = b * e - c * d;
            secondNumerator = a * e - b * d;
            if (firstNumerator < 0.0)
            {
                firstNumerator = 0.0;
                secondNumerator = e;
                secondDenominator = c;
            }
            else if (firstNumerator > firstDenominator)
            {
                firstNumerator = firstDenominator;
                secondNumerator = e + b;
                secondDenominator = c;
            }
        }
        if (secondNumerator < 0.0)
        {
            secondNumerator = 0.0;
            if (-d < 0.0)
                firstNumerator = 0.0;
            else if (-d > a)
                firstNumerator = firstDenominator;
            else
            {
                firstNumerator = -d;
                firstDenominator = a;
            }
        }
        else if (secondNumerator > secondDenominator)
        {
            secondNumerator = secondDenominator;
            if (-d + b < 0.0)
                firstNumerator = 0.0;
            else if (-d + b > a)
                firstNumerator = firstDenominator;
            else
            {
                firstNumerator = -d + b;
                firstDenominator = a;
            }
        }
        const double firstParameter = std::fabs(firstNumerator) <= Precision::Confusion()
            ? 0.0 : firstNumerator / firstDenominator;
        const double secondParameter = std::fabs(secondNumerator) <= Precision::Confusion()
            ? 0.0 : secondNumerator / secondDenominator;
        gp_Vec difference = between + first * firstParameter - second * secondParameter;
        return difference.SquareMagnitude();
    };

    long long collisionChecks = 0;
    constexpr long long MaximumCollisionChecks = 2'000'000;
    const double localArcExclusionMm = 2.0 * radiusMm + 2.0 * maximumSampleSpacingMm;
    for (size_t firstIndex = 0; firstIndex + 1 < centerline.size(); ++firstIndex)
    {
        const double firstMidArcMm = (cumulativeLengthMm[firstIndex] + cumulativeLengthMm[firstIndex + 1]) / 2.0;
        for (size_t secondIndex = firstIndex + 2; secondIndex + 1 < centerline.size(); ++secondIndex)
        {
            const double secondMidArcMm = (cumulativeLengthMm[secondIndex] + cumulativeLengthMm[secondIndex + 1]) / 2.0;
            if (secondMidArcMm - firstMidArcMm <= localArcExclusionMm)
                continue;
            if (++collisionChecks > MaximumCollisionChecks)
            {
                result.status = CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded;
                result.diagnostic = "도체 외피의 비국소 자기교차 확인이 pair 한도를 넘었습니다.";
                return result;
            }
            const double distanceSquared = segmentDistanceSquared(
                centerline[firstIndex], centerline[firstIndex + 1],
                centerline[secondIndex], centerline[secondIndex + 1]);
            const double minimumCenterlineDistanceMm = std::max(0.0, 2.0 * radiusMm - connectionToleranceMm);
            if (!std::isfinite(distanceSquared) || distanceSquared < 0.0)
            {
                result.status = CurveDepthEnvelopeStatusNative::Unresolved;
                result.diagnostic = "도체 외피 자기교차 확인에서 선분 간 최단거리를 계산하지 못했습니다.";
                return result;
            }
            if (distanceSquared < minimumCenterlineDistanceMm * minimumCenterlineDistanceMm)
            {
                result.status = CurveDepthEnvelopeStatusNative::Unresolved;
                result.diagnostic = "깊이 보정된 비국소 도체 외피가 서로 교차하거나 접촉합니다.";
                return result;
            }
        }
    }

    double coordinateScale = 1.0;
    for (const gp_Pnt& point : centerline)
    {
        result.centerlineSamples.push_back(point.X());
        result.centerlineSamples.push_back(point.Y());
        result.centerlineSamples.push_back(point.Z());
        coordinateScale = std::max(coordinateScale, std::fabs(point.X()));
        coordinateScale = std::max(coordinateScale, std::fabs(point.Y()));
        coordinateScale = std::max(coordinateScale, std::fabs(point.Z()));
    }
    if (!AllFinite(result.centerlineSamples))
    {
        result.centerlineSamples.clear();
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 보정 중심선이 유한한 모델 좌표 범위를 벗어났습니다.";
        return result;
    }

    double minimumAxisSupport = 1.0;
    for (size_t index = 0; index < centerline.size(); ++index)
    {
        gp_Vec tangent;
        if (index == 0)
            tangent = gp_Vec(centerline[0], centerline[1]);
        else if (index + 1 == centerline.size())
            tangent = gp_Vec(centerline[index - 1], centerline[index]);
        else
            tangent = gp_Vec(centerline[index - 1], centerline[index + 1]);

        if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= Precision::Confusion())
        {
            result.centerlineSamples.clear();
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "최종 중심선에서 깊이 오차에 사용할 접선을 계산하지 못했습니다.";
            return result;
        }
        tangent.Normalize();
        const double tangentAlongAxis = std::max(-1.0, std::min(1.0, tangent.Dot(workingAxis)));
        const double axisSupport = (workingAxis - tangent * tangentAlongAxis).Magnitude();
        if (!std::isfinite(axisSupport) || axisSupport <= 1e-10)
        {
            result.centerlineSamples.clear();
            result.status = CurveDepthEnvelopeStatusNative::Unresolved;
            result.diagnostic = "작업축과 최종 접선이 평행해 깊이 오차를 제한할 수 없습니다.";
            return result;
        }
        minimumAxisSupport = std::min(minimumAxisSupport, axisSupport);
    }

    double tangentSupportErrorMm = 0.0;
    if (!TryEstimateAxisRayAngularError(
            radiusMm,
            minimumAxisSupport,
            requestedAngularToleranceRad,
            tangentSupportErrorMm))
    {
        result.centerlineSamples.clear();
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "접선 각도 공차 범위에 작업축 평행 방향이 포함되어 깊이 오차를 제한할 수 없습니다.";
        return result;
    }
    const double numericErrorMm = (coordinateScale + radiusMm + requiredInsertionDepthMm + 1.0) *
        std::numeric_limits<double>::epsilon() * 256.0;
    result.maximumDepthResidualMm = maximumResidualMm;
    result.errorBoundMm = maximumSurfaceDeviationMm + tangentSupportErrorMm + maximumResidualMm + numericErrorMm;
    if (!std::isfinite(result.errorBoundMm) || result.errorBoundMm < result.maximumDepthResidualMm)
    {
        result.centerlineSamples.clear();
        result.status = CurveDepthEnvelopeStatusNative::Unresolved;
        result.diagnostic = "깊이 보정 오차 근거가 유효하지 않습니다.";
        return result;
    }

    result.status = CurveDepthEnvelopeStatusNative::Success;
    return result;
}

bool WorkingVisibleNative(long long id, long long revision, double x,double y,double z,double tolerance)
{
    auto* ctx = FindWorking(id,revision);
    if (!ctx || g_view.IsNull() || !std::isfinite(tolerance) || tolerance<=0 ||
        !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
    try {
        const gp_Pnt target(x,y,z);
        const auto camera = g_view->Camera();
        if (!camera->IsOrthographic() && gp_Vec(camera->Eye(),target).Dot(gp_Vec(camera->Direction()))<=0) return false;
        gp_Dir direction = camera->Direction();
        gp_Pnt origin;
        if (camera->IsOrthographic()) {
            Bnd_Box box; BRepBndLib::Add(g_documentSession->shape,box);
            const double span = std::sqrt(box.SquareExtent())*2 + camera->Distance()+1;
            origin = target.Translated(gp_Vec(direction)*-span);
        } else {
            origin = camera->Eye(); direction = gp_Dir(gp_Vec(origin,target));
        }
        const double distance = origin.Distance(target);
        auto& hits = *ctx->intersector;
        hits.Perform(gp_Lin(origin,direction),0,distance+tolerance);
        if (!hits.IsDone()) return false;
        double first=1e100;
        for (int i=1;i<=hits.NbPnt();++i) {
            if (hits.State(i)!=TopAbs_IN && hits.State(i)!=TopAbs_ON) continue;
            if (!std::isfinite(hits.WParameter(i))) return false;
            if (hits.WParameter(i)>=0) first=std::min(first,hits.WParameter(i));
        }
        return std::fabs(first-distance)<=tolerance;
    } catch (const Standard_Failure&) { return false; }
}

