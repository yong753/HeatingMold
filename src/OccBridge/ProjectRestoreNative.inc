// 프로젝트 복원 후보: 현재 표시/문서 상태를 건드리지 않고 STEP을 별도 형상으로 읽어
// Face/Edge 토폴로지 지문을 먼저 검증하기 위한 격리 저장소.
// Commit에서만 전역 모델 상태를 한 번 교체한다.

struct ProjectRestoreCandidateNative
{
    TopoDS_Shape shape;
    std::vector<TopoDS_Face> faces;
    TopTools_IndexedMapOfShape faceMap;
    TopTools_IndexedDataMapOfShapeListOfShape edgeFaceMap;
    std::vector<std::string> faceSignatures;
    std::vector<TopoDS_Edge> edges;
    std::vector<std::string> edgeSignatures;
};

std::map<int, std::shared_ptr<ProjectRestoreCandidateNative>> g_projectRestoreCandidates;
int g_nextProjectRestoreCandidateId = 1;
std::string g_projectRestoreCandidateError;

void DiscardAllProjectRestoreCandidates()
{
    g_projectRestoreCandidates.clear();
    g_nextProjectRestoreCandidateId = 1;
}

void ClearLiveModelForRestore()
{
    ClearExitEdgeStoreNative();
    InvalidateWorkingContexts();
    ClearDisplayedShape();
    ClearOverlayNative();
    ClearExclusionOverlaysNative();
    ClearAllAreaHighlightsNative();
    g_shape.Nullify();
    g_faces.clear();
    g_faceTopologySignatures.clear();
    g_faceMap.Clear();
    g_edgeFaceMap.Clear();
    // 계산용 재봉 사본도 원본과 수명을 같이한다.
    g_calcSewnShape.Nullify();
    g_calcSewnFaces.clear();
    g_calcSewnFaceMap.Clear();
    g_calcSewnValid = false;
    g_calcSewnDiagnostic.clear();
    g_calcSewnModifiedFaces = 0;
    g_calcSewnModifiedEdges = 0;
    g_calcSewnRequested = false;
    g_autoAxes[0] = gp::DX();
    g_autoAxes[1] = gp::DY();
    g_autoAxes[2] = gp::DZ();
    DiscardAllProjectRestoreCandidates();
}

int BeginProjectRestoreCandidateNative(const std::string& path, std::string& error)
{
    error.clear();
    g_projectRestoreCandidateError.clear();
    try
    {
        STEPControl_Reader reader;
        if (reader.ReadFile(path.c_str()) != IFSelect_RetDone)
        {
            error = "STEP 파일을 읽지 못했습니다.";
            g_projectRestoreCandidateError = error;
            return 0;
        }
        reader.TransferRoots();
        TopoDS_Shape shape = reader.OneShape();
        if (shape.IsNull())
        {
            error = "STEP 형상이 비어 있습니다.";
            g_projectRestoreCandidateError = error;
            return 0;
        }

        gp_Dir axes[3];
        ComputeAutoAxesForShape(reader.StepModel(), shape, axes, false);
        shape = AlignShapeToWorldAxesForShape(shape, axes);

        auto candidate = std::make_shared<ProjectRestoreCandidateNative>();
        candidate->shape = shape;
        BuildFaceCache(shape, candidate->faces, candidate->faceMap,
                       candidate->edgeFaceMap, candidate->faceSignatures);

        TopTools_IndexedMapOfShape edgeMap;
        TopExp::MapShapes(shape, TopAbs_EDGE, edgeMap);
        candidate->edges.reserve(static_cast<size_t>(edgeMap.Extent()));
        candidate->edgeSignatures.reserve(static_cast<size_t>(edgeMap.Extent()));
        for (int i = 1; i <= edgeMap.Extent(); ++i)
        {
            const TopoDS_Edge edge = TopoDS::Edge(edgeMap(i));
            candidate->edges.push_back(edge);
            candidate->edgeSignatures.push_back(
                EdgeTopologySignature(edge, candidate->edgeFaceMap,
                                      candidate->faceMap, candidate->faceSignatures));
        }

        const int id = g_nextProjectRestoreCandidateId++;
        g_projectRestoreCandidates[id] = candidate;
        return id;
    }
    catch (const Standard_Failure& failure)
    {
        error = failure.GetMessageString() ? failure.GetMessageString() : "STEP 후보 로드 실패";
    }
    catch (const std::exception& failure)
    {
        error = failure.what();
    }
    catch (...)
    {
        error = "STEP 후보 로드 중 알 수 없는 오류";
    }
    g_projectRestoreCandidateError = error;
    return 0;
}

bool CommitProjectRestoreCandidateNative(int candidateId)
{
    const auto it = g_projectRestoreCandidates.find(candidateId);
    if (it == g_projectRestoreCandidates.end() || !it->second)
    {
        g_projectRestoreCandidateError = "유효하지 않은 복원 후보입니다.";
        return false;
    }

    // ClearLiveModelForRestore가 후보 맵을 비우므로 shared_ptr을 먼저 보관한다.
    const std::shared_ptr<ProjectRestoreCandidateNative> candidate = it->second;
    ++g_modelRevision;
    ClearLiveModelForRestore();

    g_shape = candidate->shape;
    g_faces = candidate->faces;
    g_faceMap = candidate->faceMap;
    g_edgeFaceMap = candidate->edgeFaceMap;
    g_faceTopologySignatures = candidate->faceSignatures;
    RebuildCalcSewnCopyNative();   // 복원된 원본에서 계산용 재봉 사본을 다시 만든다

    MeshShapeNative(1.0);
    if (!g_ctx.IsNull())
    {
        g_aisShape = new AIS_ColoredShape(g_shape);
        ConfigureDisplay();
        if (!g_view.IsNull())
        {
            g_view->FitAll(0.01, Standard_False);
            g_view->Redraw();
        }
    }
    g_projectRestoreCandidateError.clear();
    return true;
}

void DiscardProjectRestoreCandidateNative(int candidateId)
{
    g_projectRestoreCandidates.erase(candidateId);
}
