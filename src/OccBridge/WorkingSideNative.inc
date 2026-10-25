// Included inside the native anonymous namespace; contexts are UI-thread owned.
struct WorkingNative
{
    long long id, revision;
    gp_Pnt origin;
    gp_Dir x, y, n;
    std::unique_ptr<IntCurvesFace_ShapeIntersector> intersector;
    std::vector<double> triangles;
    double deflection = 0.05;
    double rayLength = 0;
};
std::map<long long, std::unique_ptr<WorkingNative>> g_working;
long long g_nextWorkingId = 1;
std::vector<Handle(AIS_Shape)> g_workingBoundaries;
struct WorkingPresentationNative
{
    long long revision;
    std::vector<Handle(AIS_Shape)> boundaries;
    Handle(Graphic3d_Camera) camera;
};
std::map<long long, WorkingPresentationNative> g_workingPresentations;
long long g_nextWorkingPresentation = 1;

void ClearWorkingBoundaryNative()
{
    if (!g_ctx.IsNull()) for (auto& shape : g_workingBoundaries) g_ctx->Remove(shape,Standard_False);
    g_workingBoundaries.clear();
}

void InvalidateWorkingContexts()
{
    for (const auto& staged : g_stagedWorkingPolylines)
        if (!g_ctx.IsNull()) g_ctx->Remove(staged.second.ais,Standard_False);
    g_stagedWorkingPolylines.clear();
    g_stagedWorkingRouteOwners.clear();
    g_working.clear();
    g_workingPresentations.clear();
    ClearWorkingBoundaryNative();
}

void ReleaseWorkingNative(long long id)
{
    const auto context=g_working.find(id);
    if (context==g_working.end()) return;
    const long long revision=context->second->revision;
    bool removedRoute=false;
    for (auto it=g_stagedWorkingPolylines.begin();it!=g_stagedWorkingPolylines.end();) {
        if (it->second.contextId!=id || it->second.revision!=revision) { ++it; continue; }
        if (!g_ctx.IsNull()) { try { g_ctx->Remove(it->second.ais,Standard_False); } catch (...) {} }
        it=g_stagedWorkingPolylines.erase(it);
    }
    for (auto owner=g_stagedWorkingRouteOwners.begin();owner!=g_stagedWorkingRouteOwners.end();) {
        if (owner->second.first!=id || owner->second.second!=revision) { ++owner; continue; }
        auto route=g_routePieces.find(owner->first);
        if (route!=g_routePieces.end()) {
            if (!g_ctx.IsNull()) { try { g_ctx->Remove(route->second.ais,Standard_False); } catch (...) {} }
            g_routePieces.erase(route); removedRoute=true;
        }
        owner=g_stagedWorkingRouteOwners.erase(owner);
    }
    g_working.erase(context);
    if (removedRoute) HighlightCircuitNative(std::vector<int>());
    if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
}

WorkingNative* FindWorking(long long id, long long revision)
{
    const auto it = g_working.find(id);
    return revision == g_modelRevision && it != g_working.end() && it->second->revision == revision
        ? it->second.get() : nullptr;
}

