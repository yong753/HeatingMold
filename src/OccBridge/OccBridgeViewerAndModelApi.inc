bool Bridge::IsWorkingPointVisible(long long id,long long revision,double x,double y,double z,double tolerance)
{ return WorkingVisibleNative(id,revision,x,y,z,tolerance); }
void Bridge::ShowProjectedModelBoundary(array<array<double>^>^ loops)
{ ShowProjectedModelBoundary(loops,nullptr); }
void Bridge::ShowProjectedModelBoundary(array<array<double>^>^ loops,array<bool>^ holes)
{
    std::vector<std::vector<double>> values;
    std::vector<bool> flags;
    if (holes!=nullptr) for each(bool hole in holes) flags.push_back(hole);
    if (loops!=nullptr) for each(array<double>^ loop in loops) {
        std::vector<double> points;
        if (loop!=nullptr) for each(double v in loop) points.push_back(v);
        values.push_back(points);
    }
    ShowProjectedModelBoundaryNative(values,flags);
}
bool Bridge::ReturnToWorkingFront(long long id,long long revision)
{
    auto* ctx=FindWorking(id,revision);
    if (!ctx || g_view.IsNull()) return false;
    g_view->Camera()->SetProjectionType(Graphic3d_Camera::Projection_Orthographic);
    SetViewFrameNative(ctx->n.X(),ctx->n.Y(),ctx->n.Z(),ctx->y.X(),ctx->y.Y(),ctx->y.Z());
    return true;
}
WorkingCoordinateSystemPresentation^ Bridge::CaptureWorkingCoordinateSystemPresentation()
{
    double bounds[6]={}; bool hasBounds=false;
    const long long id=CaptureWorkingCoordinateSystemPresentationNative(bounds,hasBounds);
    if (!id) return nullptr;
    auto result=gcnew WorkingCoordinateSystemPresentation();
    result->Id=id; result->Revision=g_documentSession->modelRevision;
    result->BoundaryCount=static_cast<int>(g_documentSession->workingPresentations.at(id).boundaries.size());
    if (hasBounds) result->BoundaryBounds=gcnew array<double>{bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]};
    return result;
}
bool Bridge::RestoreWorkingCoordinateSystemPresentation(long long id) { return RestoreWorkingCoordinateSystemPresentationNative(id); }
void Bridge::ReleaseWorkingCoordinateSystemPresentation(long long id) { g_documentSession->workingPresentations.erase(id); }

ModelLoadCandidateResult^ Bridge::PrepareStepCandidate(String^ path)
{
    ModelLoadCandidateResult^ result = gcnew ModelLoadCandidateResult();
    result->Status = ModelLoadCandidateStatus::Failed;
    result->BaseModelRevision = g_documentSession->modelRevision;
    result->CandidateId = 0;
    result->Diagnostic = nullptr;
    if (String::IsNullOrWhiteSpace(path))
    {
        result->Diagnostic = "InvalidPath";
        return result;
    }

    std::string native;
    try
    {
        native = msclr::interop::marshal_as<std::string>(path);
    }
    catch (const std::exception&)
    {
        result->Diagnostic = "InvalidPath";
        return result;
    }

    std::string diagnostic;
    try
    {
        std::unique_ptr<NativeDocumentSession> candidateSession;
        long long baseModelRevision = result->BaseModelRevision;
        if (!PrepareStepFile(native.c_str(), candidateSession, baseModelRevision, diagnostic))
        {
            result->Diagnostic = gcnew String(diagnostic.c_str());
            return result;
        }

        if (baseModelRevision == std::numeric_limits<long long>::max())
        {
            result->Diagnostic = "ModelRevisionExhausted";
            return result;
        }

        // 후보 질의와 publish 후 문서가 같은 다음 revision을 사용합니다.
        candidateSession->modelRevision = baseModelRevision + 1;
        result->BaseModelRevision = baseModelRevision;

        const long long candidateId = AllocatePreparedDocumentCandidateId();
        if (candidateId == 0)
        {
            result->Diagnostic = "PreparedCandidateIdSpaceExhausted";
            return result;
        }

        PreparedDocumentCandidate candidate{
            std::move(candidateSession),
            baseModelRevision
        };
        g_preparedDocumentCandidates.emplace(candidateId, std::move(candidate));
        result->Status = ModelLoadCandidateStatus::Prepared;
        result->CandidateId = candidateId;
    }
    catch (const std::exception&)
    {
        result->Diagnostic = "CandidateGeometryPreparationFailed";
    }
    catch (...)
    {
        result->Diagnostic = "CandidateGeometryPreparationFailed";
    }

    return result;
}

