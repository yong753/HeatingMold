// Included after WorkingSideNative.inc, inside the native anonymous namespace.
using ExitEdgeStatusNative = HeatingMoldExitNative::Status;
struct ExitEdgeHitNative
{
    ExitEdgeStatusNative status = ExitEdgeStatusNative::InvalidInput;
    gp_Pnt point;
    gp_Vec tangent;
    gp_Pnt2d datum;
    double parameter = 0.0;
    int faceId = -1;
    long long vertexKey = 0;
};

ExitEdgeStatusNative ResolveExitReference(long long id, long long revision, const NativeExitEdgeRef& reference,
    HeatingMoldExitNative::ProjectedCurve*& curve, const StoredExitEdge*& stored, int& supportFaceId,
    bool legacyFullRange = false)
{
    curve = nullptr; stored = nullptr; supportFaceId = -1;
    WorkingNative* context = FindWorking(id, revision);
    if (!context || reference.revision != revision) return ExitEdgeStatusNative::StaleContext;
    const auto entry = g_exitEdges.find(reference.key);
    if (entry == g_exitEdges.end() || entry->second.edge.IsNull()) return ExitEdgeStatusNative::MissingReference;
    if (entry->second.revision != revision) return ExitEdgeStatusNative::StaleContext;
    stored = &entry->second;
    if (!std::isfinite(reference.firstParameter) || !std::isfinite(reference.lastParameter) ||
        reference.lastParameter <= reference.firstParameter) return ExitEdgeStatusNative::InvalidInput;
    bool bindingFound = false;
    for (const auto& binding : stored->bindings) {
        if (binding.faceId != reference.faceId || reference.firstParameter < binding.first ||
            reference.lastParameter > binding.last) continue;
        supportFaceId = binding.supportFaceId; bindingFound = true; break;
    }
    if (!bindingFound && !legacyFullRange) return ExitEdgeStatusNative::InvalidInput;
    if (!bindingFound) supportFaceId = stored->supportFaceId;
    auto cached = context->exitCurves.find(reference.key);
    if (cached == context->exitCurves.end()) {
        double first = 0.0, last = 0.0;
        const Handle(Geom_Curve) original = BRep_Tool::Curve(stored->edge, first, last);
        HeatingMoldExitNative::ProjectedCurve projected;
        const auto status = HeatingMoldExitNative::BuildProjectedCurve(original, first, last,
            context->origin, context->x, context->y, context->n, projected);
        if (status != ExitEdgeStatusNative::Hit) return status;
        cached = context->exitCurves.emplace(reference.key, projected).first;
    }
    curve = &cached->second;
    return HeatingMoldExitNative::ValidTrim(*curve, reference.firstParameter, reference.lastParameter)
        ? ExitEdgeStatusNative::Hit : ExitEdgeStatusNative::InvalidInput;
}

long long OriginalExitVertexKey(const StoredExitEdge& stored, const HeatingMoldExitNative::ProjectedCurve& curve,
    double parameter, const gp_Pnt& point)
{
    const double pe = HeatingMoldExitNative::ParameterEpsilon(curve.first, curve.last);
    TopoDS_Vertex first, last;
    TopExp::Vertices(stored.edge, first, last, Standard_False);
    if (std::fabs(parameter - curve.first) <= pe && !first.IsNull() &&
        point.Distance(BRep_Tool::Pnt(first)) <= HeatingMoldExitNative::GeometryEpsilon)
        return g_exitSourceVertices.FindIndex(first);
    if (std::fabs(parameter - curve.last) <= pe && !last.IsNull() &&
        point.Distance(BRep_Tool::Pnt(last)) <= HeatingMoldExitNative::GeometryEpsilon)
        return g_exitSourceVertices.FindIndex(last);
    return 0;
}

ExitEdgeStatusNative FillExitHit(const HeatingMoldExitNative::ProjectedCurve& curve, const StoredExitEdge& stored,
    const NativeExitEdgeRef& reference, int supportFaceId, double parameter, ExitEdgeHitNative& hit)
{
    hit.status = HeatingMoldExitNative::EvaluateParameter(curve, reference.firstParameter, reference.lastParameter,
        parameter, hit.point, hit.tangent, hit.datum);
    if (hit.status != ExitEdgeStatusNative::Hit) return hit.status;
    hit.parameter = parameter; // Evaluate C(t), never a different 3D projector's solution.
    hit.faceId = supportFaceId;
    hit.vertexKey = OriginalExitVertexKey(stored, curve, parameter, hit.point);
    return hit.status;
}

ExitEdgeHitNative ProjectExitEdgeNative(long long id, long long revision, const NativeExitEdgeRef& reference,
    double u, double v, double tolerance, bool legacyFullRange = false)
{
    ExitEdgeHitNative hit;
    if (!std::isfinite(u) || !std::isfinite(v) || !std::isfinite(tolerance) || tolerance < 0.0) return hit;
    try {
        HeatingMoldExitNative::ProjectedCurve* curve; const StoredExitEdge* stored; int support;
        hit.status = ResolveExitReference(id, revision, reference, curve, stored, support, legacyFullRange);
        if (hit.status != ExitEdgeStatusNative::Hit) return hit;
        double parameter = 0.0;
        hit.status = HeatingMoldExitNative::NearestParameter(*curve, reference.firstParameter, reference.lastParameter,
            gp_Pnt2d(u, v), tolerance, parameter);
        if (hit.status == ExitEdgeStatusNative::Hit) FillExitHit(*curve, *stored, reference, support, parameter, hit);
        return hit;
    } catch (...) { hit.status = ExitEdgeStatusNative::GeometryFailure; return hit; }
}

