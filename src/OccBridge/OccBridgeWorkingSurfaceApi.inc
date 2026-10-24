long long Bridge::GetModelRevision()
{
    return g_documentSession->modelRevision;
}

void Bridge::ReleaseWorkingSurfaceContext(long long id)
{
    ReleaseWorkingNative(id);
}

WorkingSurfaceContext^ Bridge::CreateWorkingSurfaceContext(
    double normalX,
    double normalY,
    double normalZ,
    double axisXX,
    double axisXY,
    double axisXZ)
{
    const long long contextId = CreateWorkingNative(
        normalX, normalY, normalZ, axisXX, axisXY, axisXZ);
    if (!contextId)
        return nullptr;

    WorkingNative* context = FindWorking(contextId, g_documentSession->modelRevision);
    auto result = gcnew WorkingSurfaceContext();
    result->Id = contextId;
    result->Revision = context->revision;
    result->MeshDeflection = context->deflection;
    result->Frame = gcnew array<double>{
        context->origin.X(), context->origin.Y(), context->origin.Z(),
        context->x.X(), context->x.Y(), context->x.Z(),
        context->y.X(), context->y.Y(), context->y.Z(),
        context->n.X(), context->n.Y(), context->n.Z() };
    result->Triangles = gcnew array<double>(static_cast<int>(context->triangles.size()));
    for (int index = 0; index < result->Triangles->Length; ++index)
        result->Triangles[index] = context->triangles[index];
    return result;
}

WorkingSurfaceHit^ Bridge::QueryWorkingSurface(long long id, long long revision, double u, double v)
{
    double point[3] = {};
    double normal[3] = {};
    double faceUv[2] = {};
    int faceId = -1;
    const auto status = QueryWorkingNative(id, revision, u, v, point, faceId, normal, faceUv);

    auto result = gcnew WorkingSurfaceHit();
    result->ContextId = id;
    result->Revision = revision;
    result->DatumPoint = gcnew array<double>{ u, v };
    result->Status = static_cast<WorkingSurfaceQueryStatus>(static_cast<int>(status));
    result->FaceId = faceId;
    if (status == WorkingStatusNative::Hit || status == WorkingStatusNative::Tangent)
    {
        result->Point = gcnew array<double>{ point[0], point[1], point[2] };
        result->Normal = gcnew array<double>{ normal[0], normal[1], normal[2] };
        result->FaceUv = gcnew array<double>{ faceUv[0], faceUv[1] };
    }
    return result;
}
WorkingSurfaceContext^ Bridge::CreatePreparedCandidateWorkingSurfaceContext(
    long long candidateId, double nx, double ny, double nz, double xx, double xy, double xz)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
        return nullptr;

    NativeDocumentSession& document = *candidate->second.document;
    const long long contextId = CreateWorkingNative(document, nx, ny, nz, xx, xy, xz, false);
    if (!contextId)
        return nullptr;

    WorkingNative* context = FindWorking(document, contextId, document.modelRevision);
    if (!context)
    {
        document.workingContexts.erase(contextId);
        return nullptr;
    }

    auto result = gcnew WorkingSurfaceContext();
    result->Id = contextId;
    result->Revision = context->revision;
    result->MeshDeflection = context->deflection;
    result->Frame = gcnew array<double>{
        context->origin.X(), context->origin.Y(), context->origin.Z(),
        context->x.X(), context->x.Y(), context->x.Z(),
        context->y.X(), context->y.Y(), context->y.Z(),
        context->n.X(), context->n.Y(), context->n.Z() };
    result->Triangles = gcnew array<double>(static_cast<int>(context->triangles.size()));
    for (int index = 0; index < result->Triangles->Length; ++index)
        result->Triangles[index] = context->triangles[index];
    return result;
}

void Bridge::ReleasePreparedCandidateWorkingSurfaceContext(long long candidateId, long long contextId)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || contextId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document)
        return;

    NativeDocumentSession& document = *candidate->second.document;
    for (auto staged = document.stagedPreparedPathSegments.begin();
        staged != document.stagedPreparedPathSegments.end();)
    {
        if (staged->second.contextId == contextId)
            staged = document.stagedPreparedPathSegments.erase(staged);
        else
            ++staged;
    }

    document.workingContexts.erase(contextId);
}

WorkingSurfaceHit^ Bridge::QueryPreparedCandidateWorkingSurface(
    long long candidateId, long long contextId, long long revision, double u, double v)
{
    auto result = gcnew WorkingSurfaceHit();
    result->ContextId = contextId;
    result->Revision = revision;
    result->DatumPoint = gcnew array<double>{ u, v };
    result->Status = WorkingSurfaceQueryStatus::StaleContext;
    result->FaceId = -1;

    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
        return result;

    double point[3] = {}, normal[3] = {}, faceUv[2] = {};
    int faceId = -1;
    const auto status = QueryWorkingNative(
        *candidate->second.document, contextId, revision, u, v, point, faceId, normal, faceUv);
    result->Status = static_cast<WorkingSurfaceQueryStatus>(static_cast<int>(status));
    result->FaceId = faceId;
    if (status == WorkingStatusNative::Hit || status == WorkingStatusNative::Tangent)
    {
        result->Point = gcnew array<double>{ point[0], point[1], point[2] };
        result->Normal = gcnew array<double>{ normal[0], normal[1], normal[2] };
        result->FaceUv = gcnew array<double>{ faceUv[0], faceUv[1] };
    }
    return result;
}

