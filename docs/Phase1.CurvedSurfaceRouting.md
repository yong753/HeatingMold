# Phase 1 — Curved-surface route mapping

Status: implemented on branch `feature/phase1-curved-surface-routing`, commit `f4a5444`.

## Purpose

Before Phase 1, a route piece was a 2D serpentine polyline lifted to the working datum
plane and projected onto the selected faces with `BRepProj_Projection`. On a curved
selection the planar segment can leave the face's projected coverage, so the projection
misses, splits, or produces a chain whose endpoints no longer meet the next piece.

Phase 1 removes that two-step lift/project. Every piece is routed by a single mechanism
that works for planar and curved faces alike:

1. The 2D pattern remains the source of truth (`(u, v)` datum points).
2. Each datum point is ray-cast **against the selected faces only**:
   origin = `context.origin + u·x + v·y`, direction = `-context.n`.
   The nearest `IN`/`ON` intersection wins and gives the true 3D surface point and face id.
3. The resulting 3D polyline is staged directly. It is never re-projected.

Because the ray is built from the datum frame only, the same 2D point always yields the
same 3D point. Adjacent pieces therefore share endpoints exactly (both use the identical
ray), which is what the 0.01 mm connection check verifies.

## Native and managed surface

| Layer | Addition |
| --- | --- |
| `WorkingSideNative.inc` | `MapWorkingPointOntoFacesNative` — filtered ray-cast per datum point |
| `WorkingSideNative.inc` | `SnapWorkingPointToFacesNative` — `BRepExtrema` fallback (≤ tolerance) |
| `WorkingSideNative.inc` | `StageSurfaceMappedRoutePieceNative` — validates every point (≤ 0.05 mm from the selected faces), stages the 3D polygon |
| `WorkingSideNative.inc` | `FaceRegionIsClosedNative`, `GetFaceAxisNative` — region gates |
| `OccBridgeApi` | `Bridge.MapWorkingPointOntoFaces`, `SnapWorkingPointToFaces`, `StageSurfaceMappedRoutePiece`, `FaceRegionIsClosed`, `GetFaceAxis` |
| `SurfacePathMapper` | `TryMapRoute` — adaptive chord refinement for routes (see deviations) |
| `MainWindow.Areas.cs` | curved branch of `RebuildNativePatternGeometry`, `StageCurvedRoutePiece`, `BuildCurvedMapDelegate`, `ResolveExitAnchor3D` |
| `MainWindow.ExitFace.cs` | `ExitChain3D` retained beside the 2D `ExitChain` |
| `MainWindow.xaml.cs` | gate messages in `TryBuildSelectedRegion` |

Selected-face ray-cast targets are cached per working context and face set. A
`TopoDS_Compound` of the original `g_faces` is used instead of sewing so face TShapes and
therefore face ids are preserved exactly.

## Accepted Phase 1 approximation errors

1. **2D spacing is not 3D surface spacing.** The serpentine is generated in the datum
   plane; on a tilted or curved patch the 3D length differs from the projected length.
   In the user case the worst HeatingRun 3D/2D chord ratio is 1.215 (total staged length
   1219.9 mm vs 1019.9 mm in 2D). Edge-to-edge 3D spacing measured at lane midpoints stays
   within 0.01 mm of the 10 mm request in that case, but that is a property of this patch,
   not a guarantee.
2. **Straight chords on a curved surface.** Samples are refined until the quarter/mid/
   three-quarter chord error is ≤ 0.05 mm (`SurfacePathMapper.TryMapRoute`).
3. **Silhouette front/back collapse.** Interior datum points always map to the visible
   front face along the ray. Only the extraction anchor uses the real 3D shared edge.
4. **Extraction lead chord sag.** The lead still follows the 2D anchor-to-path chord; on
   the R≈4303 cylinder the sag is negligible.

## Gates (user-facing)

- Closed surface (boundary made only of seams/degenerate edges) →
  `경계가 없는 닫힌 곡면은 Phase 1에서 미지원입니다 — 경계가 있는 열린 곡면 패치를 선택하세요`
- All selected faces are cylinders/cones whose axes are parallel to the working normal and
  the region splits into two loops →
  `이 방향에서는 영역을 구성할 수 없습니다 — 원통/원뿔 축이 작업 방향과 평행합니다. 작업 방향을 변경하세요`
- Piece mapping/staging failure → message plus `Face id=type` list for the area, followed
  by the native stage diagnostic when staging fails.

## Verification (real `resource/Mold.stp`)

Harness: `tests/Phase1Curved.Repro` (offscreen `HwndSource` + `Bridge.InitViewer`).

User case: heating face **79 (Cylinder)** + exit face **11 (BSplineSurface)**, working
direction +Z, margin 5, spacing 10.

