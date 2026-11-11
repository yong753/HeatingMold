// STEP 내보내기 — 3D 모델 + 확정 발열경로 중심선 + 발열사 솔리드.
// 확정된 3D 체인 좌표를 그대로 쓴다. 재생성/재매핑/작업평면 투영/원점 정규화는 하지 않는다.
// 실패 문자열은 ASCII 토큰이며 사용자 메시지는 managed 계층에서 만든다.

struct NativeStepExportCircuit
{
    std::wstring name;
    std::wstring areaName;
    int r = 0;
    int g = 0;
    int b = 0;
    std::vector<double> points;   // 확정 순서의 xyz flat
};

// 확정 체인을 실제 직경의 발열사 솔리드로 만든다.
// 직선 구간은 원통, 방향이 꺾이는 정점은 구로 메워 굽힘부를 채운다.
// 원통/구를 합집합하고, 합치기가 실패하면 같은 형상의 겹친 솔리드 화합물을 쓴다.
// OCCT 회전 프리미티브는 생성자에서 형상을 만들지 않으므로 Build() 후 Shape()를 읽는다.
bool BuildStepWireSolidNative(const std::vector<double>& flat, double radius, TopoDS_Shape& result)
{
    result.Nullify();
    if (flat.size() < 3 || (flat.size() % 3) != 0) return false;
    if (!std::isfinite(radius) || radius <= 0.0) return false;

    const size_t count = flat.size() / 3;
    std::vector<gp_Pnt> points;
    points.reserve(count);
    for (size_t i = 0; i < count; ++i)
    {
        const double x = flat[i * 3 + 0];
        const double y = flat[i * 3 + 1];
        const double z = flat[i * 3 + 2];
        if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
        points.push_back(gp_Pnt(x, y, z));
    }

    TopTools_ListOfShape parts;
    for (size_t i = 0; i + 1 < count; ++i)
    {
        const gp_Vec step(points[i], points[i + 1]);
        const double length = step.Magnitude();
        if (length <= 1.0e-9) continue;
        BRepPrimAPI_MakeCylinder cylinder(gp_Ax2(points[i], step.Normalized()), radius, length);
        cylinder.Build();
        const TopoDS_Shape segment = cylinder.Shape();
        if (segment.IsNull()) return false;
        parts.Append(segment);
    }

    // 완만한 구간(약 0.08도 미만)은 원통만으로 이어지므로 구를 넣지 않는다.
    for (size_t i = 1; i + 1 < count; ++i)
    {
        const gp_Vec incoming(points[i - 1], points[i]);
        const gp_Vec outgoing(points[i], points[i + 1]);
        if (incoming.Magnitude() <= 1.0e-9 || outgoing.Magnitude() <= 1.0e-9) continue;
        if (incoming.Normalized().Dot(outgoing.Normalized()) > 0.999999) continue;
        BRepPrimAPI_MakeSphere sphere(points[i], radius);
        sphere.Build();
        const TopoDS_Shape joint = sphere.Shape();
        if (joint.IsNull()) return false;
        parts.Append(joint);
    }

    if (parts.IsEmpty()) return false;
    if (parts.Extent() == 1)
    {
        result = parts.First();
        return true;
    }

    BRepAlgoAPI_Fuse fuse;
    fuse.SetArguments(parts);
    fuse.Build();
    if (fuse.IsDone() && !fuse.Shape().IsNull())
    {
        result = fuse.Shape();
        return true;
    }

    // Bool 합치기 실패는 기하 오류가 아니라 연산 실패다. 겹친 솔리드 화합물은 같은
    // 발열사 형상을 담으므로 부분 형상이 아니라 완전한 형상으로 내보낸다.
    BRep_Builder builder;
    TopoDS_Compound compound;
    builder.MakeCompound(compound);
    for (TopTools_ListIteratorOfListOfShape it(parts); it.More(); it.Next())
        builder.Add(compound, it.Value());
    result = compound;
    return true;
}

// 확정 체인의 중심선(폴리라인 와이어)을 그대로 만든다. 좌표는 바꾸지 않는다.
bool BuildStepCentrelineNative(const std::vector<double>& flat, TopoDS_Shape& result)
{
    result.Nullify();
    if (flat.size() < 6 || (flat.size() % 3) != 0) return false;

    BRepBuilderAPI_MakePolygon polygon;
    int added = 0;
    gp_Pnt lastPoint;
    for (size_t i = 0; i + 2 < flat.size(); i += 3)
    {
        const double x = flat[i + 0];
        const double y = flat[i + 1];
        const double z = flat[i + 2];
        if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
        const gp_Pnt point(x, y, z);
        // 연속 중복점은 0길이 에지를 만들므로 건너뛴다.
        if (added > 0 && lastPoint.Distance(point) <= 1.0e-9) continue;
        polygon.Add(point);
        lastPoint = point;
        ++added;
    }
    if (added < 2 || !polygon.IsDone()) return false;
    result = polygon.Wire();
    return !result.IsNull();
}