ModelLoadResult^ Bridge::PublishStepCandidate(long long candidateId)
{
    ModelLoadResult^ result = gcnew ModelLoadResult();
    result->Status = ModelLoadStatus::FailedBeforeCommit;
    result->ModelRevision = g_documentSession->modelRevision;
    result->Diagnostic = nullptr;

    auto candidateIt = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidateIt == g_preparedDocumentCandidates.end())
    {
        result->Diagnostic = "PreparedCandidateNotFound";
        return result;
    }

    PreparedDocumentCandidate candidate = std::move(candidateIt->second);
    g_preparedDocumentCandidates.erase(candidateIt);

    std::string diagnostic;
    NativeModelLoadStatus loadStatus = NativeModelLoadStatus::FailedBeforeCommit;
    NativeDocumentSession* activeDocumentBeforePublish = g_documentSession.get();
    try
    {
        loadStatus = PublishPreparedDocument(
            std::move(candidate.document),
            candidate.baseModelRevision,
            diagnostic);
    }
    catch (...)
    {
        loadStatus = ClassifyLoadException(activeDocumentBeforePublish, diagnostic);
    }

    result->Status = static_cast<ModelLoadStatus>(static_cast<int>(loadStatus));
    result->ModelRevision = g_documentSession->modelRevision;
    if (!diagnostic.empty())
        result->Diagnostic = gcnew String(diagnostic.c_str());
    return result;
}

bool Bridge::DiscardStepCandidate(long long candidateId)
{
    if (candidateId <= 0)
        return false;

    return g_preparedDocumentCandidates.erase(candidateId) != 0;
}

ModelLoadResult^ Bridge::LoadStepDetailed(String^ path)
{
    ModelLoadCandidateResult^ candidate = PrepareStepCandidate(path);
    if (candidate == nullptr || candidate->Status != ModelLoadCandidateStatus::Prepared)
    {
        ModelLoadResult^ failed = gcnew ModelLoadResult();
        failed->Status = ModelLoadStatus::FailedBeforeCommit;
        failed->ModelRevision = g_documentSession->modelRevision;
        failed->Diagnostic = candidate == nullptr ? "CandidatePreparationFailed" : candidate->Diagnostic;
        return failed;
    }

    return PublishStepCandidate(candidate->CandidateId);
}

bool Bridge::LoadStep(String^ path)
{
    ModelLoadResult^ result = LoadStepDetailed(path);
    return result != nullptr && result->Status != ModelLoadStatus::FailedBeforeCommit;
}

bool Bridge::RebuildModelPresentation()
{
    return RebuildModelPresentationNative();
}

