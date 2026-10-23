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
struct WorkingCoordinateSystemPresentationNative
{
    long long revision;
    std::vector<Handle(AIS_Shape)> boundaries;
    Handle(Graphic3d_Camera) camera;
};
std::map<long long, WorkingCoordinateSystemPresentationNative> g_workingPresentations;
long long g_nextWorkingCoordinateSystemPresentation = 1;

void ClearProjectedModelBoundaryNative()
{
    if (!g_ctx.IsNull()) for (auto& shape : g_workingBoundaries) g_ctx->Remove(shape,Standard_False);
    g_workingBoundaries.clear();
}

void InvalidateWorkingSurfaceContexts()
{
    for (const auto& staged : g_stagedWorkingPolylines)
        if (!g_ctx.IsNull()) g_ctx->Remove(staged.second.ais,Standard_False);
    g_stagedWorkingPolylines.clear();
    g_stagedWorkingPathSegmentOwners.clear();
    g_working.clear();
    g_workingPresentations.clear();
    ClearProjectedModelBoundaryNative();
}

void ReleaseWorkingNative(long long id)
{
    const auto context=g_working.find(id);
    if (context==g_working.end()) return;
    const long long revision=context->second->revision;
    bool removedSegment=false;
    for (auto it=g_stagedWorkingPolylines.begin();it!=g_stagedWorkingPolylines.end();) {
        if (it->second.contextId!=id || it->second.revision!=revision) { ++it; continue; }
        if (!g_ctx.IsNull()) { try { g_ctx->Remove(it->second.ais,Standard_False); } catch (...) {} }
        it=g_stagedWorkingPolylines.erase(it);
    }
    for (auto owner=g_stagedWorkingPathSegmentOwners.begin();owner!=g_stagedWorkingPathSegmentOwners.end();) {
        if (owner->second.first!=id || owner->second.second!=revision) { ++owner; continue; }
        auto path=g_pathSegments.find(owner->first);
        if (path!=g_pathSegments.end()) {
            if (!g_ctx.IsNull()) { try { g_ctx->Remove(path->second.ais,Standard_False); } catch (...) {} }
            g_pathSegments.erase(path); removedSegment=true;
        }
        owner=g_stagedWorkingPathSegmentOwners.erase(owner);
    }
    g_working.erase(context);
    if (removedSegment) HighlightCircuitNative(std::vector<int>());
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

bool DisplayWorkingPathSegmentNative(long long id,long long revision,int pieceId,const std::string& kind,
    const std::vector<double>& xyz,double* result)
{
    if (!FindWorking(id,revision) || g_ctx.IsNull() || xyz.size()<6 || xyz.size()%3 || !result) return false;
    // Negative IDs are private staging handles: collisions must never replace another candidate.
    if (pieceId<0 && (g_pathSegments.count(pieceId) || g_stagedWorkingPathSegmentOwners.count(pieceId))) return false;
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
            const bool turnSegment=k=="turnsegment" || k=="turn";
            auto old=g_pathSegments.find(pieceId);
            if (old!=g_pathSegments.end()) { g_ctx->Remove(old->second.ais,Standard_False); g_pathSegments.erase(old); }
            ais=new AIS_Shape(poly.Wire());
            Quantity_Color color=turnSegment ? Quantity_Color(0.1,0.65,1.0,Quantity_TOC_RGB) : Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB);
            g_ctx->SetColor(ais,color,Standard_False);
            if (turnSegment) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
            g_ctx->Display(ais,Standard_False); g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost); g_ctx->Activate(ais,Standard_False);
            if(pieceId<0) {
                auto path=g_pathSegments.emplace(pieceId,HeatingPathSegmentOverlay{ais,turnSegment,true,color,1.0});
                if(!path.second) { g_ctx->Remove(ais,Standard_False); return false; }
                try {
                    auto owner=g_stagedWorkingPathSegmentOwners.emplace(pieceId,std::make_pair(id,revision));
                    if(!owner.second) { g_pathSegments.erase(path.first); g_ctx->Remove(ais,Standard_False); return false; }
                } catch(...) { g_pathSegments.erase(pieceId); g_ctx->Remove(ais,Standard_False); throw; }
            } else g_pathSegments[pieceId]={ais,turnSegment,true,color,1.0};
        }
        return true;
    } catch (...) { if (!ais.IsNull() && !g_ctx.IsNull()) g_ctx->Remove(ais,Standard_False); return false; }
}