long long CreateWorkingNative(double nx, double ny, double nz, double xx, double xy, double xz)
{
    if (g_shape.IsNull() || !std::isfinite(nx) || !std::isfinite(ny) || !std::isfinite(nz) ||
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
        if (!GetModelBoundsInFrameNative(ctx->x, ctx->y, ctx->n, b)) return 0;
        const double margin = std::max(1.0, (b[5] - b[4]) * 0.01);
        ctx->rayLength = b[5]-b[4]+margin+1;
        ctx->origin = gp::Origin().Translated(x * ((b[0]+b[1])/2) +
            gp_Vec(ctx->y) * ((b[2]+b[3])/2) + n * (b[5]+margin));
        ctx->intersector = std::make_unique<IntCurvesFace_ShapeIntersector>();
        ctx->intersector->Load(g_shape, Precision::Confusion());
        // Rebuild once per preview, never per hover. Absolute deflection, explicit angular control.
        BRepTools::Clean(g_shape);
        BRepMesh_IncrementalMesh mesh(g_shape, ctx->deflection, Standard_False, 0.1, Standard_True);
        if (!mesh.IsDone()) return 0;
        for (const auto& face : g_faces) {
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
        if (!g_ctx.IsNull() && !g_aisShape.IsNull()) g_ctx->Redisplay(g_aisShape,Standard_False);
        ctx->id = g_nextWorkingId++; ctx->revision = g_modelRevision;
        const long long id = ctx->id;
        g_working.emplace(id, std::move(ctx));
        return id;
    } catch (const Standard_Failure&) { return 0; }
}

// Matches the managed enum without exposing flat numeric result arrays.
enum class WorkingStatusNative { Hit, Miss, Tangent, InvalidInput, StaleContext, GeometryFailure };
// Never discard a nearer tangent to use a back hit.
WorkingStatusNative QueryWorkingNative(long long id, long long revision, double u, double v, double* out, int& faceId, double* outNormal = nullptr)
{
    WorkingNative* ctx = FindWorking(id, revision);
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
        out[0]=p.X(); out[1]=p.Y(); out[2]=p.Z();
        faceId = g_faceMap.FindIndex(hits.Face(first))-1;
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
            Bnd_Box box; BRepBndLib::Add(g_shape,box);
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

bool WorkingFacesAdjacentNative(long long id, long long revision, int a, int b)
{
    if (!FindWorking(id,revision) || a<0 || b<0 || a>=static_cast<int>(g_faces.size()) || b>=static_cast<int>(g_faces.size())) return false;
    if (a==b) return true;
    if (g_faceAdjacencyRevision!=revision) {
        g_faceAdjacency.clear();
        TopTools_IndexedMapOfShape edgeMap;
        std::vector<std::set<int>> owners;
        for (int i=0;i<static_cast<int>(g_faces.size());++i)
            for (TopExp_Explorer ex(g_faces[i],TopAbs_EDGE);ex.More();ex.Next()) {
                const int index=edgeMap.Add(ex.Current());
                if (static_cast<int>(owners.size())<edgeMap.Extent()) owners.resize(edgeMap.Extent());
                owners[index-1].insert(i);
            }
        for (const auto& faces : owners) for (auto i=faces.begin();i!=faces.end();++i)
            for (auto j=std::next(i);j!=faces.end();++j) g_faceAdjacency.emplace(*i,*j);
        g_faceAdjacencyRevision=revision;
    }
    return g_faceAdjacency.count({std::min(a,b),std::max(a,b)})!=0;
}

bool DisplayWorkingRoutePieceNative(long long id,long long revision,int pieceId,const std::string& kind,
    const std::vector<double>& xyz,double* result)
{
    if (!FindWorking(id,revision) || g_ctx.IsNull() || xyz.size()<6 || xyz.size()%3 || !result) return false;
    // Negative IDs are private staging handles: collisions must never replace another candidate.
    if (pieceId<0 && (g_routePieces.count(pieceId) || g_stagedWorkingRouteOwners.count(pieceId))) return false;
    Handle(AIS_Shape) ais;
    try {
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i=0;i<xyz.size();i+=3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
            poly.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if (!poly.IsDone()) return false;
        GProp_GProps props; BRepGProp::LinearProperties(poly.Wire(),props);
        if (!std::isfinite(props.Mass()) || props.Mass()<=Precision::Confusion()) return false;
        result[0]=props.Mass(); result[1]=xyz[0]; result[2]=xyz[1]; result[3]=xyz[2];
        result[4]=xyz[xyz.size()-3]; result[5]=xyz[xyz.size()-2]; result[6]=xyz[xyz.size()-1];
        if (!g_ctx.IsNull()) {
            std::string k=kind; std::transform(k.begin(),k.end(),k.begin(),[](unsigned char c){return static_cast<char>(std::tolower(c));});
            const bool connector=k=="turnconnector" || k=="turn";
            auto old=g_routePieces.find(pieceId);
            if (old!=g_routePieces.end()) { g_ctx->Remove(old->second.ais,Standard_False); g_routePieces.erase(old); }
            ais=new AIS_Shape(poly.Wire());
            Quantity_Color color=connector ? Quantity_Color(0.1,0.65,1.0,Quantity_TOC_RGB) : Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB);
            g_ctx->SetColor(ais,color,Standard_False);
            if (connector) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
            g_ctx->Display(ais,Standard_False); g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost); g_ctx->Activate(ais,Standard_False);
            if(pieceId<0) {
                auto route=g_routePieces.emplace(pieceId,RoutePieceOverlay{ais,connector,true,color,1.0});
                if(!route.second) { g_ctx->Remove(ais,Standard_False); return false; }
                try {
                    auto owner=g_stagedWorkingRouteOwners.emplace(pieceId,std::make_pair(id,revision));
                    if(!owner.second) { g_routePieces.erase(route.first); g_ctx->Remove(ais,Standard_False); return false; }
                } catch(...) { g_routePieces.erase(pieceId); g_ctx->Remove(ais,Standard_False); throw; }
            } else g_routePieces[pieceId]={ais,connector,true,color,1.0};
        }
        return true;
    } catch (...) { if (!ais.IsNull() && !g_ctx.IsNull()) g_ctx->Remove(ais,Standard_False); return false; }
}

bool StageWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz)
{
    if (!FindWorking(id,revision) || stageId>=0 || xyz.size()<6 || xyz.size()%3 || g_ctx.IsNull() ||
        g_stagedWorkingPolylines.count(stageId) || g_stagedWorkingRouteOwners.count(stageId) || g_routePieces.count(stageId)) return false;
    Handle(AIS_Shape) ais;
    try {
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i=0;i<xyz.size();i+=3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
            poly.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if (!poly.IsDone()) return false;
        ais=new AIS_Shape(poly.Wire());
        g_ctx->SetColor(ais,Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB),Standard_False);
        g_ctx->Display(ais,Standard_False); g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost); g_ctx->Deactivate(ais,Standard_False);
        if(!g_stagedWorkingPolylines.emplace(stageId,StagedWorkingPolyline{ais,id,revision}).second) { g_ctx->Remove(ais,Standard_False); return false; }
        return true;
    } catch (...) { if (!ais.IsNull() && !g_ctx.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch (...) {} } g_stagedWorkingPolylines.erase(stageId); return false; }
}