StepDesignWriteResult^ Bridge::WriteDesignStep(
    String^ outputPath,
    String^ sourceModelPath,
    bool includeSourceModel,
    array<StepGeometryGroupData^>^ groups)
{
    if (String::IsNullOrWhiteSpace(outputPath) || groups == nullptr || groups->Length == 0)
        return MakeStepDesignWriteResult(1, "STEP 출력 경로와 geometry 그룹이 필요합니다.");

    try
    {
        std::vector<StepGroupNative> nativeGroups;
        nativeGroups.reserve(static_cast<size_t>(groups->Length));
        for each (StepGeometryGroupData^ group in groups)
        {
            if (group == nullptr || String::IsNullOrWhiteSpace(group->Name) ||
                group->Wires == nullptr || group->Wires->Length == 0)
            {
                return MakeStepDesignWriteResult(1, "STEP geometry 그룹 또는 wire가 비어 있습니다.");
            }

            StepGroupNative nativeGroup;
            nativeGroup.wires.reserve(static_cast<size_t>(group->Wires->Length));
            for each (StepDesignWireData^ wire in group->Wires)
            {
                if (wire == nullptr || String::IsNullOrWhiteSpace(wire->Name) ||
                    wire->Curves == nullptr || wire->Curves->Length == 0)
                {
                    return MakeStepDesignWriteResult(1, "STEP wire 이름 또는 곡선 목록이 유효하지 않습니다.");
                }

                StepWireNative nativeWire;
                nativeWire.curves.reserve(static_cast<size_t>(wire->Curves->Length));
                for each (StepDesignCurveData^ curve in wire->Curves)
                {
                    if (curve == nullptr || curve->Definition == nullptr)
                        return MakeStepDesignWriteResult(3, "STEP wire에 곡선 정의가 없습니다.");

                    nativeWire.curves.push_back(StepCurveNative{
                        ReadNativeCurveDefinition(curve->Definition),
                        curve->IsReversed});
                }
                nativeGroup.wires.push_back(std::move(nativeWire));
            }
            nativeGroups.push_back(std::move(nativeGroup));
        }

        const std::string nativeOutputPath = msclr::interop::marshal_as<std::string>(outputPath);
        const std::string nativeSourcePath = String::IsNullOrWhiteSpace(sourceModelPath)
            ? std::string()
            : msclr::interop::marshal_as<std::string>(sourceModelPath);
        std::string diagnostic;
        const int status = WriteDesignStepNative(
            nativeOutputPath,
            nativeSourcePath,
            includeSourceModel,
            nativeGroups,
            diagnostic);
        return MakeStepDesignWriteResult(status, diagnostic);
    }
    catch (const Standard_Failure& failure)
    {
        return MakeStepDesignWriteResult(6, failure.GetMessageString());
    }
    catch (const std::exception& failure)
    {
        return MakeStepDesignWriteResult(6, failure.what());
    }
    catch (Exception^ failure)
    {
        return MakeStepDesignWriteResult(6, msclr::interop::marshal_as<std::string>(failure->Message));
    }
    catch (...)
    {
        return MakeStepDesignWriteResult(6, "STEP geometry writer에서 알 수 없는 예외가 발생했습니다.");
    }
}

ShapeInfo^ Bridge::GetShapeInfo()
{
    const ShapeCounts c = ComputeCounts();
    ShapeInfo^ info = gcnew ShapeInfo();
    info->Solids = c.solids;
    info->Shells = c.shells;
    info->Faces = c.faces;
    info->Planes = c.planes;
    return info;
}

String^ Bridge::GetFaceType(int faceIndex)
{
    const char* name = FaceTypeName(*g_documentSession, faceIndex);
    return name == nullptr ? nullptr : gcnew String(name);
}

FaceGeometryFeaturesResult^ Bridge::GetFaceGeometryFeatures()
{
    std::vector<FaceGeometryFeatureNative> nativeFeatures;
    FaceGeometryFeaturesStatusNative nativeStatus = FaceGeometryFeaturesStatusNative::GeometryFailure;
    const bool succeeded = CollectFaceGeometryFeaturesNative(*g_documentSession, nativeFeatures, nativeStatus);
    const FaceFeatureQueryStatus status = succeeded
        ? FaceFeatureQueryStatus::Success
        : nativeStatus == FaceGeometryFeaturesStatusNative::InvalidModel
            ? FaceFeatureQueryStatus::InvalidModel
            : FaceFeatureQueryStatus::GeometryFailure;
    return CreateFaceGeometryFeaturesResult(status, nativeFeatures);
}

FaceGeometryFeaturesResult^ Bridge::GetPreparedCandidateFaceGeometryFeatures(long long candidateId)
{
    auto candidateIt = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidateIt == g_preparedDocumentCandidates.end() || !candidateIt->second.document)
        return CreateFaceGeometryFeaturesResult(
            FaceFeatureQueryStatus::CandidateNotFound,
            std::vector<FaceGeometryFeatureNative>());

    std::vector<FaceGeometryFeatureNative> nativeFeatures;
    FaceGeometryFeaturesStatusNative nativeStatus = FaceGeometryFeaturesStatusNative::GeometryFailure;
    NativeDocumentSession& candidateDocument = *candidateIt->second.document;
    const bool succeeded = CollectFaceGeometryFeaturesNative(candidateDocument, nativeFeatures, nativeStatus);
    const FaceFeatureQueryStatus status = succeeded
        ? FaceFeatureQueryStatus::Success
        : nativeStatus == FaceGeometryFeaturesStatusNative::InvalidModel
            ? FaceFeatureQueryStatus::InvalidModel
            : FaceFeatureQueryStatus::GeometryFailure;
    return CreateFaceGeometryFeaturesResult(status, nativeFeatures);
}