bool StageWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz)
{
    if (!FindWorking(id,revision) || stageId>=0 || xyz.size()<6 || xyz.size()%3 || g_ctx.IsNull() ||
        g_stagedWorkingPolylines.count(stageId) || g_stagedWorkingPathSegmentOwners.count(stageId) || g_pathSegments.count(stageId)) return false;
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

bool BuildProjectedWorkingWire(long long id,long long revision,const std::vector<double>& xyz,
    const std::vector<int>& faceIds,const gp_Dir& direction,TopoDS_Wire& wire,double* result)
{
    if (!FindWorking(id,revision) || xyz.size()<6 || xyz.size()%3 || faceIds.empty() || !result) return false;
    try {
        BRepBuilderAPI_MakePolygon source;
        for(size_t i=0;i<xyz.size();i+=3) {
            if(!std::isfinite(xyz[i])||!std::isfinite(xyz[i+1])||!std::isfinite(xyz[i+2])) return false;
            source.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if(!source.IsDone()) return false;
        BRepBuilderAPI_Sewing sewing(0.01,Standard_True,Standard_True,Standard_True,Standard_True);
        std::set<int> unique;
        for(int face:faceIds) { if(face<0||face>=static_cast<int>(g_faces.size())) return false; if(unique.insert(face).second) sewing.Add(g_faces[face]); }
        sewing.Perform(); TopoDS_Shape target=sewing.SewedShape(); if(target.IsNull()) return false;
        // Check coverage independently per source segment. A disconnected or
        // partially out-of-domain polyline must not pass on its global endpoints.
        IntCurvesFace_ShapeIntersector coverage; coverage.Load(target,0.01);
        size_t sourceEdges=0;
        for(BRepTools_WireExplorer ex(source.Wire());ex.More();ex.Next()) {
            BRepBuilderAPI_MakeWire oneEdge;
            oneEdge.Add(ex.Current());
            if(!oneEdge.IsDone()) return false;
            BRepProj_Projection edgeProjection(oneEdge.Wire(),target,direction);
            if(!edgeProjection.IsDone()||!edgeProjection.More()) return false;
            BRepAdaptor_Curve curve(ex.Current());
            const double first=curve.FirstParameter(),last=curve.LastParameter();
            if(!std::isfinite(first)||!std::isfinite(last)||last<=first) return false;
            for(double fraction:{0.0,0.5,1.0}) {
                const gp_Pnt p=curve.Value(first+(last-first)*fraction);
                coverage.Perform(gp_Lin(p,direction),0.0,1.0e12);
                bool hit=false;
                if(coverage.IsDone()) for(Standard_Integer h=1;h<=coverage.NbPnt();++h)
                    if(coverage.State(h)==TopAbs_IN||coverage.State(h)==TopAbs_ON) { hit=true; break; }
                if(!hit) return false;
            }
            ++sourceEdges;
        }
        if(sourceEdges==0) return false;
        BRepProj_Projection projection(source.Wire(),target,direction);
        std::vector<TopoDS_Wire> wires;
        for(projection.Init();projection.More();projection.Next()) wires.push_back(projection.Current());
        if(wires.empty()) return false;
        Handle(TopTools_HSequenceOfShape) projectedEdges=new TopTools_HSequenceOfShape();
        for(const auto& projectedWire:wires)
            for(BRepTools_WireExplorer ex(projectedWire);ex.More();ex.Next()) projectedEdges->Append(ex.Current());
        Handle(TopTools_HSequenceOfShape) connectedWires=new TopTools_HSequenceOfShape();
        ShapeAnalysis_FreeBounds::ConnectEdgesToWires(projectedEdges,0.01,Standard_False,connectedWires);
        if(connectedWires->Length()!=1) return false;
        wire=TopoDS::Wire(connectedWires->Value(1));
        if(!BRepCheck_Analyzer(wire).IsValid()) return false;
        size_t ordered=0,edges=0; for(BRepTools_WireExplorer ex(wire);ex.More();ex.Next()) ++ordered;
        for(TopExp_Explorer ex(wire,TopAbs_EDGE);ex.More();ex.Next()) ++edges;
        if(ordered==0||ordered!=edges) return false;
        GProp_GProps props; BRepGProp::LinearProperties(wire,props);
        if(!std::isfinite(props.Mass())||props.Mass()<=Precision::Confusion()) return false;
        TopoDS_Vertex first,last;
        for(BRepTools_WireExplorer ex(wire);ex.More();ex.Next()) { TopoDS_Vertex a,b; TopExp::Vertices(ex.Current(),a,b,Standard_True); if(first.IsNull()) first=a; last=b; }
        if(first.IsNull()||last.IsNull()) return false;
        gp_Pnt a=BRep_Tool::Pnt(first),b=BRep_Tool::Pnt(last),sa(xyz[0],xyz[1],xyz[2]),sb(xyz[xyz.size()-3],xyz[xyz.size()-2],xyz[xyz.size()-1]);
        auto transverse=[&](const gp_Pnt& p,const gp_Pnt& q){gp_Vec v(p,q),d(direction);return (v-d*v.Dot(d)).Magnitude();};
        if(transverse(a,sa)>0.01||transverse(b,sb)>0.01) std::swap(a,b);
        if(transverse(a,sa)>0.01||transverse(b,sb)>0.01) return false;
        result[0]=props.Mass(); result[1]=a.X();result[2]=a.Y();result[3]=a.Z();result[4]=b.X();result[5]=b.Y();result[6]=b.Z(); return true;
    } catch(...) { return false; }
}

bool StageProjectedWorkingPathSegmentNative(long long id,long long revision,int stageId,const std::string& kind,
    const std::vector<double>& xyz,const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if(stageId>=0||g_ctx.IsNull()||g_pathSegments.count(stageId)||g_stagedWorkingPathSegmentOwners.count(stageId)||g_stagedWorkingPolylines.count(stageId)) return false;
    TopoDS_Wire wire; if(!BuildProjectedWorkingWire(id,revision,xyz,faces,direction,wire,result)) return false;
    Handle(AIS_Shape) ais;
    try {
        ais=new AIS_Shape(wire); std::string k=kind;
        std::transform(k.begin(),k.end(),k.begin(),[](unsigned char c){return static_cast<char>(std::tolower(c));});
        bool turnSegment=k=="turnsegment"||k=="turn"; Quantity_Color color=turnSegment?Quantity_Color(0.1,0.65,1.0,Quantity_TOC_RGB):Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB);
        g_ctx->SetColor(ais,color,Standard_False); if(turnSegment) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
        g_ctx->Display(ais,Standard_False);g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);g_ctx->Activate(ais,Standard_False);
        auto inserted=g_pathSegments.emplace(stageId,HeatingPathSegmentOverlay{ais,turnSegment,true,color,1.0});
        if(!inserted.second) { g_ctx->Remove(ais,Standard_False); return false; }
        try { if(!g_stagedWorkingPathSegmentOwners.emplace(stageId,std::make_pair(id,revision)).second)
                throw std::runtime_error("staged path owner collision"); }
        catch(...) { g_ctx->Remove(ais,Standard_False); g_pathSegments.erase(stageId); throw; }
        return true;
    } catch(...) {
        if(!ais.IsNull()&&!g_ctx.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} }
        auto owner=g_stagedWorkingPathSegmentOwners.find(stageId);
        if(owner!=g_stagedWorkingPathSegmentOwners.end()&&owner->second==std::make_pair(id,revision)) g_stagedWorkingPathSegmentOwners.erase(owner);
        auto path=g_pathSegments.find(stageId);
        if(path!=g_pathSegments.end()&&path->second.ais==ais) g_pathSegments.erase(path);
        return false;
    }
}