ExitEdgeHitNative EvaluateExitEdgeParameterNative(long long id, long long revision, const NativeExitEdgeRef& reference,
    double parameter)
{
    ExitEdgeHitNative hit;
    if (!std::isfinite(parameter)) return hit;
    try {
        HeatingMoldExitNative::ProjectedCurve* curve; const StoredExitEdge* stored; int support;
        hit.status = ResolveExitReference(id, revision, reference, curve, stored, support);
        if (hit.status == ExitEdgeStatusNative::Hit) FillExitHit(*curve, *stored, reference, support, parameter, hit);
        return hit;
    } catch (...) { hit.status = ExitEdgeStatusNative::GeometryFailure; return hit; }
}

ExitEdgeStatusNative IntersectExitEdgeRayNative(long long id, long long revision, const NativeExitEdgeRef& reference,
    double u, double v, double du, double dv, double tolerance, std::vector<ExitEdgeHitNative>& hits)
{
    hits.clear();
    try {
        HeatingMoldExitNative::ProjectedCurve* curve; const StoredExitEdge* stored; int support;
        auto status = ResolveExitReference(id, revision, reference, curve, stored, support);
        if (status != ExitEdgeStatusNative::Hit) return status;
        std::vector<double> parameters;
        status = HeatingMoldExitNative::IntersectRay(*curve, reference.firstParameter, reference.lastParameter,
            gp_Pnt2d(u, v), du, dv, tolerance, parameters);
        if (status != ExitEdgeStatusNative::Hit) return status;
        for (double parameter : parameters) {
            ExitEdgeHitNative hit;
            status = FillExitHit(*curve, *stored, reference, support, parameter, hit);
            if (status != ExitEdgeStatusNative::Hit) { hits.clear(); return status; }
            hits.push_back(hit);
        }
        return ExitEdgeStatusNative::Hit;
    } catch (...) { hits.clear(); return ExitEdgeStatusNative::GeometryFailure; }
}

ExitEdgeStatusNative IntersectExitEdgeLineNative(long long id, long long revision, const NativeExitEdgeRef& reference,
    double ax, double ay, double bx, double by, std::vector<ExitEdgeHitNative>& hits)
{
    hits.clear();
    if (!std::isfinite(ax) || !std::isfinite(ay) || !std::isfinite(bx) || !std::isfinite(by))
        return ExitEdgeStatusNative::InvalidInput;
    try {
        HeatingMoldExitNative::ProjectedCurve* curve; const StoredExitEdge* stored; int support;
        auto status = ResolveExitReference(id, revision, reference, curve, stored, support);
        if (status != ExitEdgeStatusNative::Hit) return status;
        std::vector<double> parameters;
        status = HeatingMoldExitNative::IntersectLine(*curve, reference.firstParameter, reference.lastParameter,
            gp_Pnt2d(ax, ay), gp_Pnt2d(bx, by), parameters);
        if (status != ExitEdgeStatusNative::Hit) return status;
        for (double parameter : parameters) {
            ExitEdgeHitNative hit;
            status = FillExitHit(*curve, *stored, reference, support, parameter, hit);
            if (status != ExitEdgeStatusNative::Hit) { hits.clear(); return status; }
            hits.push_back(hit);
        }
        return ExitEdgeStatusNative::Hit;
    } catch (...) { hits.clear(); return ExitEdgeStatusNative::GeometryFailure; }
}

ExitEdgeHitNative EvaluateExitEdgeLegacyNative(long long id, long long revision, long long edgeRevision,
    long long edgeKey, double u, double v, double tolerance)
{
    NativeExitEdgeRef reference;
    reference.revision = edgeRevision; reference.key = edgeKey;
    const auto entry = g_exitEdges.find(edgeKey);
    if (!FindWorking(id, revision) || edgeRevision != revision) {
        ExitEdgeHitNative hit; hit.status = ExitEdgeStatusNative::StaleContext; return hit;
    }
    if (entry == g_exitEdges.end()) {
        // Preserve this legacy API's historical unknown-key status.
        ExitEdgeHitNative hit; hit.status = ExitEdgeStatusNative::StaleContext; return hit;
    }
    reference.faceId = entry->second.faceId;
    BRep_Tool::Range(entry->second.edge, reference.firstParameter, reference.lastParameter);
    ExitEdgeHitNative hit = ProjectExitEdgeNative(id, revision, reference, u, v, tolerance, true);
    // 이 레거시 API는 역사적으로 인접 면(참조의 면)을 반환했다. 새 투영 질의는
    // 선택 지지면을 반환하므로, 여기서만 인접 면 계약을 복원한다.
    if (hit.status == ExitEdgeStatusNative::Hit) hit.faceId = entry->second.faceId;
    return hit;
}