// 마지막 경로 조각 투영 실패 원인. 스테이징이 false 하나로 수렴해 원인을 구분할 수
// 없으므로, 실패 지점마다 짧은 코드를 남겨 UI가 메시지에 첨부할 수 있게 한다.
std::string g_workingStageDiagnostic;
std::string g_workingStageInputs;
bool WorkingStageFail(const std::string& reason)
{
    g_workingStageDiagnostic = g_workingStageInputs.empty() ? reason : reason + " | " + g_workingStageInputs;
    return false;
}
const char* GetWorkingStageDiagnosticNative() { return g_workingStageDiagnostic.c_str(); }

// BRepProj_Projection이 교선을 하나도 만들지 못하는 퇴화 케이스(예: 2점 조각의 스윕
// 평면이 원통 축과 거의 평행)에서, 소스 폴리라인의 표본점을 같은 투영 방향으로
// 레이캐스트해 표면 위의 점을 얻고 폴리라인 와이어로 재구성한다. 투영 방향을
// 바꾸지 않으므로 결과가 데이텀 평면에서 벗어나지 않고, 인접 조각과도 끝점이 맞는다.
bool ProjectWorkingWireBySampling(const TopoDS_Wire& source,const TopoDS_Shape& target,
    const gp_Dir& direction,TopoDS_Wire& wire)
{
    IntCurvesFace_ShapeIntersector intersector;
    intersector.Load(target,0.01);
    BRepBuilderAPI_MakePolygon polygon;
    gp_Pnt firstPoint,lastPoint; bool hasLast=false;
    auto append=[&](const gp_Pnt& p) {
        if(hasLast&&lastPoint.Distance(p)<=1e-7) return;
        polygon.Add(p);
        if(!hasLast) firstPoint=p;
        lastPoint=p; hasLast=true;
    };
    for(BRepTools_WireExplorer ex(source);ex.More();ex.Next()) {
        BRepAdaptor_Curve curve(ex.Current());
        const double u0=curve.FirstParameter(),u1=curve.LastParameter();
        if(!std::isfinite(u0)||!std::isfinite(u1)||u1<=u0) return WorkingStageFail("sampling-range");
        std::vector<gp_Pnt> samples;
        if(curve.GetType()==GeomAbs_Line) { samples.push_back(curve.Value(u0)); samples.push_back(curve.Value(u1)); }
        else {
            GCPnts_QuasiUniformDeflection sampler(curve,1.0e-3);
            if(!sampler.IsDone()||sampler.NbPoints()<2) return WorkingStageFail("sampling-deflection");
            for(Standard_Integer i=1;i<=sampler.NbPoints();++i) samples.push_back(sampler.Value(i));
        }
        for(const gp_Pnt& sample:samples) {
            intersector.Perform(gp_Lin(sample,direction),0.0,1.0e12);
            if(!intersector.IsDone()||intersector.NbPnt()==0) return WorkingStageFail("sampling-no-hit");
            bool found=false; double nearest=1.0e100; gp_Pnt hit;
            for(Standard_Integer i=1;i<=intersector.NbPnt();++i) {
                if(intersector.State(i)!=TopAbs_IN&&intersector.State(i)!=TopAbs_ON) continue;
                const double t=intersector.WParameter(i);
                if(!std::isfinite(t)||t<0.0) continue;
                if(t<nearest) { nearest=t; hit=intersector.Pnt(i); found=true; }
            }
            if(!found) return WorkingStageFail("sampling-outside");
            append(hit);
        }
    }
    if(!polygon.IsDone()) return WorkingStageFail("sampling-polygon");
    if(source.Closed()&&lastPoint.Distance(firstPoint)>1e-7) polygon.Close();
    wire=polygon.Wire();
    return !wire.IsNull()&&BRepCheck_Analyzer(wire).IsValid()?true:WorkingStageFail("sampling-invalid-wire");
}