bool StageProjectedWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz,
    const std::vector<int>& faces,const gp_Dir& direction,double* result)
{
    if(stageId>=0||g_ctx.IsNull()||g_stagedWorkingPolylines.count(stageId)||g_stagedWorkingPathSegmentOwners.count(stageId)||g_pathSegments.count(stageId)) return false;
    TopoDS_Wire wire; if(!BuildProjectedWorkingWire(id,revision,xyz,faces,direction,wire,result)) return false;
    Handle(AIS_Shape) ais;
    try { ais=new AIS_Shape(wire);g_ctx->SetColor(ais,Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB),Standard_False);
        g_ctx->Display(ais,Standard_False);g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost);g_ctx->Deactivate(ais,Standard_False);
        if(!g_stagedWorkingPolylines.emplace(stageId,StagedWorkingPolyline{ais,id,revision}).second) { g_ctx->Remove(ais,Standard_False); return false; }
        return true;
    } catch(...) { if(!ais.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch(...) {} } g_stagedWorkingPolylines.erase(stageId); return false; }
}

void DiscardWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& paths,const std::vector<int>& lines)
{
    for (int id : paths) {
        auto owner=g_stagedWorkingPathSegmentOwners.find(id);
        if (id>=0 || owner==g_stagedWorkingPathSegmentOwners.end() || owner->second!=std::make_pair(contextId,revision)) continue;
        auto it=g_pathSegments.find(id);
        if (it!=g_pathSegments.end()) { if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False); g_pathSegments.erase(it); }
        g_stagedWorkingPathSegmentOwners.erase(owner);
    }
    for (int id : lines) {
        auto it=g_stagedWorkingPolylines.find(id);
        if (it==g_stagedWorkingPolylines.end() || it->second.contextId!=contextId || it->second.revision!=revision) continue;
        if (!g_ctx.IsNull()) g_ctx->Remove(it->second.ais,Standard_False);
        g_stagedWorkingPolylines.erase(it);
    }
}

