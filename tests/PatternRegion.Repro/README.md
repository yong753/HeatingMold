# Pattern region projection repro ??faces {18, 74, 597, 598}

Reproduces the field failure:

```
OCCT projected route-piece validation/staging failed at piece 87
```

Run from the repository root in PowerShell:

```powershell
& tests/PatternRegion.Repro/Run.ps1
```

`Run.ps1` builds the native bridge into `x64/Release` (the project references that DLL),
then runs the harness against `resource/Mold.stp`. The harness needs no visible window; the
staging check uses an offscreen `HwndSource`/`InitViewer`, like `WorkingSide.Regression`.

The harness exits with code 0 now that the bridge recovers the degenerate pieces (see
"Implemented fix" below). Pass `-Strict` to turn any remaining check failure into a throw.

## Scenario

Mirrors `MainWindow.BuildSelectedRegion` + `GenerateMultiFace` with defaults:

| item | value |
| --- | --- |
| faces | 18 (BSpline), 74 (BSpline), 597 (plane), 598 (cylinder, R = 4303 mm) |
| working side | default `+Z`, `AxisX = +X`, datum plane `z = -3606.937` |
| projection direction | `-Z` |
| pattern | margin 5, spacing 10, connectors constrained |

The selected boundary is one closed loop of 134 points whose **z spans 222.576 mm**: the four
faces form a step/draft, so the "flat" pattern is really a serpentine in the XY silhouette that
each piece must survive projecting down onto a non-planar surface.

`Serpentine.Generate` returns 66 lanes / 133 pieces / 39 992 mm with no exception, i.e. the
pattern itself is generated successfully. The failure is purely in the projection/staging
validation of individual pieces.

## What the harness reports

Failing pieces (all `TurnConnector`, all `Points.Count == 2`, i.e. the straight fallback when
`Serpentine.RouteAlongBoundary` returns nothing because lane ends are 5 mm off the ring ??`TryProject` requires ??1e-3):

```
87, 91, 95, 99, 103, 107, 111, 115, 119, 123, 127   (lanes 43,45,??63)
```

Legacy diagnostic status 4 for each: `BRepProj_Projection` produced **zero wires**. It is not a
coverage miss (51) and not a wire-connection/endpoint failure (5/6/7).

All eleven are 10 mm segments at 2D `x ??498.5 ??500.9` (`y = 223 ??453`), i.e. the right-hand
turns that run over face 598, with `dx = +0.116 mm`, `dy = 10 mm` ??**0.662째** off the Y axis.

## Root cause

Face 598 is **not** flat even though it looks flat: it is a `Cylinder` with **R = 4303 mm**, an
angular span of **1.70째** (128 mm wide patch, 305 mm along the axis). Over the 10 mm of a
connector it stays within **0.0029 mm** of its tangent plane ??below the 0.01 mm sewing
tolerance used by the bridge. So the model gives OCCT a surface that is numerically
indistinguishable from a plane but is still parameterised as a cylinder.

Measured at the face centre, the cylinder axis is `a = (-0.0095, -0.8231, 0.5678)`.

A 2-point segment is projected by OCCT by sweeping it along `-Z` and intersecting the resulting
plane with the target surface. That sweep plane contains `d` (the segment direction) and `-Z`, so
its normal is `n = normalize(d 횞 (-Z))`; for `d = (s, 1, 0)`:

```
a 쨌 n = (0.0095 - 0.8231 쨌 s) / sqrt(1 + s짼)
```

which is zero ??the sweep plane parallel to the cylinder axis ??at

```
s = a_x / |a_y| = 0.0095 / 0.8231 = 0.01154
```

The failing connectors have `dx = 0.115565918` (= `a.X` of the app segment) per `dy = 10`, i.e.
`s = 0.0115566`: **within 0.15 % of the degenerate slope** (`a 쨌 n = -1.2e-5 rad = 0.0007째`).
`BRepProj_Projection` returns zero wires there.

Why that particular slope appears procedurally: the right-hand boundary of this region is the trim
edge of face 598, i.e. one of the cylinder's **rulings**, and the XY projection of a ruling has
exactly `dx/dy = 0.0095 / 0.8231 = 0.01154`. The lane ends follow that boundary, so every
right-hand turn connector is parallel to the projected ruling by construction ??all eleven land on
the degenerate slope simultaneously. The mirrored left-hand turns follow the boundary of other
faces, hence they pass despite the same 0.116 mm drift.

Evidence (all from the harness):

