HeatingPathSegmentResult^ Bridge::DisplayWorkingPathSegment(long long id,long long revision,int pieceId,String^ kind,array<double>^ flat)
{
    if (kind==nullptr || flat==nullptr) return nullptr;
    try {
        std::vector<double> xyz; for each(double v in flat) xyz.push_back(v);
        double data[7]={};
        if (!DisplayWorkingPathSegmentNative(id,revision,pieceId,msclr::interop::marshal_as<std::string>(kind),xyz,data)) return nullptr;
        auto result=gcnew HeatingPathSegmentResult(); result->Length=data[0];
        result->Start=gcnew array<double>{data[1],data[2],data[3]}; result->End=gcnew array<double>{data[4],data[5],data[6]};
        return result;
    } catch (...) { return nullptr; }
}
bool Bridge::StageWorkingPolyline(long long id,long long revision,int stageId,array<double>^ flat)
{
    if (flat==nullptr) return false;
    try { std::vector<double> xyz; for each(double v in flat) xyz.push_back(v); return StageWorkingPolylineNative(id,revision,stageId,xyz); }
    catch (...) { return false; }
}
HeatingPathSegmentResult^ Bridge::StageProjectedWorkingPathSegment(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(kind==nullptr||flat==nullptr||faceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingPathSegmentNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
        auto r=gcnew HeatingPathSegmentResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
HeatingPathSegmentResult^ Bridge::StageProjectedWorkingCurveSegment(long long id,long long revision,int stageId,String^ kind,
    CurveDefinitionData^ definition,array<int>^ faceIds,double dx,double dy,double dz)
{
    if (kind==nullptr || definition==nullptr || definition->Kind==nullptr ||
        definition->Direction==nullptr || faceIds==nullptr) return nullptr;
    try {
        CurveDefinitionNative curve = ReadNativeCurveDefinition(definition);

        std::vector<int> faces;
        for each (int face in faceIds) {
            faces.push_back(face);
        }
        double data[7]={};
        if (!StageProjectedWorkingCurveSegmentNative(id,revision,stageId,
                msclr::interop::marshal_as<std::string>(kind),curve,faces,gp_Dir(dx,dy,dz),data)) return nullptr;

        auto result=gcnew HeatingPathSegmentResult();
        result->Length=data[0];
        result->Start=gcnew array<double>{data[1],data[2],data[3]};
        result->End=gcnew array<double>{data[4],data[5],data[6]};
        return result;
    } catch (...) { return nullptr; }
}
HeatingPathSegmentResult^ Bridge::StageProjectedWorkingPolyline(long long id,long long revision,int stageId,array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(flat==nullptr||faceIds==nullptr)return nullptr;
    try {std::vector<double> xyz;std::vector<int> faces;for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingPolylineNative(id,revision,stageId,xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
        auto r=gcnew HeatingPathSegmentResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;}catch(...){return nullptr;}
}
bool Bridge::CommitWorkingPathGeometry(long long id,long long revision,array<int>^ stagedPathSegmentIds,array<int>^ finalPathSegmentIds,array<int>^ stagedPolylineIds)
{
    if (stagedPathSegmentIds==nullptr || finalPathSegmentIds==nullptr || stagedPolylineIds==nullptr) return false;
    try {
        std::vector<int> sr,fr,sp; for each(int v in stagedPathSegmentIds) sr.push_back(v);
        for each(int v in finalPathSegmentIds) fr.push_back(v); for each(int v in stagedPolylineIds) sp.push_back(v);
        return CommitWorkingGeometryNative(id,revision,sr,fr,sp);
    } catch (...) { return false; }
}

HeatingPathSegmentResult^ Bridge::StagePreparedCandidateWorkingCurveSegment(
    long long candidateId,
    long long contextId,
    long long revision,
    int stageId,
    String^ kind,
    CurveDefinitionData^ definition,
    array<int>^ faceIds,
    double dx,
    double dy,
    double dz,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount)
{
    if (kind == nullptr || definition == nullptr || definition->Kind == nullptr ||
        definition->Direction == nullptr || faceIds == nullptr)
    {
        return nullptr;
    }

    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        return nullptr;
    }

    try
    {
        CurveDefinitionNative curve = ReadNativeCurveDefinition(definition);
        std::vector<int> nativeFaceIds;
        nativeFaceIds.reserve(static_cast<size_t>(faceIds->Length));
        for each (int faceId in faceIds)
            nativeFaceIds.push_back(faceId);

        double measurement[7] = {};
        NativeCurveApproximation approximation;
        NativeDocumentSession& document = *candidate->second.document;
        if (!StagePreparedCandidateWorkingCurveSegmentNative(
                document,
                contextId,
                revision,
                stageId,
                msclr::interop::marshal_as<std::string>(kind),
                curve,
                nativeFaceIds,
                gp_Dir(dx, dy, dz),
                measurement,
                requestedDeflectionMm,
                requestedAngularToleranceRad,
                maximumSampleSpacingMm,
                maximumSubdivisionDepth,
                maximumProbeCount,
                &approximation))
        {
            return nullptr;
        }

        auto result = gcnew HeatingPathSegmentResult();
        result->Length = measurement[0];
        result->Start = gcnew array<double>{ measurement[1], measurement[2], measurement[3] };
        result->End = gcnew array<double>{ measurement[4], measurement[5], measurement[6] };
        result->Samples = gcnew array<double>(static_cast<int>(approximation.samples.size()));
        for (size_t index = 0; index < approximation.samples.size(); ++index)
            result->Samples[static_cast<int>(index)] = approximation.samples[index];
        result->RequestedDeflectionMm = requestedDeflectionMm;
        result->RequestedAngularToleranceRad = requestedAngularToleranceRad;
        result->MaximumMeasuredDeviationMm = approximation.maximumDeviationMm;
        result->MaximumSampleSpacingMm = approximation.maximumSpacingMm;
        result->ProbeCount = approximation.probeCount;
        result->ApproximationComplete = approximation.isComplete;
        result->ApproximationRuleVersion = gcnew String(L"occt-wire-sampling-v1");
        return result;
    }
    catch (...)
    {
        return nullptr;
    }
}

HeatingPathSegmentResult^ Bridge::StagePreparedCandidateExactCurveSegment(
    long long candidateId,
    long long contextId,
    long long revision,
    int stageId,
    String^ kind,
    CurveDefinitionData^ definition,
    double requestedDeflectionMm,
    double requestedAngularToleranceRad,
    double maximumSampleSpacingMm,
    int maximumSubdivisionDepth,
    int maximumProbeCount)
{
    if (kind == nullptr || definition == nullptr || definition->Kind == nullptr ||
        definition->Direction == nullptr)
    {
        return nullptr;
    }

    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        return nullptr;
    }

    try
    {
        CurveDefinitionNative curve = ReadNativeCurveDefinition(definition);
        double measurement[7] = {};
        NativeCurveApproximation approximation;
        NativeDocumentSession& document = *candidate->second.document;
        if (!StagePreparedCandidateExactCurveSegmentNative(
                document,
                contextId,
                revision,
                stageId,
                msclr::interop::marshal_as<std::string>(kind),
                curve,
                measurement,
                requestedDeflectionMm,
                requestedAngularToleranceRad,
                maximumSampleSpacingMm,
                maximumSubdivisionDepth,
                maximumProbeCount,
                &approximation))
        {
            return nullptr;
        }

        auto result = gcnew HeatingPathSegmentResult();
        result->Length = measurement[0];
        result->Start = gcnew array<double>{ measurement[1], measurement[2], measurement[3] };
        result->End = gcnew array<double>{ measurement[4], measurement[5], measurement[6] };
        result->Samples = gcnew array<double>(static_cast<int>(approximation.samples.size()));
        for (size_t index = 0; index < approximation.samples.size(); ++index)
            result->Samples[static_cast<int>(index)] = approximation.samples[index];
        result->RequestedDeflectionMm = requestedDeflectionMm;
        result->RequestedAngularToleranceRad = requestedAngularToleranceRad;
        result->MaximumMeasuredDeviationMm = approximation.maximumDeviationMm;
        result->MaximumSampleSpacingMm = approximation.maximumSpacingMm;
        result->ProbeCount = approximation.probeCount;
        result->ApproximationComplete = approximation.isComplete;
        result->ApproximationRuleVersion = gcnew String(L"occt-wire-sampling-v1");
        return result;
    }
    catch (...)
    {
        return nullptr;
    }
}

bool Bridge::CommitPreparedCandidateWorkingPathGeometry(
    long long candidateId,
    long long contextId,
    long long revision,
    array<int>^ stagedPathSegmentIds,
    array<int>^ finalPathSegmentIds)
{
    if (stagedPathSegmentIds == nullptr || finalPathSegmentIds == nullptr)
        return false;

    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document || candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        return false;
    }

    try
    {
        std::vector<int> stagedIds;
        std::vector<int> finalIds;
        stagedIds.reserve(static_cast<size_t>(stagedPathSegmentIds->Length));
        finalIds.reserve(static_cast<size_t>(finalPathSegmentIds->Length));
        for each (int stageId in stagedPathSegmentIds)
            stagedIds.push_back(stageId);
        for each (int finalId in finalPathSegmentIds)
            finalIds.push_back(finalId);

        return CommitPreparedCandidateWorkingPathGeometryNative(
            *candidate->second.document,
            contextId,
            revision,
            stagedIds,
            finalIds);
    }
    catch (...)
    {
        return false;
    }
}