// XDE 문서에 모델·발열사·발열경로 중심선을 각각 독립 파트로 넣고 STEP으로 쓴 뒤,
// 별도 리더로 다시 읽어 파트 수·경계·솔리드·자유 에지를 검증한다. 현재 문서(g_shape)는 건드리지 않는다.
bool WriteStepDocumentNative(const std::string& path, const std::wstring& modelName,
                             const TopoDS_Shape& model,
                             const std::vector<TopoDS_Shape>& wires,
                             const std::vector<TopoDS_Shape>& centrelines,
                             const std::vector<NativeStepExportCircuit>& circuits,
                             std::string& failure)
{
    failure.clear();
    if (model.IsNull()) { failure = "no-model"; return false; }
    if (path.empty()) { failure = "invalid-path"; return false; }

    Handle(XCAFApp_Application) app = XCAFApp_Application::GetApplication();
    if (app.IsNull()) { failure = "xde-application"; return false; }

    Handle(TDocStd_Document) doc;
    app->NewDocument("MDTV-XCAF", doc);
    if (doc.IsNull()) { failure = "xde-document"; return false; }

    Handle(XCAFDoc_ShapeTool) shapeTool = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
    Handle(XCAFDoc_ColorTool) colorTool = XCAFDoc_DocumentTool::ColorTool(doc->Main());
    if (shapeTool.IsNull() || colorTool.IsNull())
    {
        app->Close(doc);
        failure = "xde-tools";
        return false;
    }

    const TDF_Label modelLabel = shapeTool->AddShape(model, Standard_False, Standard_False);
    TDataStd_Name::Set(modelLabel, TCollection_ExtendedString(modelName.c_str()));

    for (size_t i = 0; i < wires.size(); ++i)
    {
        if (wires[i].IsNull())
        {
            app->Close(doc);
            failure = "wire-build:" + std::to_string(i);
            return false;
        }
        const TDF_Label label = shapeTool->AddShape(wires[i], Standard_False, Standard_False);
        TDataStd_Name::Set(label, TCollection_ExtendedString(circuits[i].name.c_str()));
        const Quantity_Color color(circuits[i].r / 255.0, circuits[i].g / 255.0,
                                   circuits[i].b / 255.0, Quantity_TOC_sRGB);
        colorTool->SetColor(label, color, XCAFDoc_ColorGen);
    }

    // 발열경로 중심선은 발열사와 별개의 파트로 넣는다. 좌표나 직경을 바꿔
    // 가시성을 보정하지 않는다는 원칙 때문에 곡선 파트를 따로 둔다.
    for (size_t i = 0; i < centrelines.size(); ++i)
    {
        if (centrelines[i].IsNull())
        {
            app->Close(doc);
            failure = "wire-build:" + std::to_string(i);
            return false;
        }
        const TDF_Label label = shapeTool->AddShape(centrelines[i], Standard_False, Standard_False);
        const std::wstring centreName = circuits[i].areaName + L" 발열경로";
        TDataStd_Name::Set(label, TCollection_ExtendedString(centreName.c_str()));
        const Quantity_Color color(circuits[i].r / 255.0, circuits[i].g / 255.0,
                                   circuits[i].b / 255.0, Quantity_TOC_sRGB);
        colorTool->SetColor(label, color, XCAFDoc_ColorGen);
    }

    STEPCAFControl_Writer writer;
    writer.SetNameMode(Standard_True);
    writer.SetColorMode(Standard_True);
    writer.SetLayerMode(Standard_True);
    writer.SetPropsMode(Standard_True);
    if (!writer.Transfer(doc, STEPControl_AsIs))
    {
        app->Close(doc);
        failure = "step-transfer";
        return false;
    }
    const IFSelect_ReturnStatus writeStatus = writer.Write(path.c_str());
    app->Close(doc);
    if (writeStatus != IFSelect_RetDone) { failure = "step-write"; return false; }

    // ---- 격리 검증: 방금 쓴 파일만 별도 리더로 읽는다 ----
    {
        std::ifstream probe(path.c_str(), std::ios::binary | std::ios::ate);
        if (!probe.good() || probe.tellg() <= 0) { failure = "verify-empty"; return false; }
    }

    STEPControl_Reader verifier;
    if (verifier.ReadFile(path.c_str()) != IFSelect_RetDone) { failure = "verify-read"; return false; }

    const int expectedRoots = static_cast<int>(wires.size() + centrelines.size()) + 1;
    if (verifier.NbRootsForTransfer() != expectedRoots) { failure = "verify-parts"; return false; }
    verifier.TransferRoots();
    const TopoDS_Shape readBack = verifier.OneShape();
    if (readBack.IsNull()) { failure = "verify-shape"; return false; }

    Bnd_Box expectedBox;
    BRepBndLib::Add(model, expectedBox);
    for (size_t i = 0; i < wires.size(); ++i) BRepBndLib::Add(wires[i], expectedBox);
    for (size_t i = 0; i < centrelines.size(); ++i) BRepBndLib::Add(centrelines[i], expectedBox);
    if (expectedBox.IsVoid()) { failure = "verify-bounds"; return false; }

    Bnd_Box actualBox;
    BRepBndLib::Add(readBack, actualBox);
    if (actualBox.IsVoid()) { failure = "verify-bounds"; return false; }

    double e[6] = {0, 0, 0, 0, 0, 0};
    double a[6] = {0, 0, 0, 0, 0, 0};
    expectedBox.Get(e[0], e[1], e[2], e[3], e[4], e[5]);
    actualBox.Get(a[0], a[1], a[2], a[3], a[4], a[5]);
    const double dx = e[3] - e[0];
    const double dy = e[4] - e[1];
    const double dz = e[5] - e[2];
    const double diagonal = std::sqrt(dx * dx + dy * dy + dz * dz);
    const double tolerance = std::max(0.05, diagonal * 1.0e-6);
    for (int i = 0; i < 6; ++i)
        if (std::fabs(e[i] - a[i]) > tolerance) { failure = "verify-bounds"; return false; }

    // 발열사를 넣었으면 다시 읽은 형상에도 솔리드가 있어야 한다.
    if (!wires.empty())
    {
        int solids = 0;
        for (TopExp_Explorer explorer(readBack, TopAbs_SOLID); explorer.More(); explorer.Next())
            ++solids;
        if (solids <= 0) { failure = "verify-solid"; return false; }
    }

    // 발열경로 중심선은 면에 속하지 않는 자유 에지로 남아 있어야 한다.
    if (!centrelines.empty())
    {
        TopTools_IndexedMapOfShape faceEdges;
        for (TopExp_Explorer explorer(readBack, TopAbs_FACE); explorer.More(); explorer.Next())
            TopExp::MapShapes(explorer.Current(), TopAbs_EDGE, faceEdges);
        int freeEdges = 0;
        for (TopExp_Explorer explorer(readBack, TopAbs_EDGE); explorer.More(); explorer.Next())
        {
            if (!faceEdges.Contains(explorer.Current())) ++freeEdges;
        }
        if (freeEdges < static_cast<int>(centrelines.size())) { failure = "verify-centreline"; return false; }
    }
    return true;
}