| variation of the failing segment | `a 쨌 n` | result |
| --- | --- | --- |
| as generated (`dx = 0.115566`, `dy = 10`) | -1.2e-5 (0.0007째) | `status 4`, 0 wires |
| same slope at `x0 = 400 ??500` (10 positions) | same | all fail ??not a trim/location effect |
| same slope at length 1, 2, 5, 10, 20, 40 mm | same | all fail ??not a length effect |
| same slope, reversed | same | fails |
| `dx = 0` (exactly Y-parallel) | 9.5e-3 (0.54째) | `ok`, 1 wire, len 12.15 |
| `dx = 0.100` | 1.3e-3 (0.073째) | `ok` |
| `dx = 0.120` (+0.005 mm) | 3.8e-4 (0.022째) | `ok`, 1 wire, len 12.23 |
| split into 2/3/5 sub-edges | same | still fails (no edge-crossing involved) |
| vertical + horizontal + vertical staircase | ??| `ok`, 4 wires |
| projection direction `(-0.01,-0.01,-1)` (??째 tilt) | ??| `ok`, 1 wire, len 12.07 |
| target `{598}` only | same | still fails (598 alone reproduces it) |
| targets `{18}`, `{74}`, `{597}` only | ??| fail because the segment lies outside those trims (their 2D footprint ends near `x = 376`; the connector is at `x ??498`), so they are irrelevant here |

Every point along the failing segment ray-hits face 598 with the same normal, and the incidence to
`-Z` is 34.6째, so the surface is well conditioned for projection; the coverage samples inside
`BuildProjectedWorkingWire` pass and only `wires.empty()` aborts the piece.

### Where inside OCCT it dies (measured, not inferred)

`BRepProj_Projection` builds its result from `BRepAlgoAPI_Section`. A standalone native probe
(`secprobe`, OCCT 7.9.3, same STEP file, same alignment, same 4 sewn faces) replays that pipeline
step by step for the exact app segment
`(1751.1998456735284, -1512.3210851853976, -3606.93653298544) -> (1751.31541159172, -1502.3210851853976, -3606.93653298544)`,
direction `(0,0,-1)`:

| step | result |
| --- | --- |
| `DistanceIn` / sweep extent | `mdis = 1346.84`, prism `z` from `-2260.09` to `-4953.78` |
| prism face count | 1 face |
| prism vs. target bbox distance | **0.000** (the sweep does reach all four faces) |
| `BRepAlgoAPI_Section::IsDone()` | **1** |
| `BRepAlgoAPI_Section::HasErrors()` | **0** |
| section edges | **0** |
| `ConnectEdgesToWires(0.01, shared=false)` (bridge call) | 0 wires |

So the failure is: `plane (sweep) ??cylinder R = 4303` **returns "done" with no intersection curve**
when the sweep plane is parallel to the cylinder axis to within ~1e-5 rad. BRepProj therefore
reports `IsDone()` with no wire, and the bridge's `wires.empty()` check rejects the piece. Nothing
else in the chain fails ??not the sewn target, not the prism extent, not the endpoint tolerances.

The zero-intersection band is **not infinitely narrow**, and it is not analytic either: sweeping
`dx` in 0.00005 mm steps from 0.11300 to 0.11940 gives

```
fail  dx 0.11370 .. 0.11570     (s 0.011370 .. 0.011570)
pass  dx 0.11575 .. 0.11595     <- 0.0002 wide island inside the band
fail  dx 0.11600 .. 0.11800     (s 0.011600 .. 0.011800)
pass  dx >= 0.11805
```

### The exact decision (code level, confirmed)

The chain for this piece is `BRepAlgoAPI_Section` -> `BOPAlgo_SECTION` -> `BOPAlgo_PaveFiller`
face/face step -> `IntTools_FaceFace` -> `IntPatch_Intersection` -> `IntPatch_ImpImpIntersection`.
Because the sweep of a straight 2-point segment along `-Z` is a **plane** and face 598 is a
**cylinder**, the case lands in `IntPatch_ImpImpIntersection::Perform`, `case 12/21 (Plane/Cylinder)`:

```cpp
const Standard_Real Tolang = 1.e-8;                    // hard-coded, IntPatch_ImpImpIntersection.cxx:2533
H = aSCyl->LastVParameter() - aSCyl->FirstVParameter(); // IntPatch_ImpImpIntersection.cxx:2564 -> 305.1685
IntPCy(quad1, quad2, Tolang, TolTang, bReverse, empt, slin, H);
```

`TolTang` comes from `IntTools_FaceFace::Perform`: `TolArc = TolTang = tol(face1) + tol(face2) + fuzzy`
(`IntTools_FaceFace.cxx:380-385`), i.e. ~1e-7 mm for this model. `IntPCy` then calls
`IntAna_QuadQuadGeo::Perform(Pl, Cy, Tolang, TolTang, H)`, which builds
`IntAna_IntConicQuad(axec, P, tolang, Tol, H)`. The decision that drops the curve is at the end of
`IntAna_IntConicQuad::Perform(gp_Lin, gp_Pln, Tolang, Tol, Len)` (`IntAna_IntConicQuad.cxx`):

```cpp
Direc  = A*Al + B*Bl + C*Cl;     // = a.n       = 2.4957e-05 here
parallel = Standard_False;
if (Abs(Direc) < Tolang) {                        // tolang = 1e-8  -> 2.4957e-05 > 1e-8: already false
  parallel = Standard_True;
  if (Len != 0 && Direc != 0) {
    aP2 = aP1 + Len * axisDir;                    // aP1 = axis origin projected on the plane
    if (P.Distance(aP2) > Tol) parallel = Standard_False;   // Len*sin(dangle) vs Tol
  }
}
```