void Bridge::DiscardPreparedCandidateWorkingPathGeometry(
    long long candidateId,
    long long contextId,
    long long revision,
    array<int>^ stagedPathSegmentIds)
{
    if (stagedPathSegmentIds == nullptr)
        return;

    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() ||
        !candidate->second.document)
    {
        return;
    }

    try
    {
        std::vector<int> stagedIds;
        stagedIds.reserve(static_cast<size_t>(stagedPathSegmentIds->Length));
        for each (int stageId in stagedPathSegmentIds)
            stagedIds.push_back(stageId);

        DiscardPreparedCandidateWorkingPathGeometryNative(
            *candidate->second.document,
            contextId,
            revision,
            stagedIds);
    }
    catch (...) { }
}

void Bridge::DiscardWorkingPathGeometry(long long id,long long revision,array<int>^ stagedPathSegmentIds,array<int>^ stagedPolylineIds)
{
    try {
        std::vector<int> sr,sp;
        if (stagedPathSegmentIds!=nullptr) for each(int v in stagedPathSegmentIds) sr.push_back(v);
        if (stagedPolylineIds!=nullptr) for each(int v in stagedPolylineIds) sp.push_back(v);
        DiscardWorkingGeometryNative(id,revision,sr,sp);
    } catch (...) { }
}