bool ExportStepWithRoutesNative(const std::string& path, const std::wstring& modelName,
                                double diameterMm,
                                const std::vector<NativeStepExportCircuit>& circuits,
                                std::string& failure)
{
    failure.clear();
    if (g_shape.IsNull()) { failure = "no-model"; return false; }
    if (!std::isfinite(diameterMm) || diameterMm <= 0.0) { failure = "invalid-diameter"; return false; }

    const double radius = diameterMm * 0.5;
    std::vector<TopoDS_Shape> wires(circuits.size());
    std::vector<TopoDS_Shape> centrelines(circuits.size());
    try
    {
        for (size_t i = 0; i < circuits.size(); ++i)
        {
            if (!BuildStepWireSolidNative(circuits[i].points, radius, wires[i]))
            {
                failure = "wire-build:" + std::to_string(i);
                return false;
            }
            if (!BuildStepCentrelineNative(circuits[i].points, centrelines[i]))
            {
                failure = "wire-build:" + std::to_string(i);
                return false;
            }
        }

        BRepBuilderAPI_Copy copy(g_shape, Standard_True, Standard_False);
        if (!copy.IsDone() || copy.Shape().IsNull()) { failure = "model-copy"; return false; }

        return WriteStepDocumentNative(path, modelName, copy.Shape(), wires, centrelines, circuits, failure);
    }
    catch (const Standard_Failure& error)
    {
        failure = std::string("native-throw:") + error.GetMessageString();
        return false;
    }
}
