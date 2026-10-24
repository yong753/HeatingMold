WorkingSurfaceHit^ Bridge::QueryWorkingSurfaceForFaces(long long id,long long revision,array<int>^ faceIds,double u,double v)
{
    if (faceIds==nullptr) {
        auto invalid=gcnew WorkingSurfaceHit(); invalid->ContextId=id; invalid->Revision=revision;
        invalid->DatumPoint=gcnew array<double>{u,v}; invalid->Status=WorkingSurfaceQueryStatus::InvalidInput; invalid->FaceId=-1; return invalid;
    }
    for each (int candidate in faceIds) if (candidate<0 || candidate>=static_cast<int>(g_documentSession->faces.size())) {
        auto invalid=gcnew WorkingSurfaceHit(); invalid->ContextId=id; invalid->Revision=revision;
        invalid->DatumPoint=gcnew array<double>{u,v}; invalid->Status=WorkingSurfaceQueryStatus::InvalidInput; invalid->FaceId=-1; return invalid;
    }
    double p[3]={}, n[3]={}, uv[2]={}; int face=-1;
    const auto status=QueryWorkingNative(id,revision,u,v,p,face,n,uv);
    auto result=gcnew WorkingSurfaceHit();
    result->ContextId=id; result->Revision=revision; result->DatumPoint=gcnew array<double>{u,v};
    result->Status=static_cast<WorkingSurfaceQueryStatus>(static_cast<int>(status)); result->FaceId=face;
    if (status==WorkingStatusNative::Hit || status==WorkingStatusNative::Tangent) {
        result->Point=gcnew array<double>{p[0],p[1],p[2]};
        result->Normal=gcnew array<double>{n[0],n[1],n[2]};
        result->FaceUv=gcnew array<double>{uv[0],uv[1]};
        bool selected=false;
        if (faceIds!=nullptr) for each (int candidate in faceIds) {
            if (candidate<0 || candidate>=static_cast<int>(g_documentSession->faces.size())) {
                result->Status=WorkingSurfaceQueryStatus::InvalidInput; result->Point=nullptr; result->Normal=nullptr; return result;
            }
            selected |= candidate==face;
        }
        result->IsSelectedFace=selected;
        if (!selected) { result->Status=WorkingSurfaceQueryStatus::Miss; result->Point=nullptr; result->Normal=nullptr; result->FaceUv=nullptr; }
    }
    return result;
}
WorkingFaceAdjacencyStatus Bridge::QueryWorkingFaceAdjacency(long long id,long long revision,int faceA,int faceB)
{
    switch (QueryWorkingFacesAdjacencyNative(id,revision,faceA,faceB))
    {
    case WorkingFaceAdjacencyStatusNative::Adjacent:
        return WorkingFaceAdjacencyStatus::Adjacent;
    case WorkingFaceAdjacencyStatusNative::NotAdjacent:
        return WorkingFaceAdjacencyStatus::NotAdjacent;
    case WorkingFaceAdjacencyStatusNative::InvalidFace:
        return WorkingFaceAdjacencyStatus::InvalidFace;
    case WorkingFaceAdjacencyStatusNative::StaleContext:
        return WorkingFaceAdjacencyStatus::StaleContext;
    case WorkingFaceAdjacencyStatusNative::GeometryFailure:
        return WorkingFaceAdjacencyStatus::GeometryFailure;
    default:
        return WorkingFaceAdjacencyStatus::Unknown;
    }
}
WorkingFaceAdjacencyStatus Bridge::QueryPreparedCandidateFaceAdjacency(
    long long candidateId, long long contextId, long long revision, int faceA, int faceB)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
        return WorkingFaceAdjacencyStatus::StaleContext;

    switch (QueryWorkingFacesAdjacencyNative(
        *candidate->second.document, contextId, revision, faceA, faceB))
    {
    case WorkingFaceAdjacencyStatusNative::Adjacent:
        return WorkingFaceAdjacencyStatus::Adjacent;
    case WorkingFaceAdjacencyStatusNative::NotAdjacent:
        return WorkingFaceAdjacencyStatus::NotAdjacent;
    case WorkingFaceAdjacencyStatusNative::InvalidFace:
        return WorkingFaceAdjacencyStatus::InvalidFace;
    case WorkingFaceAdjacencyStatusNative::StaleContext:
        return WorkingFaceAdjacencyStatus::StaleContext;
    case WorkingFaceAdjacencyStatusNative::GeometryFailure:
        return WorkingFaceAdjacencyStatus::GeometryFailure;
    default:
        return WorkingFaceAdjacencyStatus::Unknown;
    }
}
WorkingSelectedTopology^ Bridge::QueryWorkingSelectedTopology(
    long long id,
    long long revision,
    array<int>^ faceIds,
    double deflectionMm)
{
    WorkingSelectedTopologyNative nativeFailure;
    if (faceIds == nullptr)
        return CreateManagedSelectedTopology(nativeFailure);

    std::vector<int> nativeFaceIndices;
    for each (int faceIndex in faceIds)
        nativeFaceIndices.push_back(faceIndex);

    const WorkingSelectedTopologyNative nativeResult = QueryWorkingSelectedTopologyNative(
        *g_documentSession, id, revision, nativeFaceIndices, deflectionMm);
    return CreateManagedSelectedTopology(nativeResult);
}
WorkingSelectedTopology^ Bridge::QueryPreparedCandidateSelectedTopology(
    long long candidateId,
    long long contextId,
    long long revision,
    array<int>^ faceIds,
    double deflectionMm)
{
    WorkingSelectedTopologyNative nativeResult;
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        nativeResult.status = WorkingTopologyStatusNative::StaleContext;
        return CreateManagedSelectedTopology(nativeResult);
    }
    if (faceIds == nullptr)
        return CreateManagedSelectedTopology(nativeResult);

    std::vector<int> nativeFaceIndices;
    nativeFaceIndices.reserve(static_cast<size_t>(faceIds->Length));
    for each (int faceIndex in faceIds)
        nativeFaceIndices.push_back(faceIndex);

    nativeResult = QueryWorkingSelectedTopologyNative(
        *candidate->second.document, contextId, revision, nativeFaceIndices, deflectionMm);
    return CreateManagedSelectedTopology(nativeResult);
}
WorkingSurfaceElementsResult^ Bridge::QueryWorkingSurfaceElements(
    long long id,
    long long revision,
    array<int>^ faceIds,
    array<WorkingRegionLoopData^>^ effectiveBoundary,
    double deflectionMm,
    double angularToleranceRad)
{
    return QuerySurfaceElementsForDocument(
        *g_documentSession,
        id,
        revision,
        faceIds,
        effectiveBoundary,
        deflectionMm,
        angularToleranceRad);
}

WorkingSurfaceElementsResult^ Bridge::QueryPreparedCandidateSurfaceElements(
    long long candidateId,
    long long contextId,
    long long revision,
    array<int>^ faceIds,
    array<WorkingRegionLoopData^>^ effectiveBoundary,
    double deflectionMm,
    double angularToleranceRad)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        WorkingSurfaceElementsNative staleResult;
        staleResult.status = WorkingSurfaceElementsStatusNative::StaleContext;
        return CreateManagedSurfaceElements(staleResult);
    }

    return QuerySurfaceElementsForDocument(
        *candidate->second.document,
        contextId,
        revision,
        faceIds,
        effectiveBoundary,
        deflectionMm,
        angularToleranceRad);
}
bool Bridge::AreWorkingFacesAdjacent(long long id,long long revision,int faceA,int faceB)
{ return QueryWorkingFaceAdjacency(id,revision,faceA,faceB)==WorkingFaceAdjacencyStatus::Adjacent; }