bool BuildProjectedWorkingWire(long long id,long long revision,const std::vector<double>& xyz,
    const std::vector<int>& faceIds,const gp_Dir& direction,TopoDS_Wire& wire,double* result)
{
    const WorkingNative* context=FindWorking(id,revision);
    if(!context)
        return WorkingStageFail("context "+std::to_string(id)+"@"+std::to_string(revision)
            +" model@"+std::to_string(g_modelRevision));
    g_workingStageInputs += " origin=("+std::to_string(context->origin.X())+","+std::to_string(context->origin.Y())+","+std::to_string(context->origin.Z())+")";
    if(xyz.size()<6||xyz.size()%3) return WorkingStageFail("xyz-size "+std::to_string(xyz.size()));
    if(faceIds.empty()) return WorkingStageFail("no-faces");
    if(!result) return WorkingStageFail("no-result");
    try {
        BRepBuilderAPI_MakePolygon source;
        for(size_t i=0;i<xyz.size();i+=3) {
            if(!std::isfinite(xyz[i])||!std::isfinite(xyz[i+1])||!std::isfinite(xyz[i+2]))
                return WorkingStageFail("non-finite-xyz "+std::to_string(i/3));
            source.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if(!source.IsDone()) return WorkingStageFail("source-polygon");
        BRepBuilderAPI_Sewing sewing(0.01,Standard_True,Standard_True,Standard_True,Standard_True);
        std::set<int> unique;
        for(int face:faceIds) { if(face<0||face>=static_cast<int>(g_faces.size())) return WorkingStageFail("face-index "+std::to_string(face)); if(unique.insert(face).second) sewing.Add(g_faces[face]); }
        sewing.Perform(); TopoDS_Shape target=sewing.SewedShape(); if(target.IsNull()) return WorkingStageFail("target-null");
        // Check coverage independently per source segment. A disconnected or
        // partially out-of-domain polyline must not pass on its global endpoints.
        // (개별 에지의 BRepProj 결과는 검사하지 않는다 — 퇴화 스윕에서는 교선이 0개일 수
        //  있고, 그 경우는 아래 전체 투영 단계에서 표본점 레이캐스트로 복구한다.)
        // 진단용 스위치: false면 소스 구간의 투영 광선 검사를 건너뛴다. 에지 개수 집계는
        // 그대로 유지하고, 선택면 밖 경로는 아래 BRepProj·와이어 연결·끝점 검사에서 걸러진다.
        const bool checkSourceCoverage=false;
        IntCurvesFace_ShapeIntersector coverage; if(checkSourceCoverage) coverage.Load(target,0.01);
        size_t sourceEdges=0;
        for(BRepTools_WireExplorer ex(source.Wire());ex.More();ex.Next()) {
            if(checkSourceCoverage) {
                BRepAdaptor_Curve curve(ex.Current());
                const double first=curve.FirstParameter(),last=curve.LastParameter();
                if(!std::isfinite(first)||!std::isfinite(last)||last<=first)
                    return WorkingStageFail("source-range edge "+std::to_string(sourceEdges));
                for(double fraction:{0.0,0.5,1.0}) {
                    const gp_Pnt p=curve.Value(first+(last-first)*fraction);
                    coverage.Perform(gp_Lin(p,direction),0.0,1.0e12);
                    bool hit=false;
                    if(coverage.IsDone()) for(Standard_Integer h=1;h<=coverage.NbPnt();++h)
                        if(coverage.State(h)==TopAbs_IN||coverage.State(h)==TopAbs_ON) { hit=true; break; }
                    if(!hit) return WorkingStageFail("coverage-miss edge "+std::to_string(sourceEdges)+" frac "+std::to_string(fraction));
                }
            }
            ++sourceEdges;
        }
        if(sourceEdges==0) return WorkingStageFail("no-source-edges");
        BRepProj_Projection projection(source.Wire(),target,direction);
        std::vector<TopoDS_Wire> wires;
        for(projection.Init();projection.More();projection.Next()) wires.push_back(projection.Current());
        if(wires.empty()) {
            // 스윕 평면이 곡면 룰링과 평행한 퇴화 케이스: BRepProj가 "완료" 상태로
            // 교선 0개를 반환한다. 방향은 그대로 두고 표본점 레이캐스트로 재구성한다.
            if(!ProjectWorkingWireBySampling(source.Wire(),target,direction,wire)) return false;
        } else {
            Handle(TopTools_HSequenceOfShape) projectedEdges=new TopTools_HSequenceOfShape();
            for(const auto& projectedWire:wires)
                for(BRepTools_WireExplorer ex(projectedWire);ex.More();ex.Next()) projectedEdges->Append(ex.Current());
            Handle(TopTools_HSequenceOfShape) connectedWires=new TopTools_HSequenceOfShape();
            ShapeAnalysis_FreeBounds::ConnectEdgesToWires(projectedEdges,0.01,Standard_False,connectedWires);
            if(connectedWires->Length()!=1) return WorkingStageFail("connected-wires "+std::to_string(connectedWires->Length()));
            wire=TopoDS::Wire(connectedWires->Value(1));
            if(!BRepCheck_Analyzer(wire).IsValid()) return WorkingStageFail("wire-invalid");
        }
        size_t ordered=0,edges=0; for(BRepTools_WireExplorer ex(wire);ex.More();ex.Next()) ++ordered;
        for(TopExp_Explorer ex(wire,TopAbs_EDGE);ex.More();ex.Next()) ++edges;
        if(ordered==0||ordered!=edges) return WorkingStageFail("wire-order "+std::to_string(ordered)+"/"+std::to_string(edges));
        GProp_GProps props; BRepGProp::LinearProperties(wire,props);
        if(!std::isfinite(props.Mass())||props.Mass()<=Precision::Confusion()) return WorkingStageFail("zero-length");
        TopoDS_Vertex first,last;
        for(BRepTools_WireExplorer ex(wire);ex.More();ex.Next()) { TopoDS_Vertex a,b; TopExp::Vertices(ex.Current(),a,b,Standard_True); if(first.IsNull()) first=a; last=b; }
        if(first.IsNull()||last.IsNull()) return WorkingStageFail("no-end-vertices");
        gp_Pnt a=BRep_Tool::Pnt(first),b=BRep_Tool::Pnt(last),sa(xyz[0],xyz[1],xyz[2]),sb(xyz[xyz.size()-3],xyz[xyz.size()-2],xyz[xyz.size()-1]);
        auto transverse=[&](const gp_Pnt& p,const gp_Pnt& q){gp_Vec v(p,q),d(direction);return (v-d*v.Dot(d)).Magnitude();};
        if(transverse(a,sa)>0.01||transverse(b,sb)>0.01) std::swap(a,b);
        if(transverse(a,sa)>0.01||transverse(b,sb)>0.01)
            return WorkingStageFail("endpoint-off-surface "+std::to_string(transverse(a,sa))+"/"+std::to_string(transverse(b,sb)));
        result[0]=props.Mass(); result[1]=a.X();result[2]=a.Y();result[3]=a.Z();result[4]=b.X();result[5]=b.Y();result[6]=b.Z(); return true;
    } catch(const Standard_Failure& failure) {
        return WorkingStageFail(std::string("exception ")+(failure.GetMessageString()?failure.GetMessageString():""));
    } catch(...) { return WorkingStageFail("exception"); }
}

bool StageProjectedWorkingRouteNative(long long id,long long revision,int stageId,const std::string& kind,
    const std::vector<double>& xyz,const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    std::string faceText;
    for(size_t i=0;i<faces.size();++i) { if(i) faceText+=","; faceText+=std::to_string(faces[i]); }
    g_workingStageInputs="dir=("+std::to_string(direction.X())+","+std::to_string(direction.Y())+","+std::to_string(direction.Z())+")"
        +" pts="+std::to_string(xyz.size()/3)+" faces="+faceText;
    if(xyz.size()>=3) g_workingStageInputs+=" first=("+std::to_string(xyz[0])+","+std::to_string(xyz[1])+","+std::to_string(xyz[2])+")";
    if(stageId>=0||g_ctx.IsNull()||g_routePieces.count(stageId)||g_stagedWorkingRouteOwners.count(stageId)||g_stagedWorkingPolylines.count(stageId))
        return WorkingStageFail("stage-precheck id "+std::to_string(stageId)+" ctx "+std::to_string(g_ctx.IsNull()?0:1)
            +" route "+std::to_string(g_routePieces.count(stageId))
            +" owner "+std::to_string(g_stagedWorkingRouteOwners.count(stageId))
            +" line "+std::to_string(g_stagedWorkingPolylines.count(stageId)));
    g_workingStageDiagnostic.clear();
    TopoDS_Wire wire; if(!BuildProjectedWorkingWire(id,revision,xyz,faces,direction,wire,result)) return false;
    Handle(AIS_Shape) ais;
    try {
        ais=new AIS_Shape(wire); std::string k=kind;
        std::transform(k.begin(),k.end(),k.begin(),[](unsigned char c){return static_cast<char>(std::tolower(c));});
        bool connector=k=="turnconnector"||k=="turn";
        bool lead=k=="exitpiece"||k=="exit";
        Quantity_Color color=connector?Quantity_Color(0.1,0.65,1.0,Quantity_TOC_RGB)
            :(lead?Quantity_Color(0.95,0.55,0.10,Quantity_TOC_RGB):Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB));
        g_ctx->SetColor(ais,color,Standard_False); if(connector) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
        g_ctx->Display(ais,Standard_False);g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);g_ctx->Activate(ais,Standard_False);
        auto inserted=g_routePieces.emplace(stageId,RoutePieceOverlay{ais,connector,true,color,1.0});
        if(!inserted.second) { g_ctx->Remove(ais,Standard_False); return WorkingStageFail("display-id-collision"); }
        try { if(!g_stagedWorkingRouteOwners.emplace(stageId,std::make_pair(id,revision)).second)
                throw std::runtime_error("staged route owner collision"); }
        catch(...) { g_ctx->Remove(ais,Standard_False); g_routePieces.erase(stageId); throw; }
        return true;
    } catch(...) {
        if(!ais.IsNull()&&!g_ctx.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} }
        auto owner=g_stagedWorkingRouteOwners.find(stageId);
        if(owner!=g_stagedWorkingRouteOwners.end()&&owner->second==std::make_pair(id,revision)) g_stagedWorkingRouteOwners.erase(owner);
        auto route=g_routePieces.find(stageId);
        if(route!=g_routePieces.end()&&route->second.ais==ais) g_routePieces.erase(route);
        return WorkingStageFail("display-exception");
    }
}

