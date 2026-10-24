using System;
using System.IO;
using HeatingMold.OccBridge;
using HeatingMold.App;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Legacy;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;

static class Program
{
    static int count;
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        count++; Console.WriteLine("PASS " + label);
    }
    static bool Near(double a, double b) => Math.Abs(a-b) < .01;
    static double[] Lead(double[] a, double x, double depth = 0, int id = 0) =>
        Bridge.ProjectConnectionPath(id, new[]{a[0],a[1],100.0,x,a[1],100.0}, a, new[]{0},0,0,-1,depth);
    [STAThread]
    static void Main(string[] args)
    {
        Check(Bridge.LoadStep(Path.Combine(args[0],"slope.stp")), "load slope");
        var piece = Bridge.ProjectHeatingPathSegment(0,"HeatingRun",new[]{20.0,30,100,80,30,100},new[]{0},0,0,-1,0);
        Console.WriteLine(piece == null ? "Heating path segment: null" : $"Heating path segment: {string.Join(",",piece.Start)} -> {string.Join(",",piece.End)}, length {piece.Length}");
        Check(piece != null && Near(piece.Start[0],20) && Near(piece.Start[2],10) && Near(piece.End[2],40), "actual projected endpoints in source order");
        Check(Near(piece.Length,Math.Sqrt(60*60+30*30)), "actual 3D heating length");
        var lead = Lead(piece.Start,150);
        Check(lead[0]==1, "project surface and continue outside");
        var preview = Bridge.ProjectConnectionPath(0,new[]{20.0,30,100,150,30,100},piece.Start,new[]{0},0,0,-1,0,true);
        Check(preview[0]==1 && Near(preview[1],lead[1]) && Near(preview[2],lead[2]) && Near(preview[3],lead[3]),
            "preview reuses exact committed surface/external projection and lengths");
        Check(Bridge.ProjectConnectionPath(0,new[]{20.0,30,100,70,30,100},piece.Start,new[]{0},0,0,-1,0,true)[0]==-2,
            "invalid preview rejects inside destination with the committed validator");
        Bridge.ClearConnectionPathPreview(); Bridge.ClearConnectionPathPreview();
        Check(Lead(piece.Start,150)[0]==1,"preview clear is idempotent and leaves projection usable");
        Check(Near(lead[2],Math.Sqrt(80*80+40*40)) && Near(lead[3],Math.Sqrt(50*50+50*50)) && Near(lead[1],lead[2]+lead[3]), "actual surface/external/total lead lengths");
        var reverse = Bridge.ProjectHeatingPathSegment(1,"HeatingRun",new[]{80.0,30,100,20,30,100},new[]{0},0,0,-1,3);
        Check(reverse != null && Near(reverse.Start[0],80) && Near(reverse.Start[2],37) && Near(reverse.End[2],7), "reversed source endpoints and insertion offset");
        Check(Lead(reverse.End,150,3)[0]==1, "lead shares inserted 3D endpoint");
        Check(Lead(piece.Start,70)[0]==-2, "reject destination inside mold footprint");
        Check(Lead(new[]{20.0,30,9.0},150)[0]==-4, "reject wrong 3D start rather than fabricate turnSegment");
        Check(Lead(piece.Start,20)[0]<0, "reject zero-length source");
        Check(Lead(piece.Start,150,0,0)[0]==1 && Lead(piece.Start,160,0,0)[0]==1, "stable lead ID replacement");
        Bridge.ClearConnectionPaths(); Bridge.ClearConnectionPaths();
        Check(Lead(piece.Start,150)[0]==1, "restart after clear");
        var outward = Lead(new[]{100.0,30,50},150);
        Check(outward[0]==1 && outward[2]==0 && Near(outward[1],Math.Sqrt(50*50+50*50)), "margin-zero outward boundary lead has zero surface length");
        var insertedOutward = Lead(new[]{100.0,30,47},150,3);
        Check(insertedOutward[0]==1 && insertedOutward[2]==0 && Near(insertedOutward[1],outward[1]), "outward boundary lead preserves insertion offset");
        var inward = Lead(new[]{100.0,30,50},-50);
        Check(inward[0]==1 && Near(inward[2],Math.Sqrt(100*100+50*50)), "boundary inward lead still projects the complete surface portion");
        Check(Lead(new[]{100.0,30,49},150)[0]<0, "zero-surface path cannot fabricate a mismatched boundary start");
        Check(Lead(new[]{100.002,30,50.002},150)[0]<0, "precise CAD does not receive a blanket 0.01 mm boundary allowance");
        Check(Bridge.ProjectConnectionPath(0,new[]{100.0,60,100,150,90,100},new[]{100.0,60,50},new[]{0},0,0,-1,0)[0]==1,
            "outward corner endpoint permits direct external continuation");
        Check(Bridge.LoadStep(Path.Combine(args[0],"adjacent.stp")), "load adjacent heating patches");
        var adjacent = Bridge.ProjectConnectionPath(0,new[]{20.0,30,100,150,30,100},new[]{20.0,30,10},new[]{0,1},0,0,-1,0);
        Check(adjacent[0]==1 && Near(adjacent[2],Math.Sqrt(80*80+40*40)), "continuous projection across sewn selected-face seam");
        Check(Bridge.ProjectConnectionPath(0,new[]{20.0,30,100,70,30,100},new[]{20.0,30,10},new[]{0},0,0,-1,0)[0]==-2,
            "outside selected patch but inside unselected mold face is rejected");
        Check(Bridge.ProjectConnectionPath(0,new[]{20.0,30,100,150,30,100},new[]{20.0,30,10},new[]{0},0,0,-1,0)[0]==-5,
            "external segment cannot cross adjacent unselected material despite outside destination");
        Check(Lead(new[]{50.0,30,25},150)[0]<0, "selected boundary at internal seam cannot bypass unselected mold");
        Check(Bridge.LoadStep(Path.Combine(args[0],"concave.stp")), "load concave whole-mold footprint");
        Check(Lead(new[]{20.0,30,10},150)[0]==-5, "external segment cannot re-enter unselected arm across concavity");
        Check(Lead(new[]{50.0,30,25},150)[0]<0, "zero-surface boundary path cannot bypass concave re-entry");
        Check(Bridge.LoadStep(Path.Combine(args[0],"narrow.stp")), "load narrow footprint obstruction");
        Check(Lead(new[]{20.0,30,10},150)[0]==-5, "exact full-segment projection catches narrow obstruction missed by coarse point tests");
        Check(Bridge.LoadStep(Path.Combine(args[0],"wall.stp")), "load axis-parallel material obstruction");
        Check(Lead(new[]{20.0,30,10},150)[0]==-5, "external segment cannot cross unselected vertical material");
        Check(Bridge.LoadStep(Path.Combine(args[0],"noisy.stp")), "load STEP with noisy vertex/edge boundary agreement");
        var noisy = Bridge.ProjectConnectionPath(0,new[]{100.0,30,100,150,30,100},new[]{100.002,30,50.002},new[]{0},0,0,-1,0);
        Check(noisy[0]==1 && noisy[2]==0, "CAD edge/vertex tolerances accept noisy outward boundary and datum alignment");
        Check(Lead(new[]{100.02,30,50.02},150)[0]<0, "boundary recognition does not exceed the 0.01 mm projection budget");
        var noisyInward = Lead(new[]{100.002,30,50.002},-50);
        Check(noisyInward[0]==1 && noisyInward[2]>100, "noisy inward boundary still requires full surface projection");
        Check(Bridge.LoadStep(Path.Combine(args[0],"noisyBlocked.stp")), "load noisy boundary with nearby narrow obstruction");
        Check(Lead(new[]{100.002,30,50.002},150)[0]<0, "tolerant boundary recognition cannot skip a nearby unselected obstruction");
        Check(Bridge.LoadStep(Path.Combine(args[0],"hole.stp")), "load holed face");
        Check(Lead(new[]{20.0,30,0.0},150)[0]==-3, "reject surface gap/re-entry through hole");
        Check(Bridge.LoadStep(Path.Combine(args[0],"curve.stp")), "load cylinder patch");
        double z = -100+Math.Sqrt(10000-400);
        var curved = Lead(new[]{30.0,30,z},150);
        Check(curved[0]==1, "exact curved surface lead to boundary");
        Check(Near(curved[2],100*(.5+Math.Asin(.2))), "curved lead uses arc length, not planar/chord length");
        var curveBoundary = new[]{50+100*Math.Sin(.5),30.0,-100+100*Math.Cos(.5)};
        var curveOutward = Lead(curveBoundary,150);
        Check(curveOutward[0]==1 && curveOutward[2]==0, "curved-face boundary supports zero-surface outward lead");
        var curveInward = Lead(curveBoundary,-50);
        Check(curveInward[0]==1 && Near(curveInward[2],100), "curved-face boundary inward lead retains exact surface arc");
        Check(Bridge.ProjectConnectionPath(0,new[]{double.NaN,0,0,1,0,0},new[]{0.0,0,0},new[]{0},0,0,-1,0)[0]<0, "reject nonfinite input");
        CheckUiState();
        CheckPreviewScheduling();
        Console.WriteLine($"{count} native projection regression checks passed.");
    }

    static void CheckUiState()
    {
        var window = new MainWindow(); // Deliberately never shown: no GL context needed.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => typeof(MainWindow).GetField(name,flags).GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name,flags).SetValue(window,value);
        void Invoke(string name) => typeof(MainWindow).GetMethod(name,flags).Invoke(window,new object[]{null,null});
        var pattern = HeatingPathGenerator.Generate(new List<Vec2>{new(0,0),new(100,0),new(100,60),new(0,60)},null,
            new PatternParameters { BoundaryMarginMm = 5, CenterPitchMm = 10 }, requireContinuousPath: true);
        Set("_pattern",pattern);
        Set("_multiFaceFrame",new ProjectionFrame(new Vec3(0,0,100),new Vec3(0,0,1),new Vec3(1,0,0)));
        var turns = (List<bool>)Field("_turnSegmentEnabled");
        var projected = (List<HeatingPathSegmentResult>)Field("_projectedPathSegments");
        var lengths = new double[pattern.PathSegments.Count];
        double expected = 0;
        for(int i=0;i<pattern.PathSegments.Count;i++)
        {
            var p = pattern.PathSegments[i]; lengths[i]=p.Length*1.5; expected+=lengths[i];
            var a=p.Points[0]; var b=p.Points[p.Points.Count-1];
            projected.Add(new HeatingPathSegmentResult{Length=lengths[i],Start=new[]{a.X,a.Y,10.0},End=new[]{b.X,b.Y,20.0}});
            if(p.Kind==HeatingPathSegmentKind.TurnSegment) turns.Add(true);
        }
        Set("_pathSegmentLengths",lengths);
        object mainWindowViewModel=Field("_mainWindowViewModel");
        object regionSelection=mainWindowViewModel.GetType().GetProperty("RegionSelection").GetValue(mainWindowViewModel);
        regionSelection.GetType().GetMethod("Add").Invoke(regionSelection,new object[]{FaceRefId.New()});
        typeof(MainWindow).GetMethod("ApplyPathSegmentStates",flags).Invoke(window,null);
        var grid=(DataGrid)window.FindName("CircuitLengthGrid");
        double SumColumn(string name)
        {
            double sum=0;
            foreach(var row in (IEnumerable)grid.ItemsSource) sum+=(double)row.GetType().GetProperty(name).GetValue(row);
            return sum;
        }
        Check(Near(SumColumn("HeatingLength"),expected),"UI table uses native lengths rather than planar lengths");
        turns[0]=false;
        typeof(MainWindow).GetMethod("ApplyPathSegmentStates",flags).Invoke(window,null);
        int firstTurn=0;
        while(pattern.PathSegments[firstTurn].Kind!=HeatingPathSegmentKind.TurnSegment) firstTurn++;
        Check(grid.Items.Count==2 && Near(SumColumn("HeatingLength"),expected-lengths[firstTurn]),"split circuit table excludes disabled turnSegment native length");
        Invoke("ConnectionPointSetup_Click");
        var pending=(IList)Field("_pendingConnectionPoints");
        var viewport=(OccViewport)window.FindName("Viewport");
        var status=(TextBlock)window.FindName("StatusText");
        Check(pending.Count==0 && !viewport.ConnectionPointSelectionMode && status.Text=="유효한 작업 방향이 없습니다",
            "power setup is gated before working-side confirmation despite restored legacy topology");
        // The old automatic endpoint-queue entry is intentionally unavailable at this checkpoint.
        // A future first-hit-safe route migration must add its own entry/endpoint tests before enabling it.
        var session=(WorkingCoordinateSystemSession)Field("_workingCoordinateSystem");
        var revision=new ModelRevision(Bridge.GetModelRevision());
        session.SetPreview(new WorkingCoordinateSystem(new WorkingCoordinateSystemId(-1),revision,(ProjectionFrame)Field("_multiFaceFrame")));
        session.Confirm(revision);
        viewport.RectangleSelectionMode=true; viewport.CircuitSplitMode=true;
        Invoke("ConnectionPointSetup_Click");
        Check(pending.Count==0 && !viewport.ConnectionPointSelectionMode && viewport.RectangleSelectionMode && viewport.CircuitSplitMode &&
            status.Text.StartsWith("생성 보류: 전원 경로"),"confirmed side alone cannot enter incompatible legacy power flow or disable other modes");
        viewport.RectangleSelectionMode=false; viewport.CircuitSplitMode=false;

        // Seed restored pending state, rather than bypassing/re-enabling the gate. Keep cancellation,
        // actual-3D endpoint and late-hover regressions meaningful for lifecycle cleanup of old state.
        void SeedPending()
        {
            pending.Clear();
            var type=typeof(MainWindow).GetNestedType("PendingConnectionPoint",BindingFlags.NonPublic);
            var groups=(IEnumerable)typeof(MainWindow).GetMethod("CircuitPieceGroups",flags).Invoke(window,null);
            int circuit=0;
            foreach(IList group in groups)
            {
                var a=projected[(int)group[0]].Start;
                var b=projected[(int)group[group.Count-1]].End;
                pending.Add(Activator.CreateInstance(type,new object[]{circuit,true,new Vec3(a[0],a[1],a[2])}));
                pending.Add(Activator.CreateInstance(type,new object[]{circuit,false,new Vec3(b[0],b[1],b[2])}));
                circuit++;
            }
            viewport.ConnectionPointSelectionMode=true;
        }
        SeedPending();
        object start=pending[0].GetType().GetProperty("Start").GetValue(pending[0]);
        Check(((Vec3)start).Z==10,"restored pending power start retains projected DTO endpoint height, not datum plane");
        var hoverMethod = typeof(MainWindow).GetMethod("OnConnectionPointMoved",flags);
        hoverMethod.Invoke(window,new object[]{12,34}); // No viewer: invalid screen-plane hit.
        Check(pending.Count==4 && Near(SumColumn("ConnectionLength"),0),"late hover cannot enter legacy projection or consume restored pending endpoints while gated");
        var zoomMethod = typeof(OccViewport).GetMethod("WheelZoomAmount",BindingFlags.Static | BindingFlags.NonPublic);
        int Zoom(int delta) => (int)zoomMethod.Invoke(null,new object[]{delta});
        Check(Zoom(120)==-12 && Zoom(-120)==12 && Zoom(240)==-24 && Zoom(-240)==24,
            "wheel zoom direction inverted for both directions and multiple notches");
        Check(Zoom(60)==-12 && Zoom(-60)==12 && Zoom(0)==12,"wheel preserves baseline fractional-notch and zero-delta quantization, only reversing sign");
        var dipToPixelsMethod = typeof(OccViewport).GetMethod("DipToDevicePixels", BindingFlags.Static | BindingFlags.NonPublic);
        int Pixels(double dip, double dpiScale) => (int)dipToPixelsMethod.Invoke(null, new object[] { dip, dpiScale });
        Check(Pixels(4, 1.0) == 4 && Pixels(4, 1.25) == 5 && Pixels(4, 1.5) == 6 && Pixels(4, 2.0) == 8,
            "viewer drag thresholds scale from WPF logical units to native pixels at common DPI settings");
        Check(Pixels(1.1, 1.25) == 2,
            "viewer size conversion rounds fractional device pixels upward to avoid clipping the native child window");
        var thresholdMethod = typeof(OccViewport).GetMethod("GetDevicePixelThreshold", BindingFlags.Static | BindingFlags.NonPublic);
        int anisotropicThreshold = (int)thresholdMethod.Invoke(null, new object[] { 4.0, 1.25, 1.5 });
        Check(anisotropicThreshold == 6,
            "drag threshold uses the larger device scale when horizontal and vertical DPI differ");
        var restoredLengths=new[]{123.0,456.0};
        Set("_connectionLengths",restoredLengths);
        Invoke("ConnectionPointSetup_Click");
        Check(pending.Count==4 && ReferenceEquals(restoredLengths,Field("_connectionLengths")) && status.Text.StartsWith("생성 보류: 전원 경로"),
            "gated setup cannot silently restart legacy projection, replace restored endpoints or rewrite lead lengths");
        Invoke("ConnectionPointCancel_Click");
        Check(((IList)Field("_pendingConnectionPoints")).Count==0 && !viewport.ConnectionPointSelectionMode,"explicit cancellation clears pending setup");
        SeedPending();
        typeof(MainWindow).GetMethod("ApplyPathSegmentStates",flags).Invoke(window,null);
        Check(((IList)Field("_pendingConnectionPoints")).Count==0 && !viewport.ConnectionPointSelectionMode,"topology update cancels pending power setup");
        SeedPending();
        Invoke("ClearExclusions_Click");
        Check(Field("_pattern")==null && ((IList)Field("_pendingConnectionPoints")).Count==0 && !viewport.ConnectionPointSelectionMode,"exclusion changes invalidate generated topology and pending leads");
        window.Close();
    }

    static void CheckPreviewScheduling()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewport = new OccViewport();
        typeof(OccViewport).GetField("_ready",flags).SetValue(viewport,true);
        viewport.ConnectionPointSelectionMode = true;
        int calls = 0, lastX = -1, lastY = -1;
        viewport.ConnectionPointMoved += (x,y) => { calls++; lastX=x; lastY=y; };
        void Queue(int x,int y) => typeof(OccViewport).GetMethod("QueueConnectionPointPreview",flags).Invoke(viewport,new object[]{x,y});
        void Flush() => typeof(OccViewport).GetMethod("FlushConnectionPointPreview",flags).Invoke(viewport,new object[]{null,EventArgs.Empty});
        void Message(int message) => typeof(OccViewport).GetMethod("WndProc",flags).Invoke(viewport,
            new object[]{IntPtr.Zero,message,IntPtr.Zero,IntPtr.Zero,false});
        DispatcherTimer Timer() => (DispatcherTimer)typeof(OccViewport).GetField("_connectionPointPreviewTimer",flags).GetValue(viewport);

        for(int i=0;i<100;i++) Queue(i,i+1);
        Check(calls==0 && Timer().IsEnabled && Timer().Interval==TimeSpan.FromMilliseconds(100),
            "mouse burst schedules one delayed 100ms preview without synchronous projection");
        Flush();
        Check(calls==1 && lastX==99 && lastY==100 && !Timer().IsEnabled,"preview tick projects only the latest cursor");
        Queue(99,100); Flush();
        Check(calls==1 && !Timer().IsEnabled,"same cursor does not reproject");
        Queue(50,60); Queue(99,100); Flush();
        Check(calls==1,"coalescing back to already projected pixel avoids redundant work");

        void CheckCancelled(Action cancel,string label)
        {
            Queue(20,30);
            int before=calls;
            cancel(); Flush(); // Even a stale queued timer tick cannot recreate the preview.
            Check(calls==before && !Timer().IsEnabled,label);
        }
        CheckCancelled(()=>Message(0x02A3),"mouse leave cancels pending preview");
        CheckCancelled(()=>Message(0x020A),"wheel navigation cancels pending preview");
        CheckCancelled(()=>Message(0x0204),"pan navigation cancels pending preview");
        Message(0x0205);
        CheckCancelled(()=>Message(0x0215),"capture loss cancels pending preview");
        CheckCancelled(()=>viewport.ConnectionPointSelectionMode=false,"mode exit cancels pending preview");
        viewport.ConnectionPointSelectionMode=true;
        CheckCancelled(viewport.CancelConnectionPathPreview,"explicit cancellation/reset cancels pending preview");
        int picked=0;
        viewport.ConnectionPointPicked += (x,y) => picked++;
        Message(0x0201); // Mouse down cancels hover before click/drag.
        Queue(40,50);
        CheckCancelled(()=>Message(0x0202),"commit cancels pending preview before delivering destination");
        Check(picked==1,"commit destination still delivered once");
        viewport.ConnectionPointSelectionMode=false;
    }
}