| Check | Result |
| --- | --- |
| Region | 1 closed loop, 14 boundary points, 75.6 mm along Z |
| Pattern | 10 lanes, 21 route pieces |
| Old projected path (baseline) | 2 of 21 pieces fail to stage |
| Phase 1 mapping | 21/21 pieces map onto the selected cylinder |
| Phase 1 staging | 21/21 pieces stage as 3D polylines |
| Face transitions | no non-adjacent face jump |
| Adjacent piece endpoints | 0.000000 mm worst distance (≤ 0.01) |
| Lane distortion | 3D/2D chord ratio 1.2151 worst |
| Adjacent-lane midpoint spacing | 0.01 mm deviation from 10 mm worst |
| Mapping fallback usage | 0 calls / 117 user-case probes (retained, unexercised) |

Survey of other single-face patches in the same model:

| Type | Result |
| --- | --- |
| Cylinder | PASS — face 5 patch routed and staged (1 piece) |
| BSplineSurface | PASS — face 11 patch routed and staged (1 piece) |
| Cone | SKIP — 54 cone faces all form a single loop but are fillets/chamfers; best projected area 8.4 mm², best Z span 4.5 mm |
| Torus | SKIP — 8 faces, all single loop, best projected area 1.7 mm² |
| Sphere | SKIP — all 7 faces return no region loop; the sampled ones are open spherical patches (UV spans well below 2π), so they are not closed surfaces but cannot be region-routed either |
| Fully closed face | SKIP — this model contains no face without a free boundary, so the closed-surface message is not exercised |
| Axis ∥ +Z cylinder/cone | SKIP — 262 cylinder/cone faces, none with an axis within 0.999 of +Z |

The mapping path is type-independent (it only needs a ray hit on a selected face), but
the Cone/Sphere/Torus/closed/axis gates are implemented and unverified against this model.
Do not claim them as functionally tested until a model with usable patches is available.

Plane-only areas keep the previous `BRepProj` path byte-for-byte; `HeatingMold.Core.Tests`
passes 48/48.

## Deviations from the original plan

1. **Compound instead of 0.01 mm sewing** for the filtered ray-cast target. Sewing can
   replace face TShapes, which would break the `g_faceMap` id used in hit results.
2. **New `TryMapRoute` instead of changing `TryMap`.** `TryMap` enforces a 0.1 mm planar
   leaf spacing and emits ~4 points per leaf (≈12k points for a 300 mm piece), far too dense
   for display and for per-point `BRepExtrema` validation. `TryMapRoute` refines on chord
   error only (0.05 mm) and keeps the existing `TryMap`/`TryMapWithMetadata` tests intact.
   Since Phase 1 the app calls only `TryMapRoute`; `TryMap`, `TryMapWithMetadata` and
   `AreSamplesContinuous` are now library primitives exercised by unit tests only.
3. **Anchor 3D point computed on demand** by nearest-segment projection onto the 1D
   `ExitChain` with its parallel `ExitChain3D`, instead of storing an arc-length fraction.
   The result is identical and no new anchor state has to be kept consistent.
4. **Seam check uses face adjacency only** (no 2 mm 3D step cap). Route refinement is
   deliberately sparse on long leads, so a 3D step cap would reject valid leads.
5. **Fallback path** may use the whole-model `QueryWorkingSurface` hit and snap it back to
   the selected faces within 0.5 mm; staging still enforces the 0.05 mm on-surface check,
   so a loose fallback point is rejected rather than displayed off-surface. The instrumented
   harness records every fallback call. The user case (21 pieces, 115 datum probes besides the
   2 anchor points) and the single-face survey (16 probes) recorded **zero** fallback calls:
   the filtered ray-cast resolved every probe. The fallback is therefore retained as the only
   recovery path for curved models that are not covered by this verification, and it remains
   **unexercised** — do not describe it as verified behaviour.

## Phase 2 backlog

- UV-domain routing so spacing is metric-correct on the surface (replace projected spacing).
- Multi-face stitching with per-face UV routing and seam-crossing continuity.
- Periodic seam handling (U/V wrap) instead of treating seams as boundaries.
- Closed-region support (full sphere/torus) by cutting at a chosen seam.
- Axis-parallel cylinder/cone support (full revolution) with a user-chosen seam.
- Extraction lead sag correction and metric-corrected lead length.

## Reproduce

```powershell
# Build the native bridge and app first
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  HeatingMold.Mvp.sln /t:Build /p:Configuration=Release /p:Platform=x64 /m /v:m

# Make the OCCT/3rd-party DLLs visible, then run the harness
$root = (Resolve-Path .).Path
$occt = "$root/third_party/opencascade-7.9.3-vc14-64"
$dirs = @("$occt/win64/vc14/bin") + @(Get-ChildItem "$root/third_party/3rdparty-vc14-64" -Recurse -Filter *.dll | ForEach-Object DirectoryName | Select-Object -Unique)
$env:PATH = ($dirs -join ';') + ';' + $env:PATH
$env:CSF_OCCTResourcePath = "$occt/src"
dotnet run --project tests/Phase1Curved.Repro -c Release -- "$root/resource/Mold.stp"
```

The harness exits non-zero if any of its checks fails.