bool StageProjectedWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz,
    const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if(stageId>=0||g_ctx.IsNull()||g_stagedWorkingPolylines.count(stageId)||g_stagedWorkingRouteOwners.count(stageId)||g_routePieces.count(stageId)) return false;
    g_workingStageDiagnostic.clear();
    TopoDS_Wire wire; if(!BuildProjectedWorkingWire(id,revision,xyz,faces,direction,wire,result)) return false;
    Handle(AIS_Shape) ais;
    try { ais=new AIS_Shape(wire);g_ctx->SetColor(ais,Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB),Standard_False);
        g_ctx->Display(ais,Standard_False);g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);g_ctx->Deactivate(ais,Standard_False);
        if(!g_stagedWorkingPolylines.emplace(stageId,StagedWorkingPolyline{ais,id,revision}).second) { g_ctx->Remove(ais,Standard_False); return false; }
        return true;
    } catch(...) { if(!ais.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} } g_stagedWorkingPolylines.erase(stageId); return false; }
}

// ---- 인출면 픽: 화면 광선의 최근접 가시면. 선택 상태를 바꾸지 않는 읽기 전용. ----
// status: 0=hit, 1=miss, 2=tangent, 3=invalid/geometry
int PickFacePointNative(int x, int y, double* point, int& faceId, double* normal, double* uv)
{
    if (g_view.IsNull() || g_shape.IsNull() || !point || !normal || !uv) return 3;
    try {
        Standard_Real rx, ry, rz, vx, vy, vz;
        g_view->ConvertWithProj(x, y, rx, ry, rz, vx, vy, vz);
        gp_Vec ray(vx, vy, vz);
        if (ray.SquareMagnitude() < 1e-18) return 3;
        ray.Normalize();
        IntCurvesFace_ShapeIntersector hits;
        hits.Load(g_shape, 1e-7);
        hits.Perform(gp_Lin(gp_Pnt(rx, ry, rz), gp_Dir(ray)), 0.0, 1e100);
        if (!hits.IsDone() || hits.NbPnt() == 0) return 1;
        int best = 0; double nearest = 1e100;
        for (int i = 1; i <= hits.NbPnt(); ++i) {
            if (hits.State(i) != TopAbs_IN && hits.State(i) != TopAbs_ON) continue;
            const double t = hits.WParameter(i);
            if (!std::isfinite(t) || t < 0) continue;
            if (t < nearest) { nearest = t; best = i; }
        }
        if (!best) return 1;
        // Never fall through a nearer occluder to a hidden face.
        const TopoDS_Face face = hits.Face(best);
        if (face.IsNull() || !g_faceMap.Contains(face)) return 3;
        const int id = g_faceMap.FindIndex(face) - 1;
        if (id < 0) return 3;
        const gp_Pnt p = hits.Pnt(best);
        const double u = hits.UParameter(best), v = hits.VParameter(best);
        if (!std::isfinite(u) || !std::isfinite(v)) return 3;
        BRepAdaptor_Surface surface(face);
        gp_Vec n;
        if (surface.GetType() == GeomAbs_Sphere) n = gp_Vec(surface.Sphere().Location(), p);
        else {
            gp_Pnt q; gp_Vec du, dv;
            surface.D1(u, v, q, du, dv);
            n = du.Crossed(dv);
        }
        if (!std::isfinite(n.SquareMagnitude()) || n.SquareMagnitude() < 1e-24) return 2;
        n.Normalize();
        if (face.Orientation() == TopAbs_REVERSED) n.Reverse();
        if (std::fabs(n.Dot(gp_Vec(ray))) < 1e-7) return 2;
        point[0]=p.X(); point[1]=p.Y(); point[2]=p.Z();
        normal[0]=n.X(); normal[1]=n.Y(); normal[2]=n.Z();
        uv[0]=u; uv[1]=v; faceId=id;
        return 0;
    } catch (const Standard_Failure&) { return 3; }
}

