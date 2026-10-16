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
Handle(AIS_InteractiveObject) g_workingDiagnostic;
std::vector<Handle(AIS_Shape)> g_workingBoundaries;
struct WorkingPresentationNative
{
    long long revision;
    std::vector<Handle(AIS_Shape)> boundaries;
    Handle(AIS_InteractiveObject) diagnostic;
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
    g_working.clear();
    g_workingPresentations.clear();
    ClearWorkingBoundaryNative();
    if (!g_ctx.IsNull() && !g_workingDiagnostic.IsNull()) g_ctx->Remove(g_workingDiagnostic, Standard_False);
    g_workingDiagnostic.Nullify();
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
WorkingStatusNative QueryWorkingNative(long long id, long long revision, double u, double v, double* out, int& faceId)
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

bool WorkingScreenNative(long long id,long long revision,double x,double y,double z,double* out)
{
    if (!FindWorking(id,revision) || g_view.IsNull() || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
    try {
        const auto camera=g_view->Camera();
        const gp_Pnt point(x,y,z);
        if (!camera->IsOrthographic() && gp_Vec(camera->Eye(),point).Dot(gp_Vec(camera->Direction()))<=0) return false;
        const gp_Pnt projected=camera->Project(point);
        int width,height; g_view->Window()->Size(width,height);
        out[0]=(projected.X()+1)*width/2;
        out[1]=(1-projected.Y())*height/2;
        return std::isfinite(out[0]) && std::isfinite(out[1]);
    } catch (const Standard_Failure&) { return false; }
}

void ShowWorkingDiagnosticNative(const std::vector<double>& xyz, bool point)
{
    if (g_ctx.IsNull()) return;
    if (!g_workingDiagnostic.IsNull()) g_ctx->Remove(g_workingDiagnostic,Standard_False);
    g_workingDiagnostic.Nullify();
    if (point && xyz.size()==3) {
        Handle(AIS_Point) marker = new AIS_Point(new Geom_CartesianPoint(gp_Pnt(xyz[0],xyz[1],xyz[2])));
        marker->SetColor(Quantity_Color(1,0.2,0,Quantity_TOC_RGB));
        marker->SetMarker(Aspect_TOM_O_PLUS);
        marker->Attributes()->SetPointAspect(new Prs3d_PointAspect(Aspect_TOM_O_PLUS,
            Quantity_Color(1,0.2,0,Quantity_TOC_RGB),3));
        g_workingDiagnostic=marker;
    }
    if (!g_workingDiagnostic.IsNull()) {
        g_ctx->Display(g_workingDiagnostic,Standard_False);
        g_ctx->Deactivate(g_workingDiagnostic);
    }
    g_view->Redraw();
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

long long CaptureWorkingPresentationNative(double* bounds, double* point, bool& hasBounds, bool& hasPoint)
{
    try {
        WorkingPresentationNative snapshot;
        snapshot.revision=g_modelRevision;
        snapshot.boundaries=g_workingBoundaries;
        snapshot.diagnostic=g_workingDiagnostic;
        if (!g_view.IsNull()) snapshot.camera=new Graphic3d_Camera(g_view->Camera());
        Bnd_Box box;
        for (const auto& boundary : snapshot.boundaries) BRepBndLib::Add(boundary->Shape(),box);
        hasBounds=!box.IsVoid();
        if (hasBounds) box.Get(bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]);
        Handle(AIS_Point) marker=Handle(AIS_Point)::DownCast(snapshot.diagnostic);
        hasPoint=!marker.IsNull();
        if (hasPoint) {
            const gp_Pnt p=marker->Component()->Pnt();
            point[0]=p.X(); point[1]=p.Y(); point[2]=p.Z();
        }
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
    if (g_ctx.IsNull()) return snapshot.boundaries.empty() && snapshot.diagnostic.IsNull() && snapshot.camera.IsNull();
    try {
        ClearWorkingBoundaryNative();
        if (!g_workingDiagnostic.IsNull()) g_ctx->Remove(g_workingDiagnostic,Standard_False);
        g_workingBoundaries=snapshot.boundaries;
        g_workingDiagnostic=snapshot.diagnostic;
        for (const auto& boundary : g_workingBoundaries) {
            g_ctx->Display(boundary,Standard_False); g_ctx->Deactivate(boundary);
        }
        if (!g_workingDiagnostic.IsNull()) {
            g_ctx->Display(g_workingDiagnostic,Standard_False); g_ctx->Deactivate(g_workingDiagnostic);
        }
        if (!snapshot.camera.IsNull() && !g_view.IsNull()) g_view->Camera()->Copy(snapshot.camera);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    } catch (const Standard_Failure&) { return false; }
}