array<UvWire^>^ Bridge::GetFaceUvBoundary(int faceIndex)
{
    std::vector<NativeWire> nativeWires;
    if (!ExtractUvWires(*g_documentSession, faceIndex, nativeWires)) return nullptr;
    return CreateManagedUvWires(nativeWires);
}

array<UvWire^>^ Bridge::GetPreparedCandidateFaceUvBoundary(long long candidateId, int faceIndex)
{
    auto candidateIt = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidateIt == g_preparedDocumentCandidates.end() || !candidateIt->second.document)
        return nullptr;

    std::vector<NativeWire> nativeWires;
    try
    {
        if (!ExtractUvWires(*candidateIt->second.document, faceIndex, nativeWires)) return nullptr;
    }
    catch (const Standard_Failure&)
    {
        return nullptr;
    }
    return CreateManagedUvWires(nativeWires);
}

array<RegionBoundaryLoop^>^ Bridge::GetSelectedRegionBoundary(array<int>^ faceIds, double deflection)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<NativeBoundaryLoop> nativeLoops;
    try
    {
        if (!ExtractSelectedRegionBoundary(ids, deflection, nativeLoops)) return nullptr;
    }
    catch (const Standard_Failure&)
    {
        return nullptr;
    }
    auto result = gcnew array<RegionBoundaryLoop^>((int)nativeLoops.size());
    for (int i = 0; i < result->Length; ++i)
    {
        RegionBoundaryLoop^ loop = gcnew RegionBoundaryLoop();
        loop->Closed = nativeLoops[(size_t)i].closed;
        auto points = gcnew array<double>((int)nativeLoops[(size_t)i].xyz.size());
        for (int k = 0; k < points->Length; ++k) points[k] = nativeLoops[(size_t)i].xyz[(size_t)k];
        loop->Points = points;
        result[i] = loop;
    }
    return result;
}

array<double>^ Bridge::SampleUvTo3D(int faceIndex, double u, double v)
{
    double p[3];
    if (!SamplePoint(*g_documentSession, faceIndex, u, v, p)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = p[0]; r[1] = p[1]; r[2] = p[2];
    return r;
}

array<double>^ Bridge::SamplePreparedCandidateUvTo3D(long long candidateId, int faceIndex, double u, double v)
{
    auto candidateIt = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidateIt == g_preparedDocumentCandidates.end() || !candidateIt->second.document)
        return nullptr;

    double point[3];
    try
    {
        if (!SamplePoint(*candidateIt->second.document, faceIndex, u, v, point)) return nullptr;
    }
    catch (const Standard_Failure&)
    {
        return nullptr;
    }

    return gcnew array<double>{point[0], point[1], point[2]};
}

array<double>^ Bridge::PickSelectedBoundaryPoint(array<int>^ faceIds, int x, int y)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    double p[4];
    if (!PickSelectedBoundaryPointNative(ids, x, y, p)) return nullptr;
    array<double>^ result = gcnew array<double>(4);
    for (int i = 0; i < 4; ++i) result[i] = p[i];
    return result;
}