// ---- 인출면과 가열면이 공유하는 실제 CAD edge → 3D 폴리라인 ----
bool SharedBoundaryNative(int exitFaceId, const std::vector<int>& heatingFaces,
    double deflection, std::vector<std::vector<double>>& out)
{
    out.clear();
    if (exitFaceId < 0 || exitFaceId >= static_cast<int>(g_faces.size()) || heatingFaces.empty()) return false;
    if (!std::isfinite(deflection) || deflection <= 0) deflection = 0.05;
    try {
        TopTools_IndexedMapOfShape exitEdges;
        for (TopExp_Explorer ex(g_faces[exitFaceId], TopAbs_EDGE); ex.More(); ex.Next())
            exitEdges.Add(ex.Current());
        if (exitEdges.IsEmpty()) return false;
        TopTools_IndexedMapOfShape shared;
        for (int id : heatingFaces) {
            if (id < 0 || id >= static_cast<int>(g_faces.size()) || id == exitFaceId) continue;
            for (TopExp_Explorer ex(g_faces[id], TopAbs_EDGE); ex.More(); ex.Next()) {
                const int index = exitEdges.FindIndex(ex.Current());
                if (index) shared.Add(exitEdges(index));
            }
        }
        for (int i = 1; i <= shared.Extent(); ++i) {
            BRepAdaptor_Curve curve(TopoDS::Edge(shared(i)));
            std::vector<double> poly;
            GCPnts_QuasiUniformDeflection samples(curve, deflection);
            if (samples.IsDone() && samples.NbPoints() >= 2 && samples.NbPoints() <= 20000) {
                for (int k = 1; k <= samples.NbPoints(); ++k) {
                    const gp_Pnt p = samples.Value(k);
                    poly.insert(poly.end(), {p.X(), p.Y(), p.Z()});
                }
            } else {
                const double first = curve.FirstParameter(), last = curve.LastParameter();
                if (!std::isfinite(first) || !std::isfinite(last) || last < first) continue;
                for (double t : {first, last}) {
                    const gp_Pnt p = curve.Value(t);
                    poly.insert(poly.end(), {p.X(), p.Y(), p.Z()});
                }
            }
            if (poly.size() >= 6) out.push_back(poly);
        }
        return !out.empty();
    } catch (const Standard_Failure&) { return false; }
}