bool CommitWorkingGeometryNative(long long contextId,long long revision,const std::vector<int>& stagedSegments,const std::vector<int>& finalSegments,
    const std::vector<int>& stagedLines)
{
    if (!FindWorking(contextId,revision)) return false;
    if (stagedSegments.size()!=finalSegments.size()) return false;
    std::set<int> finalSet, stagedSegmentSet, stagedLineSet;
    for (size_t i=0;i<stagedSegments.size();++i)
        if (stagedSegments[i]>=0 || !stagedSegmentSet.insert(stagedSegments[i]).second || !g_pathSegments.count(stagedSegments[i]) || !g_stagedWorkingPathSegmentOwners.count(stagedSegments[i]) ||
            g_stagedWorkingPathSegmentOwners.at(stagedSegments[i])!=std::make_pair(contextId,revision) || finalSegments[i]<0 || !finalSet.insert(finalSegments[i]).second) return false;
    for (int id : stagedLines) if (!stagedLineSet.insert(id).second || !g_stagedWorkingPolylines.count(id) ||
        g_stagedWorkingPolylines.at(id).contextId!=contextId || g_stagedWorkingPolylines.at(id).revision!=revision) return false;
    std::map<int,HeatingPathSegmentOverlay> nextPathSegments;
    std::vector<Handle(AIS_Shape)> nextWorld;
    std::vector<Handle(AIS_Shape)> removed;
    std::set<int> stagedSet;
    try {
        for (size_t i=0;i<stagedSegments.size();++i) nextPathSegments.emplace(finalSegments[i],g_pathSegments.at(stagedSegments[i]));
        for (const auto& path:g_pathSegments) {
            if(path.first>=0||stagedSegmentSet.count(path.first)) continue;
            auto owner=g_stagedWorkingPathSegmentOwners.find(path.first);
            if(owner!=g_stagedWorkingPathSegmentOwners.end()&&owner->second!=std::make_pair(contextId,revision))
                nextPathSegments.emplace(path.first,path.second);
        }
        nextWorld.reserve(stagedLines.size());
        for (int id : stagedLines) nextWorld.push_back(g_stagedWorkingPolylines.at(id).ais);
        removed.reserve(g_pathSegments.size()+g_worldOverlays.size());
        stagedSet.insert(stagedSegments.begin(),stagedSegments.end());
    } catch (...) { return false; }
    try {
        // Candidate geometry is already displayed and selectable. All validation precedes destructive swap.
        HighlightCircuitNative(std::vector<int>());
        for (const auto& old : g_pathSegments) {
            // Keep private candidates belonging to other live contexts. Replace committed
            // positive paths and only this transaction's staged candidates.
            if (old.first<0 && !stagedSet.count(old.first)) continue;
            removed.push_back(old.second.ais);
            if (!g_ctx.IsNull()) g_ctx->Remove(old.second.ais,Standard_False);
        }
        for (const auto& old : g_worldOverlays) {
            removed.push_back(old);
            if (!g_ctx.IsNull()) g_ctx->Remove(old,Standard_False);
        }
        g_pathSegments.swap(nextPathSegments); g_worldOverlays.swap(nextWorld);
        for (int id : stagedSegments) g_stagedWorkingPathSegmentOwners.erase(id);
        for (int id : stagedLines) g_stagedWorkingPolylines.erase(id);
        if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
        return true;
    } catch (...) {
        if (!g_ctx.IsNull()) for (const auto& old : removed) { try { g_ctx->Display(old,Standard_False); } catch (...) {} }
        return false;
    }
}