array<double>^ Bridge::ProjectPathWireOntoSelectedFaces(array<int>^ faceIds, array<double>^ xyzFlat,
                                                        double dx, double dy, double dz, double insertionDepth)
{
    if (faceIds == nullptr || faceIds->Length == 0 || xyzFlat == nullptr) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<double> xyz(xyzFlat->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    double length = 0.0;
    int edgeCount = 0, status = 0, wireCount = 0;
    bool success = false;
    try
    {
        success = ProjectPathWireOntoSelectedFacesNative(ids, xyz, gp_Dir(dx, dy, dz), insertionDepth,
            &length, &edgeCount, &status, &wireCount);
    }
    catch (const Standard_Failure&) { status = 99; }
    auto result = gcnew array<double>(5);
    result[0] = success ? 1.0 : 0.0;
    result[1] = length;
    result[2] = edgeCount;
    result[3] = wireCount;
    result[4] = status;
    return result;
}

HeatingPathSegmentResult^ Bridge::ProjectHeatingPathSegment(int pieceId, String^ kind, array<double>^ xyzFlat,
    array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm)
{
    if (kind == nullptr || xyzFlat == nullptr || faceIds == nullptr) return nullptr;
    std::vector<double> xyz(xyzFlat->Length);
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[i] = xyzFlat[i];
    for (int i = 0; i < faceIds->Length; ++i) ids[i] = faceIds[i];
    double data[7];
    try { if (!ProjectHeatingPathSegmentNative(pieceId, msclr::interop::marshal_as<std::string>(kind), xyz, ids, gp_Dir(dx, dy, dz), insertionDepthMm, data)) return nullptr; }
    catch (const Standard_Failure&) { return nullptr; }
    auto result = gcnew HeatingPathSegmentResult();
    result->Length = data[0];
    result->Start = gcnew array<double>(3); result->End = gcnew array<double>(3);
    for (int i = 0; i < 3; ++i) { result->Start[i] = data[i + 1]; result->End[i] = data[i + 4]; }
    return result;
}

bool Bridge::SetTurnSegmentEnabled(int pieceId, bool enabled, double r, double g, double b)
{
    return SetTurnSegmentNative(pieceId, enabled, r, g, b);
}

void Bridge::ClearPathSegments() { ClearPathSegmentsNative(); if (!g_view.IsNull()) g_view->Redraw(); }

array<double>^ Bridge::ProjectConnectionPath(int connectionPathId, array<double>^ endpointAndConnectionPoint,
    array<double>^ actualPathEndpoint, array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm)
{
    return ProjectConnectionPath(connectionPathId, endpointAndConnectionPoint, actualPathEndpoint, faceIds, dx, dy, dz, insertionDepthMm, false);
}

array<double>^ Bridge::ProjectConnectionPath(int connectionPathId, array<double>^ endpointAndConnectionPoint,
    array<double>^ actualPathEndpoint, array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm, bool previewOnly)
{
    // Remove the old preview without painting a blank frame. Redraw once after
    // replacement/validation, including failure so invalid positions clear it.
    const bool hadPreview = !g_documentSession->presentation.connectionPathPreview.IsNull() || !g_documentSession->presentation.connectionPointPreview.IsNull();
    ClearConnectionPathPreviewNative();
    auto result = gcnew array<double>(4);
    result[0] = -1;
    if (endpointAndConnectionPoint == nullptr || actualPathEndpoint == nullptr || faceIds == nullptr) {
        if (hadPreview && !g_view.IsNull()) g_view->Redraw();
        return result;
    }
    std::vector<double> xyz(endpointAndConnectionPoint->Length), actual(actualPathEndpoint->Length);
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < endpointAndConnectionPoint->Length; ++i) xyz[i] = endpointAndConnectionPoint[i];
    for (int i = 0; i < actualPathEndpoint->Length; ++i) actual[i] = actualPathEndpoint[i];
    for (int i = 0; i < faceIds->Length; ++i) ids[i] = faceIds[i];
    double data[4] = { -9, 0, 0, 0 };
    try { ProjectConnectionPathNative(connectionPathId, xyz, actual, ids, gp_Dir(dx, dy, dz), insertionDepthMm, data, previewOnly); }
    catch (const Standard_Failure&) {
        if (previewOnly) ClearConnectionPathPreviewNative();
        data[0] = -9;
    }
    if (!g_view.IsNull()) g_view->Redraw();
    for (int i = 0; i < 4; ++i) result[i] = data[i];
    return result;
}