// ---- 인출 리드(미사용): 2D 프레임 왕복 정확도에 의존하므로 현재 인출 리드는
//      투영 경로(StageProjectedWorkingRoutePiece)로 스테이징한다. 3D 인출면 리드 구현 시 재검증 필요. ----
bool StageWorkingExitPieceNative(long long id, long long revision, int stageId,
    const std::vector<double>& xyz, const std::vector<int>& faces, double* result)
{
    if (stageId >= 0 || g_ctx.IsNull() || !FindWorking(id, revision) || xyz.size() < 6 || xyz.size() % 3 ||
        faces.empty() || !result || g_routePieces.count(stageId) ||
        g_stagedWorkingRouteOwners.count(stageId) || g_stagedWorkingPolylines.count(stageId)) return false;
    Handle(AIS_Shape) ais;
    try {
        BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
        std::set<int> unique;
        for (int face : faces) {
            if (face < 0 || face >= static_cast<int>(g_faces.size())) return false;
            if (unique.insert(face).second) sewing.Add(g_faces[face]);
        }
        sewing.Perform();
        const TopoDS_Shape target = sewing.SewedShape();
        if (target.IsNull()) return false;
        // 인출 리드는 마진 밴드 안, 즉 선택 면 위에만 놓인다.
        for (size_t i = 0; i < xyz.size(); i += 3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
            const gp_Pnt p(xyz[i], xyz[i+1], xyz[i+2]);
            BRepExtrema_DistShapeShape distance(BRepBuilderAPI_MakeVertex(p).Vertex(), target);
            if (!distance.IsDone() || distance.Value() > 0.05) return false;
        }
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i = 0; i < xyz.size(); i += 3) poly.Add(gp_Pnt(xyz[i], xyz[i+1], xyz[i+2]));
        if (!poly.IsDone()) return false;
        GProp_GProps props; BRepGProp::LinearProperties(poly.Wire(), props);
        if (!std::isfinite(props.Mass()) || props.Mass() <= Precision::Confusion()) return false;
        result[0]=props.Mass(); result[1]=xyz[0]; result[2]=xyz[1]; result[3]=xyz[2];
        result[4]=xyz[xyz.size()-3]; result[5]=xyz[xyz.size()-2]; result[6]=xyz[xyz.size()-1];
        ais = new AIS_Shape(poly.Wire());
        const Quantity_Color color(0.95,0.55,0.10,Quantity_TOC_RGB);
        g_ctx->SetColor(ais,color,Standard_False);
        g_ctx->Display(ais,Standard_False);
        g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);
        g_ctx->Activate(ais,Standard_False);
        auto inserted=g_routePieces.emplace(stageId,RoutePieceOverlay{ais,false,true,color,1.0});
        if(!inserted.second) { g_ctx->Remove(ais,Standard_False); return false; }
        try { if(!g_stagedWorkingRouteOwners.emplace(stageId,std::make_pair(id,revision)).second)
                throw std::runtime_error("staged route owner collision"); }
        catch(...) { g_ctx->Remove(ais,Standard_False); g_routePieces.erase(stageId); throw; }
        return true;
    } catch(...) {
        if(!ais.IsNull()&&!g_ctx.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} }
        auto owner=g_stagedWorkingRouteOwners.find(stageId);
        if(owner!=g_stagedWorkingRouteOwners.end()&&owner->second==std::make_pair(id,revision)) g_stagedWorkingRouteOwners.erase(owner);
        auto route=g_routePieces.find(stageId);
        if(route!=g_routePieces.end()&&route->second.ais==ais) g_routePieces.erase(route);
        return false;
    }
}

void DiscardWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& routes,const std::vector<int>& lines){
    for (int id : routes) {
        auto owner=g_stagedWorkingRouteOwners.find(id);
        if (id>=0 || owner==g_stagedWorkingRouteOwners.end() || owner->second!=std::make_pair(contextId,revision)) continue;
        auto it=g_routePieces.find(id);
        if (it!=g_routePieces.end()) { if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False); g_routePieces.erase(it); }
        g_stagedWorkingRouteOwners.erase(owner);
    }
    for (int id : lines) {
        auto it=g_stagedWorkingPolylines.find(id);
        if (it==g_stagedWorkingPolylines.end() || it->second.contextId!=contextId || it->second.revision!=revision) continue;
        if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False);
        g_stagedWorkingPolylines.erase(it);
    }
}

