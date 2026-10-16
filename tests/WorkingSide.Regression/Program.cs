using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

static class Program
{
    static int count;
    static void Check(bool value,string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS "+label); ++count; }
    static ProjectionFrame Frame(WorkingContext c) => new(new(c.Frame[0],c.Frame[1],c.Frame[2]),
        new(c.Frame[9],c.Frame[10],c.Frame[11]),new(c.Frame[3],c.Frame[4],c.Frame[5]));
    static WorkingHit Query(WorkingContext c,Vec3 world)
    { var uv=Frame(c).Project(world); return Bridge.QueryWorkingSurface(c.Id,c.Revision,uv.X,uv.Y); }
    static IReadOnlyList<WorkingBoundaryRing> Rings(WorkingContext c)
    {
        var f=Frame(c); var triangles=new List<IReadOnlyList<Vec2>>();
        for (int i=0;i<c.Triangles.Length;i+=9)
            triangles.Add(new[]{f.Project(new(c.Triangles[i],c.Triangles[i+1],c.Triangles[i+2])),
                f.Project(new(c.Triangles[i+3],c.Triangles[i+4],c.Triangles[i+5])),
                f.Project(new(c.Triangles[i+6],c.Triangles[i+7],c.Triangles[i+8]))});
        return WorkingBoundary.FromTriangles(triangles,c.MeshDeflection);
    }
    static object Invoke(object target,string name,params object[] args) =>
        target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static T Field<T>(object target,string name) =>
        (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static void SetField(object target,string name,object value) =>
        target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    static bool Same(double[] a,double[] b) => a==null || b==null ? a==b :
        a.Length==b.Length && a.Zip(b).All(p=>Math.Abs(p.First-p.Second)<1e-8);
    static bool SamePresentation(WorkingPresentation a,WorkingPresentation b) =>
        a.BoundaryCount==b.BoundaryCount && Same(a.BoundaryBounds,b.BoundaryBounds) && Same(a.DiagnosticPoint,b.DiagnosticPoint);

    static void CheckPreviewRollback(MainWindow window,WorkingContext projectionContext,bool throwAfterCamera)
    {
        var state=Field<WorkingSideSession>(window,"_workingSide");
        var oldPreview=state.Preview; var oldConfirmed=state.Confirmed;
        var oldRings=Field<IReadOnlyList<WorkingBoundaryRing>>(window,"_workingRings");
        var oldSample=Field<WorkingBoundarySample>(window,"_workingBoundarySample");
        var oldBaseline=Field<object>(window,"_workingPreviewBaseline");
        var box=(CheckBox)window.FindName("WorkingPickBox");
        var viewport=(OccViewport)window.FindName("Viewport");
        bool oldChecked=box.IsChecked==true,oldMode=viewport.WorkingBoundaryPickMode;
        var text=((TextBlock)window.FindName("WorkingSideText")).Text;
        var before=Bridge.CaptureWorkingPresentation();
        var beforeScreen=Bridge.WorkingPointToScreen(projectionContext.Id,projectionContext.Revision,15,5,0);
        long candidate=0,revision=0;
        var prepare=Field<Func<long,long,bool>>(window,"_prepareWorkingFront");
        SetField(window,"_prepareWorkingFront",new Func<long,long,bool>((id,rev)=>
        {
            candidate=id; revision=rev;
            var during=Bridge.CaptureWorkingPresentation();
            Check(during.BoundaryCount>0 && !Same(during.BoundaryBounds,before.BoundaryBounds),
                "failure injected only after candidate native geometry is displayed");
            Bridge.ReleaseWorkingPresentation(during.Id);
            Bridge.ReturnToWorkingFront(id,rev); // Mutate camera too, before failure.
            if (throwAfterCamera) throw new InvalidOperationException("injected post-display camera preparation exception");
            return false;
        }));
        try
        {
            Invoke(window,"PreviewWorkingSide",new Vec3(0,-.5,-1));
            var after=Bridge.CaptureWorkingPresentation();
            try
            {
                Check(candidate!=0 && SamePresentation(before,after),"failed preview restores exact boundary/marker presentation, no dead candidate geometry");
                Check(Same(beforeScreen,Bridge.WorkingPointToScreen(projectionContext.Id,projectionContext.Revision,15,5,0)),
                    "failed preview restores camera projection/orientation/zoom/pan");
                Check(ReferenceEquals(state.Preview,oldPreview) && ReferenceEquals(state.Confirmed,oldConfirmed) &&
                    ReferenceEquals(oldRings,Field<IReadOnlyList<WorkingBoundaryRing>>(window,"_workingRings")) &&
                    ReferenceEquals(oldSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")) &&
                    ReferenceEquals(oldBaseline,Field<object>(window,"_workingPreviewBaseline")),
                    "failed preview restores prior preview/confirmed/rings/sample/savepoint logical state");
                Check(box.IsChecked==oldChecked && viewport.WorkingBoundaryPickMode==oldMode &&
                    ((TextBlock)window.FindName("WorkingSideText")).Text==text,"failed preview restores mode and side label");
                Check(Bridge.QueryWorkingSurface(candidate,revision,0,0).Status==WorkingQueryStatus.StaleContext,
                    "failed candidate context is released only after rollback");
            }
            finally { Bridge.ReleaseWorkingPresentation(after.Id); }
        }
        finally { SetField(window,"_prepareWorkingFront",prepare); Bridge.ReleaseWorkingPresentation(before.Id); }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string path=Path.Combine(args[0],"layers.stp");
        Check(Bridge.LoadStep(path),"load layered model without face selection");
        Check(Bridge.CreateWorkingContext(0,0,0,1,0,0)==null,"invalid working direction cannot create context");
        var front=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Check(front!=null && front.Triangles.Length>0,"whole-model native context and mesh");
        Check(front.Frame[2]>20,"datum beyond whole-model maximum");
        var top=Query(front,new(0,0,0)); var exposed=Query(front,new(40,0,0));
        Check(top.Status==WorkingQueryStatus.Hit && Math.Abs(top.Point[2]-20)<1e-6,"nearest front full-model hit hides lower surface");
        Check(exposed.Status==WorkingQueryStatus.Hit && Math.Abs(exposed.Point[2])<1e-6,"partly occluded face usable only at exposed points");
        Check(top.FaceId!=exposed.FaceId,"first hit returns actual face ID, not selected filter");
        Check(Query(front,new(80,0,0)).Status==WorkingQueryStatus.Miss,"outside silhouette has no hidden/back fallback");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,double.NaN,0).Status==WorkingQueryStatus.InvalidInput,"invalid UV status");
        var back=Bridge.CreateWorkingContext(0,0,-1,1,0,0);
        Check(back.Frame[2]<0 && Math.Abs(Query(back,new(0,0,0)).Point[2])<1e-6,"flipped side sees lower first hit and opposite outside datum");
        Check(Query(front,new(0,0,0)).Point[2]==20,"new preview leaves confirmed-context geometry unchanged");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision+1,0,0).Status==WorkingQueryStatus.StaleContext,"native rejects wrong revision");
        Check(Bridge.QueryWorkingSurface(back.Id,front.Revision,0,0).Status==WorkingQueryStatus.Hit,"independent same-revision contexts coexist");
        Bridge.ReleaseWorkingContext(back.Id);
        Check(Bridge.QueryWorkingSurface(back.Id,back.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"released context rejected");
        var layeredRings=Rings(front);
        Check(layeredRings.Count==1 && !layeredRings[0].IsHole,"occluder edges and shared triangles are not outer boundary");

        using var source=new HwndSource(new HwndSourceParameters("Working-side regression") { Width=900,Height=700 });
        Check(Bridge.InitViewer(source.Handle,900,700),"offscreen native viewer initialized");
        Bridge.SetViewFrame(0,0,1,0,1,0);
        Check(!Bridge.IsWorkingPointVisible(front.Id,front.Revision,0,0,0,.001),"camera visibility rejects occluded candidate at its own screen point");
        Check(Bridge.IsWorkingPointVisible(front.Id,front.Revision,40,0,0,.001),"camera visibility allows exposed lower surface");
        Bridge.SetViewFrame(1,0,1,0,1,0);
        Bridge.ZoomView(1.3);
        Check(Query(front,new(0,0,0)).Point[2]==20,"camera orbit/zoom do not alter fixed working side");

        var window=new MainWindow();
        var viewport=(OccViewport)window.FindName("Viewport");
        var pickBox=(CheckBox)window.FindName("WorkingPickBox");
        viewport.RectangleSelectionMode=true; viewport.ConnectorEditMode=true; viewport.PowerLocationMode=true;
        pickBox.IsChecked=true;
        Check(pickBox.IsChecked==false && !viewport.WorkingBoundaryPickMode && viewport.RectangleSelectionMode &&
            viewport.ConnectorEditMode && viewport.PowerLocationMode,"ineligible unconfirmed checkbox entry preserves other interaction modes");
        viewport.RectangleSelectionMode=false; viewport.ConnectorEditMode=false; viewport.PowerLocationMode=false;
        CheckPreviewRollback(window,front,false);
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,1));
        Invoke(window,"WorkingConfirm_Click",null,new RoutedEventArgs());
        var state=Field<WorkingSideSession>(window,"_workingSide");
        Check(state.Confirmed!=null && state.Preview==null,"UI atomic confirmation without heating face or pattern");
        var screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,50,0,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        var status=(TextBlock)window.FindName("StatusText");
        Console.WriteLine(status.Text);
        Check(status.Text.StartsWith("외곽 진단 UV="),"independent full-model outer boundary screen pick succeeds");
        pickBox.IsChecked=true;
        var confirmedSample=Field<WorkingBoundarySample>(window,"_workingBoundarySample");
        var confirmedPresentation=Bridge.CaptureWorkingPresentation();
        Check(confirmedPresentation.DiagnosticPoint!=null && viewport.WorkingBoundaryPickMode,"confirmed marker and diagnostic mode active before preview");
        Bridge.SetViewPerspective(true); Bridge.SetViewFrame(.2,.3,1,0,1,0); Bridge.ZoomView(1.2);
        CheckPreviewRollback(window,front,true);
        var cancelScreen=Bridge.WorkingPointToScreen(front.Id,front.Revision,15,5,0);
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,-1));
        Check(ReferenceEquals(confirmedSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")) &&
            !viewport.WorkingBoundaryPickMode && pickBox.IsChecked==false,"successful preview preserves confirmed sample and suspends, not discards, its pick mode");
        var previewPresentation=Bridge.CaptureWorkingPresentation();
        Check(previewPresentation.DiagnosticPoint==null,"confirmed marker hidden while inspecting an unconfirmed preview");
        Bridge.ReleaseWorkingPresentation(previewPresentation.Id);
        viewport.RectangleSelectionMode=true; viewport.ConnectorEditMode=true; viewport.PowerLocationMode=true;
        pickBox.IsChecked=true;
        Check(pickBox.IsChecked==false && !viewport.WorkingBoundaryPickMode && viewport.RectangleSelectionMode &&
            viewport.ConnectorEditMode && viewport.PowerLocationMode,"outstanding preview checkbox rejection does not disable other modes");
        viewport.RectangleSelectionMode=false; viewport.ConnectorEditMode=false; viewport.PowerLocationMode=false;
        Invoke(window,"OnWorkingBoundaryPicked",0,0);
        Check(ReferenceEquals(confirmedSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")),
            "rejected preview diagnostic click cannot discard confirmed sample");
        CheckPreviewRollback(window,front,false);
        var replaced=state.Preview;
        var originalBaseline=Field<object>(window,"_workingPreviewBaseline");
        Invoke(window,"PreviewWorkingSide",new Vec3(.2,0,-1));
        Check(!ReferenceEquals(replaced,state.Preview) && ReferenceEquals(originalBaseline,Field<object>(window,"_workingPreviewBaseline")) &&
            ReferenceEquals(confirmedSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")),
            "successful preview replacement retains the original confirmed savepoint and sample");
        Check(Bridge.QueryWorkingSurface(replaced.Id.Value,replaced.Revision.Value,0,0).Status==WorkingQueryStatus.StaleContext,
            "replaced preview context is released after replacement commits");
        Invoke(window,"WorkingCancel_Click",null,new RoutedEventArgs());
        var cancelledPresentation=Bridge.CaptureWorkingPresentation();
        Check(SamePresentation(confirmedPresentation,cancelledPresentation) &&
            ReferenceEquals(confirmedSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")) &&
            pickBox.IsChecked==true && viewport.WorkingBoundaryPickMode,"cancel restores confirmed native marker/boundary, sample and active pick mode");
        Check(Same(cancelScreen,Bridge.WorkingPointToScreen(front.Id,front.Revision,15,5,0)),"cancel restores the pre-preview camera, not the preview camera");
        Bridge.ReleaseWorkingPresentation(confirmedPresentation.Id); Bridge.ReleaseWorkingPresentation(cancelledPresentation.Id);
        Invoke(window,"WorkingFront_Click",null,new RoutedEventArgs());
        var returned=Bridge.CaptureWorkingPresentation();
        Check(returned.DiagnosticPoint!=null && ReferenceEquals(confirmedSample,Field<WorkingBoundarySample>(window,"_workingBoundarySample")),
            "return to confirmed front does not clear confirmed marker or sample");
        Bridge.ReleaseWorkingPresentation(returned.Id);
        Bridge.ZoomView(1.5);
        Bridge.PanStart(0,0); Bridge.PanMove(17,11); Bridge.PanEnd();
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,50,5,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0]+4,(int)screen[1]);
        Check(status.Text.StartsWith("외곽 진단 UV="),"screen aperture follows zoom/pan and snaps independently of cursor ray");
        Bridge.SetViewPerspective(true);
        Bridge.SetViewFrame(.3,.2,1,0,1,0);
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,50,0,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        Check(status.Text.StartsWith("외곽 진단 UV="),"perspective screen candidate and visibility ray use actual camera");
        Check(Query(front,new(0,0,0)).Point[2]==20,"perspective camera still cannot change confirmed orthographic side");
        Check(Bridge.ReturnToWorkingFront(state.Confirmed.Id.Value,state.Confirmed.Revision.Value),"return to confirmed orthographic front");
        Invoke(window,"OnWorkingBoundaryPicked",0,0);
        Check(status.Text.StartsWith("외곽 선택 실패"),"out-of-aperture diagnostic failure, no route generation");
        Invoke(window,"Generate_Click",null,new RoutedEventArgs());
        Check(status.Text.StartsWith("생성 보류"),"unsafe legacy pattern generation gated after confirmation");
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,-1));
        Check(state.Confirmed.Frame.Normal.Z==1 && state.Preview.Frame.Normal.Z==-1,"UI preview cannot mutate confirmed direction");
        Invoke(window,"WorkingCancel_Click",null,new RoutedEventArgs());
        Check(state.Confirmed.Frame.Normal.Z==1 && state.Preview==null,"UI cancel retains confirmed side");
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,50,0,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        pickBox.IsChecked=true;
        var oldConfirmed=state.Confirmed;
        Check(Field<WorkingBoundarySample>(window,"_workingBoundarySample")!=null,"confirmed diagnostic exists before replacement confirmation");
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,1));
        Invoke(window,"WorkingConfirm_Click",null,new RoutedEventArgs());
        var committedPresentation=Bridge.CaptureWorkingPresentation();
        Check(Field<WorkingBoundarySample>(window,"_workingBoundarySample")==null && committedPresentation.DiagnosticPoint==null &&
            !viewport.WorkingBoundaryPickMode && pickBox.IsChecked==false && Field<object>(window,"_workingPreviewBaseline")==null,
            "confirmation, not preview, clears old diagnostic and releases its restore savepoint");
        Bridge.ReleaseWorkingPresentation(committedPresentation.Id);
        Check(Bridge.QueryWorkingSurface(oldConfirmed.Id.Value,oldConfirmed.Revision.Value,0,0).Status==WorkingQueryStatus.StaleContext,
            "old confirmed context is released only after new confirmation");
        Bridge.ReleaseWorkingContext(state.Confirmed.Id.Value);
        pickBox.IsChecked=false; viewport.RectangleSelectionMode=true; pickBox.IsChecked=true;
        Check(pickBox.IsChecked==false && !viewport.WorkingBoundaryPickMode && viewport.RectangleSelectionMode,
            "released native context cannot activate boundary mode even with matching model revision");
        viewport.RectangleSelectionMode=false;

        Check(Bridge.LoadStep(Path.Combine(args[0],"ring.stp")),"load ring fixture");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"model reload invalidates native contexts");
        var ringContext=Bridge.CreateWorkingContext(0,0,1,1,0,0); var rings=Rings(ringContext);
        Check(rings.Count==2 && rings.Count(r=>r.IsHole)==1,"whole-model union retains hole across disconnected adjacent sheet faces");
        Check(Query(ringContext,new(50,50,0)).Status==WorkingQueryStatus.Miss,"through-hole is not working surface");
        Invoke(window,"ResetWorkingSide");
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,1));
        Invoke(window,"WorkingConfirm_Click",null,new RoutedEventArgs());
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,20,50,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        Check(status.Text.StartsWith("외곽 선택 실패"),"interior hole boundary is never a diagnostic endpoint");
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,10,20,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        Check(status.Text.StartsWith("외곽 선택 실패"),"shared sheet seam is never a diagnostic endpoint");
        Check(Bridge.LoadStep(Path.Combine(args[0],"sphere.stp")),"load sphere with curved silhouette, not trim-edge outline");
        var sphere=Bridge.CreateWorkingContext(0,0,1,1,0,0); var sphereRings=Rings(sphere);
        Check(sphereRings.Count==1 && sphereRings[0].Points.Count>20,"curved silhouette exists independently of face wire/seam");
        double area=Math.Abs(RegionOps.SignedArea(sphereRings[0].Points));
        Check(Math.Abs(area-Math.PI*400)<10,"sphere mesh silhouette approximates projected disk, explicit tolerance");
        var tangent=Query(sphere,new(20,0,0));
        Check(tangent.Status==WorkingQueryStatus.Tangent || tangent.Status==WorkingQueryStatus.Miss,"tangent is explicit or no hit, never back surface fallback");
        var sphereTop=Query(sphere,new(0,0,0));
        Check(sphereTop.Status==WorkingQueryStatus.Hit && Math.Abs(sphereTop.Point[2]-20)<1e-5,"curved surface first hit");
        Invoke(window,"ResetWorkingSide");
        Invoke(window,"PreviewWorkingSide",new Vec3(0,0,1));
        Invoke(window,"WorkingConfirm_Click",null,new RoutedEventArgs());
        screen=Bridge.WorkingPointToScreen(state.Confirmed.Id.Value,state.Confirmed.Revision.Value,20,0,0);
        Invoke(window,"OnWorkingBoundaryPicked",(int)screen[0],(int)screen[1]);
        Console.WriteLine(status.Text);
        Check(status.Text.StartsWith("외곽 진단 UV="),"curved silhouette candidate BRep-refines and is selectable without CAD trim edge");
        foreach (var n in new[]{new Vec3(1,0,0),new Vec3(-1,0,0),new Vec3(0,1,0),new Vec3(0,-1,0),new Vec3(0,0,1),new Vec3(0,0,-1)})
        {
            var axisX=Math.Abs(n.X)>.9 ? new Vec3(0,1,0) : new Vec3(1,0,0);
            var c=Bridge.CreateWorkingContext(n.X,n.Y,n.Z,axisX.X,axisX.Y,axisX.Z);
            var hit=Query(c,new(0,0,0));
            var p=new Vec3(hit.Point[0],hit.Point[1],hit.Point[2]);
            Check(hit.Status==WorkingQueryStatus.Hit && (p-n*20).Length<1e-5 && Vec3.Dot(Frame(c).Origin,n)>20,
                "all-axis outside datum and nearest surface: "+n);
            Bridge.ReleaseWorkingContext(c.Id);
        }
        Check(Bridge.LoadStep(Path.Combine(args[0],"tangentBack.stp")),"load tangent contact with rear surface behind it");
        var tangentContext=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        var grazing=Query(tangentContext,new(20,0,0));
        Check(grazing.Status==WorkingQueryStatus.Tangent || grazing.Status==WorkingQueryStatus.GeometryFailure,
            "nearest grazing contact is not discarded in favor of a rear surface hit");
        var stalePresentation=Bridge.CaptureWorkingPresentation();
        Check(Bridge.LoadStep(Path.Combine(args[0],"trimmedVisibility.stp")),"load holed upper trimmed face with full lower sheet");
        Check(!Bridge.RestoreWorkingPresentation(stalePresentation.Id),"presentation savepoints cannot restore across model revision");
        Bridge.ReleaseWorkingPresentation(stalePresentation.Id);
        var trimmed=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Bridge.ReturnToWorkingFront(trimmed.Id,trimmed.Revision);
        Check(Query(trimmed,new(0,0,0)).Point[2]==0 && Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,0,0,0,.001),
            "upper surface extension through trimmed hole does not occlude lower BRep hit");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,60,0,0,.001),
            "outside upper face trim is not a visibility blocker");
        Check(!Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,30,0,0,.001),
            "upper face IN hit still blocks the lower surface");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,10,0,20,.001),
            "ON trim-boundary hit remains eligible for visibility");
        Check(!Bridge.LoadStep(Path.Combine(args[0],"absent.stp")),"failed model load");
        Check(Bridge.QueryWorkingSurface(trimmed.Id,trimmed.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"failed reload also invalidates context");
        Check(Bridge.CreateWorkingContext(0,0,1,1,0,0)==null,"no context available without model");
        window.Close();
        Console.WriteLine($"{count} working-side checks passed.");
    }
}