void Bridge::ClearConnectionPaths() { ClearConnectionPathsNative(); if (!g_view.IsNull()) g_view->Redraw(); }
void Bridge::ClearConnectionPathPreview()
{
    const bool hadPreview = !g_documentSession->presentation.connectionPathPreview.IsNull() || !g_documentSession->presentation.connectionPointPreview.IsNull();
    ClearConnectionPathPreviewNative();
    if (hadPreview && !g_view.IsNull()) g_view->Redraw();
}
bool Bridge::SetControlPointEditPreview(array<array<double>^>^ xyzPolylines)
{
    if (xyzPolylines == nullptr || xyzPolylines->Length == 0)
    {
        ClearControlPointEditPreview();
        return false;
    }

    std::vector<std::vector<double>> polylines;
    for each (array<double>^ coordinates in xyzPolylines)
    {
        if (coordinates == nullptr || coordinates->Length < 6 || coordinates->Length % 3 != 0)
        {
            ClearControlPointEditPreview();
            return false;
        }

        std::vector<double> nativeCoordinates;
        nativeCoordinates.reserve(static_cast<size_t>(coordinates->Length));
        for each (double value in coordinates)
            nativeCoordinates.push_back(value);
        polylines.push_back(std::move(nativeCoordinates));
    }

    bool displayed = false;
    try
    {
        displayed = SetControlPointEditPreviewNative(polylines);
    }
    catch (const Standard_Failure&)
    {
        ClearControlPointEditPreviewNative();
    }
    if (!g_view.IsNull()) g_view->Redraw();
    return displayed;
}
void Bridge::ClearControlPointEditPreview()
{
    const bool hadPreview = !g_documentSession->presentation.controlPointEditPreview.IsNull();
    ClearControlPointEditPreviewNative();
    if (hadPreview && !g_view.IsNull()) g_view->Redraw();
}
int Bridge::PickTurnSegment(int x, int y) { return PickTurnSegmentNative(x, y); }
int Bridge::PickHeatingPathSegment(int x, int y)
{
    if (g_ctx.IsNull() || g_view.IsNull()) return -1;
    g_ctx->MoveTo(x, y, g_view, Standard_False);
    Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
    if (owner.IsNull()) return -1;
    Handle(AIS_InteractiveObject) selectable = Handle(AIS_InteractiveObject)::DownCast(owner->Selectable());
    for (const auto& piece : g_documentSession->presentation.pathSegments) if (piece.second.ais == selectable) return piece.first;
    return -1;
}
void Bridge::HighlightCircuit(array<int>^ pieceIds)
{
    std::vector<int> ids;
    if (pieceIds != nullptr) for each (int id in pieceIds) ids.push_back(id);
    HighlightCircuitNative(ids);
}

// ================= 뷰어 managed (S1 항목 1·6) =================

bool Bridge::InitViewer(IntPtr hwnd, int width, int height)
{
    (void)width; (void)height;
    return InitViewerNative(hwnd.ToPointer());
}

void Bridge::Shutdown()
{
    ShutdownNative();
}

void Bridge::ResizeView(int width, int height)
{
    (void)width; (void)height;
    ResizeViewNative();
}

void Bridge::FitView()
{
    FitViewNative();
}

void Bridge::RotateStart(int x, int y)
{
    RotateStartNative(x, y);
}

void Bridge::RotateMove(int x, int y)
{
    RotateMoveNative(x, y);
}

void Bridge::RotateEnd()
{
    // StartRotation/Rotation 쌍으로 충분 — 종료 시 별도 처리 없음
}

namespace { int g_panStartX = 0, g_panStartY = 0; }

void Bridge::PanStart(int x, int y)
{
    g_panStartX = x;
    g_panStartY = y;
    if (!g_view.IsNull()) g_view->Pan(0, 0, 1.0, Standard_True);
}

void Bridge::PanMove(int x, int y)
{
    if (g_view.IsNull()) return;
    g_view->Pan(x - g_panStartX, g_panStartY - y, 1.0, Standard_False);
    g_view->Redraw();
}

void Bridge::PanEnd()
{
    // Pan remembers the starting camera state; no explicit end call is needed.
}

void Bridge::ZoomView(double factor)
{
    ZoomViewNative(factor);
}

void Bridge::ZoomViewAtPoint(int x, int y, int deltaY)
{
    ZoomViewAtPointNative(x, y, deltaY);
}

void Bridge::SetShapeColor(double r, double g, double b)
{
    SetShapeColorNative(r, g, b);
}

void Bridge::RedrawView()
{
    if (!g_view.IsNull()) g_view->Redraw();
}