Measured values for this piece: `|a.n| = 2.4957e-05 rad`, `Len = 305.1685 mm`, so the axis drifts
`305.1685 * 2.4957e-05 = 0.007616 mm` off the plane across the cylinder **while the tolerance in force
is ~1e-7 mm**. The parallel test therefore fails and `IntAna_QuadQuadGeo` takes the **non-parallel**
branch: `param1 = radius / |a.n| = 4303 / 2.4957e-05 = 1.72e8 mm` ??a "line" that OCCT represents as
an ellipse with a 172,000 km semi-axis. That curve cannot be trimmed to a 130 x 250 mm face patch and
no section edge is produced.

The gate is therefore a single linear comparison, and it needs `Tol >= Len*sin(dangle)`. Confirmed
numerically on `BRepAlgoAPI_Section` with the same inputs (fuzzy sweep):

```
fuzzy <= 0.0076   -> 0 edges
fuzzy >= 0.00762  -> 1 edge      (0.00762 = 305.1685 * 2.4957e-05)
```

So the tolerance OCCT would need here is 0.0076 mm, about 76,000x `Precision::Confusion()`, purely
because the app's connector direction happens to sit 2.5e-05 rad off the cylinder axis (0.030 mm per
1200 mm).

i.e. everywhere within `|a.n| <~ 2.1e-4 rad (0.012 deg)` OCCT may either return a 12.149 mm curve
or return nothing, and the app's generated slope sits inside that band by construction. The island
is the signature of a tolerance-scale decision inside the section algorithm, not of a geometric
condition at one exact angle.

Two contributing factors on the generator side, worth knowing before choosing a fix:

1. `RouteAlongBoundary` can only route along the ring when both ends lie *on* the ring
   (??1e-3), so every turn connector in practice becomes a 2-point straight segment.
2. One global `-Z` projection is used for a region that is 222 mm out of plane and contains a
   4.3 m radius band; any turn connector that follows a ruling edge of that band is degenerate by
   construction.

## Implemented fix

`src/OccBridge/WorkingSideNative.inc`:

1. `BuildProjectedWorkingWire` no longer aborts when the *per-edge* `BRepProj_Projection` returns
   no wire. The source-segment ray coverage check that had been added for that step is disabled
   in the current code (it was a `const false` switch and has been removed), so an out-of-domain
   piece is caught by the whole-polyline `BRepProj_Projection`, wire-connection and endpoint
   checks below instead. Only an empty source polyline is rejected up front.
2. If the whole-polyline `BRepProj_Projection` also yields zero wires, the new
   `ProjectWorkingWireBySampling` helper ray-casts sample points of the source polyline along the
   **original projection direction** and rebuilds the wire as a polyline through those surface
   hits. Lines use their two endpoints; other curve types are sampled with
   `GCPnts_QuasiUniformDeflection` at 1 µm.
3. Because the direction is unchanged, the recovered wire stays on the intended datum projection:
   for piece 87 both hits lie on the same cylinder ruling, so the fallback chord *is* the exact
   projected ruling (length 12.15 mm, matching the non-degenerate Y-parallel case). No ~0.1 mm
   displacement, and the endpoints still line up with the neighbouring pieces within the existing
   0.01 mm checks.

The app now stages all 133 pieces (harness Phase 2). Phase 1 and Phase 3 still show raw
`BRepProj_Projection` behavior: the OCCT section still returns zero intersection curves for these
connectors, which is why the fallback exists.

## Considered alternatives (not implemented)

- Retry a failed piece with a perturbed projection direction (verified to succeed, but it
  displaces the curve off the datum by ~0.1 mm over 12 mm and must be recorded/toleranced).
- Or detect the near-degenerate case (`sweep plane ??parallel to the surface's ruling`) and
  report a domain message instead of a piece index.
- Or stop projecting a single global silhouette and fit a local projection per piece/face.
- Or, since the patch is flat to 3 쨉m over a connector, replace the 4303 mm cylinder with a
  plane for projection purposes (and keep the exact surface only for the final geometry).

## Files

- `Program.cs` - characterization + goal checks; prints the failure table.
- `Run.ps1` - native build + PATH/CSF setup + run.
- `PatternRegion.Repro.csproj` - references `x64/Release/OccBridge.dll`, project-references
  `HeatingMold.Core`.
- `native/SectionProbe.cpp`, `native/build.bat` - standalone OCCT probe that replays
  `BRepProj_Projection` for the exact piece-87 segment (plus a fine `dx` sweep) without the bridge,
  showing the failure is inside `BRepAlgoAPI_Section`. Build with `native\build.bat`, then run
  `SectionProbe.exe resource\Mold.stp` with `third_party\opencascade-7.9.3-vc14-64\win64\vc14\bin`
  and the `3rdparty-vc14-64` DLL folders on `PATH` and `CSF_OCCTResourcePath` pointing at the OCCT
  `src` folder.