bool CommitWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& stagedRoutes,const std::vector<int>& finalRoutes,
    const std::vector<int>& stagedLines)
{
    if (!FindWorking(contextId,revision)) return false;
    if (stagedRoutes.size()!=finalRoutes.size()) return false;
    std::set<int> finalSet, stagedRouteSet, stagedLineSet;
    for (size_t i=0;i<stagedRoutes.size();++i)
        if (stagedRoutes[i]>=0 || !stagedRouteSet.insert(stagedRoutes[i]).second || !g_routePieces.count(stagedRoutes[i]) || !g_stagedWorkingRouteOwners.count(stagedRoutes[i]) ||
            g_stagedWorkingRouteOwners.at(stagedRoutes[i])!=std::make_pair(contextId,revision) || finalRoutes[i]<0 || !finalSet.insert(finalRoutes[i]).second) return false;
    for (int id : stagedLines) if (!stagedLineSet.insert(id).second || !g_stagedWorkingPolylines.count(id) ||
        g_stagedWorkingPolylines.at(id).contextId!=contextId || g_stagedWorkingPolylines.at(id).revision!=revision) return false;
    std::map<int,RoutePieceOverlay> nextRoutes;
    std::vector<Handle(AIS_Shape)> nextWorld;
    std::vector<Handle(AIS_Shape)> removed;
    try {
        for (size_t i=0;i<stagedRoutes.size();++i) nextRoutes.emplace(finalRoutes[i],g_routePieces.at(stagedRoutes[i]));
        for (const auto& route:g_routePieces) {
            if(route.first>=0||stagedRouteSet.count(route.first)) continue;
            auto owner=g_stagedWorkingRouteOwners.find(route.first);
            if(owner!=g_stagedWorkingRouteOwners.end()&&owner->second!=std::make_pair(contextId,revision))
                nextRoutes.emplace(route.first,route.second);
        }
        nextWorld.reserve(stagedLines.size());
        for (int id : stagedLines) nextWorld.push_back(g_stagedWorkingPolylines.at(id).ais);
        removed.reserve(g_routePieces.size()+g_worldOverlays.size());
    } catch (...) { return false; }
    try {
        // Candidate geometry is already displayed and selectable. All validation precedes destructive swap.
        HighlightCircuitNative(std::vector<int>());
        for (const auto& old : g_routePieces) {
            // Staged candidates are already displayed and are adopted as-is through
            // nextRoutes (same AIS handles), so only previously committed routes may be removed.
            if (old.first<0) continue;
            removed.push_back(old.second.ais);
            if (!g_ctx.IsNull()) g_ctx->Remove(old.second.ais,Standard_False);
        }
        for (const auto& old : g_worldOverlays) {
            removed.push_back(old);
            if (!g_ctx.IsNull()) g_ctx->Remove(old,Standard_False);
        }
        g_routePieces.swap(nextRoutes); g_worldOverlays.swap(nextWorld);
        for (int id : stagedRoutes) g_stagedWorkingRouteOwners.erase(id);
        for (int id : stagedLines) g_stagedWorkingPolylines.erase(id);
        if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
        return true;
    } catch (...) {
        if (!g_ctx.IsNull()) for (const auto& old : removed) { try { g_ctx->Display(old,Standard_False); } catch (...) {} }
        return false;
    }
}

void ShowWorkingBoundaryNative(const std::vector<std::vector<double>>& loops, const std::vector<bool>& holes)
{
    ClearWorkingBoundaryNative();
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
        g_workingBoundaries.push_back(ais);
    }
    g_view->Redraw();
}

long long CaptureWorkingPresentationNative(double* bounds, bool& hasBounds)
{
    try {
        WorkingPresentationNative snapshot;
        snapshot.revision=g_modelRevision;
        snapshot.boundaries=g_workingBoundaries;
        if (!g_view.IsNull()) snapshot.camera=new Graphic3d_Camera(g_view->Camera());
        Bnd_Box box;
        for (const auto& boundary : snapshot.boundaries) BRepBndLib::Add(boundary->Shape(),box);
        hasBounds=!box.IsVoid();
        if (hasBounds) box.Get(bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]);
        const long long id=g_nextWorkingPresentation++;
        g_workingPresentations.emplace(id,std::move(snapshot));
        return id;
    } catch (const Standard_Failure&) { return 0; }
}

bool RestoreWorkingPresentationNative(long long id)
{
    const auto it=g_workingPresentations.find(id);
    if (it==g_workingPresentations.end() || it->second.revision!=g_modelRevision) return false;
    const auto& snapshot=it->second;
    if (g_ctx.IsNull()) return snapshot.boundaries.empty() && snapshot.camera.IsNull();
    try {
        ClearWorkingBoundaryNative();
        g_workingBoundaries=snapshot.boundaries;
        for (const auto& boundary : g_workingBoundaries) {
            g_ctx->Display(boundary,Standard_False); g_ctx->Deactivate(boundary);
        }
        if (!snapshot.camera.IsNull() && !g_view.IsNull()) g_view->Camera()->Copy(snapshot.camera);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    } catch (const Standard_Failure&) { return false; }
}