void Bridge::ClearOverlays()
{
    ClearOverlayNative();
}

void Bridge::SetViewFrame(double dx, double dy, double dz, double ux, double uy, double uz)
{
    SetViewFrameNative(dx, dy, dz, ux, uy, uz);
}

array<double>^ Bridge::GetAutoAxis(int axis)
{
    double v[3];
    if (!GetAutoAxisNative(axis, v)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = v[0]; r[1] = v[1]; r[2] = v[2];
    return r;
}

array<int>^ Bridge::SelectFacesInRectangle(int x0, int y0, int x1, int y1, bool toggle)
{
    const std::vector<int> ids = SelectFacesRectangleNative(x0, y0, x1, y1, toggle);
    array<int>^ result = gcnew array<int>((int)ids.size());
    for (int i = 0; i < result->Length; ++i) result[i] = ids[(size_t)i];
    return result;
}

void Bridge::SetSelectionRectangle(int x0, int y0, int x1, int y1)
{
    SetSelectionRectangleNative(x0, y0, x1, y1);
}

void Bridge::ClearSelectionRectangle()
{
    ClearSelectionRectangleNative();
}

array<double>^ Bridge::ScreenPointOnPlane(int x, int y, double ox, double oy, double oz, double nx, double ny, double nz)
{
    gp_Vec n(nx, ny, nz);
    if (n.SquareMagnitude() < 1e-18) return nullptr;
    n.Normalize();
    double p[3];
    if (!ScreenPointOnPlaneNative(x, y, gp_Pnt(ox, oy, oz), gp_Dir(n), p)) return nullptr;
    array<double>^ result = gcnew array<double>(3);
    result[0] = p[0]; result[1] = p[1]; result[2] = p[2];
    return result;
}

bool Bridge::AddExclusionOverlay(array<double>^ xyzFlat)
{
    if (xyzFlat == nullptr) return false;
    std::vector<double> xyz(xyzFlat->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    return AddExclusionOverlayNative(xyz);
}

void Bridge::SetExclusionPreview(array<double>^ xyzFlat)
{
    std::vector<double> xyz;
    if (xyzFlat != nullptr)
    {
        xyz.resize(xyzFlat->Length);
        for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    }
    SetExclusionPreviewNative(xyz);
}

void Bridge::ClearExclusionOverlays()
{
    ClearExclusionOverlaysNative();
}

bool Bridge::ShowQualityIssueLocation(double x, double y, double z)
{
    try
    {
        return ShowQualityIssueLocationNative(x, y, z);
    }
    catch (const Standard_Failure&)
    {
        ClearQualityIssueLocationNative();
        return false;
    }
}

void Bridge::ClearQualityIssueLocation()
{
    const bool hadLocation = g_documentSession &&
        !g_documentSession->presentation.qualityIssueLocation.IsNull();
    ClearQualityIssueLocationNative();
    if (hadLocation && !g_view.IsNull())
        g_view->Redraw();
}

int Bridge::HoverFace(int x, int y)
{
    return HoverFaceNative(x, y);
}

void Bridge::HideFaceHighlights()
{
    HideFaceHighlightsNative();
}

void Bridge::ClearHover()
{
    ClearHoverNative();
}

int Bridge::SelectFace(int x, int y, bool toggle)
{
    return SelectFaceNative(x, y, toggle);
}

array<HlrPolyline^>^ Bridge::HlrEdges(double nx, double ny, double nz,
                                     double xx, double xy, double xz, bool includeHidden)
{
    const std::vector<NativeHlrLine> lines = RunHlrGeometryNative(nx, ny, nz, xx, xy, xz, includeHidden);
    List<HlrPolyline^>^ result = gcnew List<HlrPolyline^>();
    for (size_t i = 0; i < lines.size(); ++i)
    {
        HlrPolyline^ poly = gcnew HlrPolyline();
        poly->Visible = lines[i].visible;
        array<double>^ pts = gcnew array<double>((int)lines[i].pts.size());
        for (size_t k = 0; k < lines[i].pts.size(); ++k)
            pts[(int)k] = lines[i].pts[k];
        poly->Points = pts;
        result->Add(poly);
    }
    return result->ToArray();
}

}}