void ShowProjectedModelBoundaryNative(const std::vector<std::vector<double>>& loops, const std::vector<bool>& holes)
{
    ClearProjectedModelBoundaryNative();
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

long long CaptureWorkingCoordinateSystemPresentationNative(double* bounds, bool& hasBounds)
{
    try {
        WorkingCoordinateSystemPresentationNative snapshot;
        snapshot.revision=g_modelRevision;
        snapshot.boundaries=g_workingBoundaries;
        if (!g_view.IsNull()) snapshot.camera=new Graphic3d_Camera(g_view->Camera());
        Bnd_Box box;
        for (const auto& boundary : snapshot.boundaries) BRepBndLib::Add(boundary->Shape(),box);
        hasBounds=!box.IsVoid();
        if (hasBounds) box.Get(bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]);
        const long long id=g_nextWorkingCoordinateSystemPresentation++;
        g_workingPresentations.emplace(id,std::move(snapshot));
        return id;
    } catch (const Standard_Failure&) { return 0; }
}

bool RestoreWorkingCoordinateSystemPresentationNative(long long id)
{
    const auto it=g_workingPresentations.find(id);
    if (it==g_workingPresentations.end() || it->second.revision!=g_modelRevision) return false;
    const auto& snapshot=it->second;
    if (g_ctx.IsNull()) return snapshot.boundaries.empty() && snapshot.camera.IsNull();
    try {
        ClearProjectedModelBoundaryNative();
        g_workingBoundaries=snapshot.boundaries;
        for (const auto& boundary : g_workingBoundaries) {
            g_ctx->Display(boundary,Standard_False); g_ctx->Deactivate(boundary);
        }
        if (!snapshot.camera.IsNull() && !g_view.IsNull()) g_view->Camera()->Copy(snapshot.camera);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    } catch (const Standard_Failure&) { return false; }
}
