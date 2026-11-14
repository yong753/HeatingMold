# HeatingMold project instructions

## 현재 상태와 문서 정리 규칙

- **현재 상태는 `docs/CurrentStatus.md`가 단일 현황이다.** 구현·정책·확인된 검증·실 검증 대기·
  남은 순서를 그 문서에서 확인한다. 이 AGENTS는 계약·이력의 정본으로 유지한다.
- 2026-11-15 문서 정리에서 완료된 실행계획/체크포인트/진단기록 8개를 저장소 밖 백업 후
  삭제했다. 그 문서들을 가리키던 **활성 참조는 `docs/CurrentStatus.md`로 바꿨다.**
  과거 기록을 역사적으로 언급하는 문장은 삭제 당시 사실 그대로 남아 있을 수 있다.
- 보존: `docs/ValidationGuard.md`, `docs/VertexEditing.md`, `docs/VisualTest.Scenarios.md`
  (사용법·스키마), `docs/FixedDirectionProjection.Plan.md`(미구현 계획 + 사용자 직접 수정 상태,
  **수정 금지**), `AI_PARALLEL_DEVELOPMENT_PLAN.md`(미사용 계획), 요구사항 원본 txt/pdf/메일.
- **자동 suite 통과(특히 실패 표본을 근사로 통과시키는 경우)를 사용자 실 CAD 해결로 치환하지
  않는다.** 실 검증 대기 항목은 `docs/CurrentStatus.md` §1·§7·§11에 있다.

## 응답 언어 (사용자 확정)

- 사용자에게 제공하는 설명, 구현계획, 작업 결과는 한국어로 작성한다.

## Coordinate and geometry contract

- Design decisions (generation, editing, splitting, boundary/exclusion tests, pitch and margin) use the **confirmed working plane's 2D coordinates**. Camera coordinates and surface UV are not design coordinates.
- A constrained CAD boundary is the original edge projected onto that working plane. Preserve curves as curves; a display tessellation or endpoint chord is not the authoritative constraint.
- Preserve original edge identity, valid parameter interval, orientation, model revision, working-context identity/revision, and the selected original parameter. A CAD parameter is not a sample index or a linear interpolation ratio.
- Separate unbound boundary search, projection onto an already-bound edge, and direct evaluation of a saved parameter. Editing must not silently switch to a neighbouring edge.
- Evaluate a boundary point's 3D position on the original edge at the same parameter selected in 2D. Unprojecting a datum produces a point on the working plane, **not** a point on the model surface.
- General route points still use the existing selected-face/global surface mapping policy. Original topology matching, surface validation, adaptive sampling and actual 3D lengths remain necessary. This is not a ban on all 3D geometry operations.
- Sampling is allowed for display, hit testing and explicitly error-controlled numerical calculations. It must not replace original CAD identity or silently relax exact boundary constraints.
- Keep cursor snap distances separate from geometry validation tolerances. Missing references, stale contexts, degenerate projections, ambiguous solutions, tangent/overlapping generation intersections must be explicit failures; never hide them by increasing tolerance or falling back to a sampled boundary.
- Preserve candidate identity/binding through conversions and UI selection. Equal 2D coordinates do not prove equal original edges or equal 3D positions.

## Rectangle face selection (user-confirmed 2026-11-11)

- The earlier visible-pixel-only rule existed for solid models. The current product model is a face set, so rectangle selection uses OCCT's own selection judgement instead of face-ID raster visibility.
- Product policy: select **every model FACE that intersects the rectangle**, including faces hidden behind nearer geometry. Always replace the previous selection. Do not branch on solid/face-set shape type and do not add a pixel-based fallback.
- Restrict the result to FACE owners of the original model display object. Route pieces, markers and other selectable objects remain selectable by their own paths but must not leak into the rectangle face result.
- Drag preview and mouse-release confirmation must use the same rectangle query. An empty rectangle is a valid empty selection; a query or apply failure preserves the drag-start selection.
- The low-level selector overlap flag plus `Pick` is the product query. The face-ID image path and its 8x refinement remain diagnostic only; never call them from the product rectangle path.
- Verified 2026-11-11: `tests\WorkingSide.Regression` passes 167 checks, including both coincident faces, the face hidden behind a near sheet, edge-crossing rectangles, reverse drags, empty rectangles, hidden-face preview, no face-ID builds, and the default example selecting 80/80 faces before and after working-context remesh. App tests 121 and Core tests 185 pass.
- Not yet verified manually: real Shift-drag in the app, zoom/pan/window-resize variation, Escape/capture-loss cancel, and rectangle selection while routes/markers are displayed. The original 43/49 counts were not reproduced; neither count is an expected value.

## 제외영역 허용 범위 (사용자 확정)

- 작업영역 내부에 구멍을 만드는 제외영역, 적용영역을 둘 이상으로 분리하는 제외영역은 허용하지 않는다.
- 제외 후에도 구멍 없는 단일 연결 영역이 유지되는 경우(외곽을 깎는 제외)만 허용한다.
- 제외영역 추가/변경은 이 조건을 먼저 검증하고, 위반 시 기존 제외영역·경로·선택점을 보존한 채 명시적으로 거부한다.
- 유효 외곽선은 마진 적용 전 `적용영역 − 제외영역`이다. 제외로 새로 생긴 직선 구간은 원본 CAD edge/parameter로 가장하지 않고 별도 artificial provenance로 보존하며, 3D 평가는 선택 면 표면 매핑을 사용한다.

## Transactions and preservation

- Prepare immutable candidate circuits, IDs, lengths, mapping, fingerprints, endpoint arrays and connector states before committing.
- Validate and stage **all changed pieces**, then perform one native commit. Unchanged areas retain their committed circuits/mapping/native objects; never rebuild them from draft inputs.
- Include boundary binding, geometry, context and validation policy in cache keys. A cache hit must not bypass changed endpoint constraints.
- Separate native retain/replace/delete, including an empty final result. Do not bypass route transactions with a global overlay clear.
- Before commit, any failure preserves existing paths, lengths, IDs, endpoint bindings, edits and other areas. After commit, publish prepared managed references before presentation updates; redraw failure is not geometry rollback.
- Route presentation targets (piece-color IDs/RGB and endpoint markers) are prepared from the committing final area set before the single native commit and applied from that snapshot afterwards. Never re-read the mutable area list at that point: a deletion has already removed the dropped area's native piece IDs, so recoloring from the pre-delete list fails the native all-or-nothing check and raises a spurious warning. Validate that color targets are exactly retained ∪ final, with no duplicates and no staged/deleted IDs. A post-commit presentation failure keeps the committed geometry and must remain visible in the caller's completion status instead of being overwritten.
- The same rule applies to the route length labels: the label refresh reads the committed native chains of an explicit area set, so the commit path must pass the committing final set. Reading `_areas` there re-queries a just-deleted area and fails with `영역 <name>의 확정 발열경로를 읽지 못했습니다.` (`Bridge.GetCommittedRouteChains` returns null whenever the returned chain count differs from the requested ID count). Verified 2026-11-11 by build and App tests (Release/x64, 182 pass) only; the end-to-end split of a routed area in the running app was not re-run. The 2-argument overload still reads `_areas` and is only valid after the list has been replaced (project load).
- Actual circuit length/electrical calculations use the validated 3D route once. Dashed guides contribute no heating length.

## Scope and verification

- Do not introduce automatic detours, new self-intersection/pitch/bend-radius checks, Undo/Redo, power-connection changes or expanded DXF output without explicit approval.
- Preserve the existing supported-face/region gates and general-edit policy. Do not replace all polygon Boolean/offset operations with CAD Boolean operations.
- Keep legacy APIs unless actual caller/compatibility analysis justifies removal. Historical phase documents are not the current geometry contract.
- Add regressions for inclined/curved edges, sewing subranges, identical projection at different depths, endpoints/periodicity, ambiguity/staleness, fixed-edge dragging, same-FACE multi-area independence, narrow exclusions, and cross-area draft/commit isolation.
- Native builds must match the App reference at `x64/Release/OccBridge.dll`; record the actual loaded DLL when diagnosing native regressions.
- Run focused tests per change and a review/correction/retest cycle for significant changes. Automated scenario completion is not recorded visual or manual-drag verification.
- Never claim the user's failing CAD model is fixed unless that model was reproduced and tested; report unverified coverage explicitly.

## Sample STEP model (verified 2026-11-10)

- `resource\Mold_Solids.stp` removes the surface bodies from the product structure only: the deleted entity is the single `SHAPE_REPRESENTATION_RELATIONSHIP` (`#23`) that linked `MANIFOLD_SURFACE_SHAPE_REPRESENTATION` `#13`. The definitions of `SURFACE.135` (86 faces) and `SURFACE.142` (4 faces) remain in the file as unreferenced data, so external CAD that reads representations directly can still display them. The app follows the product structure and loads 30 solids / 574 faces.
- `tools\step-transform\bake-axes.ps1` applies the load-time `supplemental geometry` axis rotation to every `CARTESIAN_POINT` and `DIRECTION` value (rotation about the STEP origin, no translation). Rotating the supplemental placement as well makes the app's load-time rotation identity (verified `max |R' − I| = 4e-17`, zero point-wise error), so a baked copy opened in CAD uses the same coordinates the app displays.
- A CAD re-export of a baked copy normally drops the `supplemental geometry` representation; the app then falls back to principal inertia axes, so a reimported edited model needs the frame pinned again rather than assumed.
- `resource\Mold_Solids_aligned_surface.stp` re-adds the deleted relationship (`#23`, 129 bytes) to the baked copy, so OCCT loads 30 solids + 2 surface shells (664 faces) while keeping the supplemental frame.
- `tools\step-cleanup\diagnostics\thicken.cpp` thickens a free shell. On `SURFACE.135`, `BRepOffsetAPI_MakeThickSolid::MakeThickSolidByJoin` and `BRepOffset_MakeOffset` fail in every join/self-intersection/mode combination, and `PerformBySimple` only returns a shell; only `BRepOffsetAPI_MakeThickSolid::MakeThickSolidBySimple` produces a solid. `resource\Mold_Solids_surface135_thick3.stp` is that shell at 3 mm toward the mold solids: 1 solid / 206 faces, volume 4,306,377.6 mm³, area 2,869,973.6 mm² (3.01 mm). The side is chosen by sampling candidate faces against the mold solids: +normal 0.134 vs −normal 0.073 inside fraction.
- The thickened body carries 18 degenerate zero-length edges (total 0.096 mm) inherited from the source patch, so a STEP round-trip reports one face `NotClosed`. They are not visible gaps; do not claim the body is watertight without re-checking these edges.

## Default example model `Mold_Cover_Trim.step` (user-confirmed 2026-11-11)

- The user designated `resource\Mold_Cover_Trim.step` as the default example model. The file on disk is `.step`, not `.stp`. SHA-256 `8CB03405817606D1917FEF55B46A60011FFB5E93075EECDEF5A8651D4359FCED`.
- Verified structure: one open shell, 80 faces, 0 solids, area 958,841.060 mm², BRepCheck valid, one free-boundary loop of 60 edges. It is the Mold part of `Mold_Item_trim100.step` (identical bbox, area and face count); the trim used the item's YZ outline offset by 100 mm with square corners, so the X extents are unchanged.
- **User-confirmed: the heating face is +X.** The example's working normal is (+1,0,0). The file has no `supplemental geometry`; since the 2026-11-11 import-policy change the app keeps the file coordinates (no principal-axis rotation), so in that frame the broad faces face ±X and a +Z working plane would project them nearly edge-on.
- Implemented 2026-11-11: `LoadFile` applies the working normal (0,0,1) by default; `ExampleModelPresets` replaces it with (+1,0,0) when the file hashes to `Mold_Cover_Trim.step`, so '예제 열기' and 'STEP 열기' use identical coordinates. For a ±X normal the in-plane X rule picks (0,0,∓1) so that plane Y = (0,1,0) and the viewport's screen-up direction is world +Y (user-confirmed 2026-11-11); non-X normals keep X = (1,0,0). The default example lookup (`OcctEnv.FindMoldStp`) and the csproj content copy now target `Mold_Cover_Trim.step`; `Mold_Solids.stp` remains as a regression fixture.
- The visual-test scenario loader rejects `model.solidCount <= 0` (`VisualScenario.Load`), so it cannot yet target this 0-solid model. Change that gate and author the new-model scenario together; do not fake a solid count.
- Import policy (user-confirmed 2026-11-11): use `supplemental geometry` when present; otherwise load the STEP in its own coordinates with no rotation. Principal-axis alignment remains only to restore projects saved with policy 1. New saves use `ImportPolicyVersion = 2`; load accepts 1 (legacy principal axes) and 2 (supplemental or file frame).
- Working-context mesh exception (2026-11-11): face #46 is a BSpline sliver (area 0.000143114 mm², width about 0.0002 mm) that gets no triangulation at any deflection, so the old completeness check failed the whole +X context. `CreateWorkingNativeCore` now skips only untriangulated faces whose area is ≤ `deflection²` (0.0025 mm² at the 0.05 mm working deflection), logs `working mesh: skipped N sub-deflection face(s)...` to `HEATINGMOLD_OCC_LOG`, and still fails for larger untriangulated faces. Never raise the deflection or tolerance to hide a missing face; the design boundary comes from original CAD edges, not this mesh.
- Region-boundary degenerates (2026-11-11): the exterior-edge classifier counts edge uses per selected face, so a zero-length pole/apex edge is counted once and looked like a boundary edge; the source matcher then aborted with `region-boundary: missing-source len=0.000000`. Both the working and original boundary collections now skip `BRep_Tool::Degenerated` edges and log `region-boundary: skipped degenerate edges working=N original=M` to `HEATINGMOLD_OCC_LOG`. On this model, single-face scans of 15/28/32/40/58/59/62/70 each skip one edge; the reported missing-source error is gone. Faces 17/34/62/70 then exposed a separate app-side continuity check (next bullet). Do not replace the degeneracy rule with a length threshold or a larger tolerance.
- Boundary continuity (2026-11-11): the step-to-step sampling check compared consecutive edges' curve endpoints with the 0.01 mm sewing tolerance. On this model the shared vertices carry 0.0226799 mm tolerance; faces 17/34/62/70 had endpoint gaps 0.021611 / 0.019852 / 0.010124 / 0.012633 mm while `sharedVertex=1` and `vertexDist=0`. That is a valid CAD vertex connection, not a model gap. The check now allows `max(sewingTolerance, BRep_Tool::Tolerance(sharedVertex))` for step continuity and loop closure, and logs `region-boundary: gap within vertex tolerance ...`; it still fails when the vertices are not the same or the gap exceeds the vertex tolerance. This is not a global tolerance increase: non-shared edges keep 0.01 mm and the original-edge source match stays 0.1 mm. After the change all 80 single-face boundaries succeed; the model was not modified for this cause. The user's actual multi-face selection is still unverified.
- The native working origin is the projected bbox center, positioned 1 % (minimum 1 mm) in front of the model along the normal; model coordinates are never translated. Record the actual expected origin after the first instrumented load instead of assuming it.
- Pending measurements for this example (do not guess): the face set that yields a single closed region on the open shell, the start/end exit points, and the expected circuit piece/turn counts for the scenario.
- Thickening test (2026-11-11, `tools\step-cleanup\diagnostics\thicken.exe`): a 3 mm offset makes one solid / 212 faces / 2,880,990 mm³ in memory, but the STEP re-read is BRepCheck **INVALID** with 19 free edges and vertex/edge tolerance 6.000 mm. A 1 mm offset re-reads valid (212 faces, 960,222 mm³) but still has 18 free edges (0.053 mm total, one 17-edge open chain and a degenerate point). Both written variants are the outward offset, which covers the original +X face with a new offset surface; the inward variant completes in memory but the tool writes only the first candidate. Do not call this model a closed solid until a STEP round-trip passes with zero free edges and normal tolerances. Known trouble spots: the non-planar 60-edge rim and the pinch near raw coordinates (-4341.6, -591.9, 965.4); `ShapeAnalysis_Shell` reports `oriented=no` on the source shell.
- A thickened solid also changes the app's import alignment from surface principal axes to volume principal axes, so its loaded frame, origin and face IDs must be re-measured rather than inherited from the open shell.

## Region boundary topology policy (user-confirmed 2026-11-11)

- **User requirement: exit-face candidates are not a precondition for exit-point selection.** Picking the start/end exit point must work from the effective region outline alone; never gate it on `GetBoundaryExitSides` returning candidates. A full-model selection legitimately has no non-selected adjacent face, so an empty candidate list is normal, not an error.
- Verified root cause of `region-boundary: selected faces are non-manifold` on the 80-face default example: the original topology is complete (261 non-degenerate edges; 52 used once, 209 used twice, none above two). Adding all 80 faces to `BRepBuilderAPI_Sewing(0.01, true, true, true, true)` merges original edges into one edge shared by four distinct faces 34/46/62/70 (3D length 0.0449975 mm). The app rejected its own temporary copy, not the model.
- Implemented policy: `BuildOriginalSelectedRegionShape` decides from the original faces alone. If every non-degenerate edge is used by one or two selected faces and those faces form one edge-connected component with at least one exterior edge, the original faces are used as-is and **no sewing runs**. Otherwise a bounded repair runs with non-manifold mode **off** (already-shared internal edges are never merge candidates) and the selected face count must survive unchanged.
- With no sewing, boundary edges are the original edges, so the source history is pinned to the identical original edge (`IdentitySourceHistory`). An empty history makes the 0.1 mm geometry search ambiguous on the 6e-6 mm sliver rim edge (`ambiguous-source len=0.000006`). Do not fix that by loosening the match tolerance or adding a length threshold.
- `ExtractBoundaryExitSides` uses the same preparation and the same degenerate-edge skip, and now fails the whole query instead of publishing a partial component/coverage result.
- `BuildProjectedWorkingWire` uses the original selected-face compound as the projection target when the original topology is complete, and only falls back to bounded sewing otherwise.
- Verified 2026-11-11: `tests\WorkingSide.Regression` 173 checks pass, including new checks that the 80-face selection yields one closed loop with original edge references, that the full-model selection has no exit-face candidate, and that the candidate lookup leaves the region unchanged. App tests 121 and Core tests 185 pass.
- Not yet verified: real app mouse picking, saving and reopening an 80-face area, and project files whose saved boundary intervals came from a previously merged subrange.

## Electrical review: one shared heating power (user-confirmed 2026-11-11, supersedes applied-voltage mode and per-area target power)

- **User requirement (current product rule, corrected 2026-11-11):** the product has **one** heating-power input in kW, and it is the total the whole confirmed area set may use. Target heat power and allowed power are the same concept; do not reintroduce a second input or a per-area target power. Allowed voltage and current (defaults 220 V / 16 A) are still **per-circuit** limits. Wire diameter and resistance per metre stay fixed by the selected case (Case 1: SUS 316L Ø0.30 mm / 4 Ω/m; Case 2: Ø0.15 mm / 8 Ω/m); direct wire input is not part of the product UI.
- **User-confirmed shared-budget rule:** if one area is divided into two, the **sum** of the parts' powers must stay at or below the allowed limit. Splitting never increases the usable total power, and two parts do not each get the full limit.
- **User correction superseding the earlier rule:** 220 V is the default **allowed** voltage, not a fixed applied voltage. The earlier "one voltage input feeds both the calculation and the voltage limit" rule is no longer the product rule; do not reintroduce it.
- Implemented calculation: R = rL, V = √(P·R), I = √(P/R) from each circuit's allocated power (`ElectricalCalculator.ComputeFromTargetPower`). A non-finite or non-positive power is rejected explicitly: 0 W would otherwise pass every limit silently.
- Implemented allocation (`TargetPowerAllocation.TryAllocate`): the single heating power is distributed over the circuits of **every Ready confirmed area, independent of the review scope**, in proportion to each circuit's **actual measured 3D conductor length**, P_i = P·L_i/ΣL_i, and the last share takes the remainder so the total is preserved exactly. `WorkingArea.TargetPowerW` is that derived share (null until the allocation runs); `TargetPowerText`/`TargetPowerError` no longer exist. Filtering the review to the current area must not re-allocate, and editing one area does redistribute the whole set because the budget is shared.
- With length-proportional allocation every circuit carries the same current, I = √(P/(rΣL)). Splitting more therefore cannot cure a current-limit failure; never auto-split indefinitely on an over-current verdict and never declare a split impossible from the pre-split length alone when the final conductor set changes.
- Implemented verdict: `ElectricalLimits` (Core) holds the three limits; `CircuitAssessment` compares each circuit's unrounded voltage/current (`<=` is accepted) and lists every exceeded item; `ElectricalSpecTable.IsSharedPowerOverLimit` compares `TotalTargetPowerW` with `Limits.PowerLimitW`, so an over-limit **sum** is NG no matter how the area was split. `ElectricalSpecTable` keeps NG rows `IsValid` (calculation errors stay separate in `Message`) so NG circuits remain in rows, totals, the UI and DXF. `ElectricalSettingsResolver.TryResolveLimits` reads the allowed limits and treats the power field as the shared heating power.
- The review table has a `판정` column (`OK`/`NG`; calculation errors and a row without allocated power show `—`). The summary reports the allocated total against the limit and never reports '전체 OK' while any requested area is unready or a calculation error exists.
- **User-confirmed UI placement (2026-11-11, corrected):** the path tab is generation-only. The review tab owns the single heating-power input (`AllowedPowerBox`, label `발열 전력 (kW) — 전체 합계 한도`) and the automatic-split button (`SplitAreaButton`, `SplitStatusText`). `TargetPowerBox` was removed; do not reintroduce a per-area target-power control. Keep the control names, events and automation names when relocating them in XAML.
- **User-confirmed default:** the shared heating power starts at **3.5 kW** (`ElectricalSettingsResolver.DefaultPowerLimitText`). This corrects the earlier 35 kW request; do not reintroduce 35 kW. A loaded project keeps its saved value, and a cleared input stays empty with an input error instead of silently regaining the default.
- **User-confirmed UI consolidation (2026-11-11):** the review tab is one electrical-specification section. Its separate 회로별 길이 table was removed; circuit length is the specification table's `길이 m` column, and the removed table's `[참고]` rows for stale areas are gone with it. The viewport expander now mirrors the same specification rows (`ExpandedElectricalGrid`, header 전기 사양 크게 보기) instead of a duplicate length table. The read-only 직경/단위 저항 boxes were removed because the preset label already states both; project save still writes them from the selected Case via `GetFixedWireText`, and loading no longer fills any box. The split button stays on the review tab without its own section title.
- `ReviewSnapshot` has no `MissingTargetPower` state; `ExcludedCount` counts every entry that is not `Ready`. A row whose circuit received no allocation keeps its measured length and resistance, leaves power/voltage/current blank (never 0), and notes "발열 전력이 배분되지 않았습니다"; it never becomes exportable.
- Project schema is now version **6** (`ProjectJsonStore.CurrentSchemaVersion`, `ProjectDocument.SchemaVersion`). New saves write 6; only schema 6 loads. `ProjectJsonStore.Load` reads `schemaVersion` before strict deserialization and rejects every older or newer version with an explicit message, so a legacy file is refused before the native model is replaced. `ProjectElectricalMigration`, per-area `targetPowerText`, the legacy `appliedVoltageText`, and the diagnostic boundary-kind strings were removed in the 2026-11-12 legacy cleanup.
- A cleared **allowed-limit** text field saved by the user stays empty and shows an input error; it is not silently replaced. There is no per-area target-power field to restore any more.
- DXF output keeps the existing numeric electrical columns (adding verdict or limit columns is a separate approved change). The spec-table caption now states `입력 발열전력 길이비 배분 · 회로별 필요 전압 계산 · 회로별 전압/전류 및 합계 전력 판정`, matching the shared-budget verdict.
- Verified 2026-11-11 after the shared-power change: App builds Release/x64 with 0 errors (6 pre-existing CS8632 warnings in `MainWindow.ProjectLoad.cs`), Core tests 256 pass, App tests 177 pass, and every WorkingSide regression group passes (173 working-side, 36 pattern-state, 64 geometry-rollback, 19 project-load, 50 project-load exclusion, 3 electrical, 30 sidebar, 20 area-highlight, 22 exclusion+exit, 19 direct-exit, 22 vertex-edit). The new checks cover the 1.4 kW / 2.1 kW length-proportional allocation, the summary `배분 … / 한도 …` totals, no re-allocation when the scope is filtered, an area without a generated route being excluded, an unready area blocking the whole OK verdict, and the single input living on the review tab. Not verified: the running app's layout and real mouse interaction, splitting from the review tab, a project saved by the new UI reopened in the app, the legacy-project confirmation dialog in the running app, and DXF readability in an external CAD viewer.


## Area split into independent areas (user-confirmed 2026-11-11)

- **User requirement:** an over-limit area is handled by splitting the **applied area itself** into multiple independent areas, each holding exactly one circuit. Never place several circuits inside one area, and never keep a persistent parent/child link after the split.
- **User-confirmed split axis:** a horizontal wire layout splits with a **horizontal-cut line** (above/below); a vertical layout splits with a **vertical-cut line** (left/right). Splits use the confirmed working plane's 2D coordinates.
- **User-confirmed order (corrected 2026-11-11, supersedes "the user generates the route from those points"):** split first into areas that can have equal expected heating length, then the app assigns each new area's start/end exit point, then it **generates each part's route in the same split**. Automatic assignment and automatic generation are both the product behaviour; the user may still reselect a point and regenerate afterwards. A split that cannot assign and generate **every** part commits nothing and keeps the parent area, its route and its selected points.
- **User requirement (2026-11-11): the assigned points must stay inside the range the user had already selected.** (a) each part's start/end sweep coordinate lies inside the parent's original start→end sweep interval, (b) every point keeps at least the part's margin from the part's own sweep-direction ends, including the middle part of a 3-way split, (c) the point keeps a real boundary binding. Confirmed interpretation: "inside the original start/end points" means the **sweep/progression axis** (horizontal layout → Y, vertical layout → X) and the margin is measured along that same axis; a Euclidean inset from every side would leave the boundary. Only the selected exit points are constrained — the generator's existing final extra lane may still pass the selected end.
- **User correction:** although the parts are called split areas, they are independent areas exactly like any other confirmed area. The margin is each area's **internal** margin, applied to the new cut boundary as well; the wire spacing stays per area.
- **User-confirmed maximum (2026-11-12, supersedes "not limited to 2 parts"):** one automatic split produces at most **4** independent areas. Compute the required count from the required circuit voltage as before; if it exceeds `TargetPowerSplitPlanner.MaxParts = 4`, the split is refused with an explicit error and the parent area, its route and its selected points are kept. Never clamp 5+ down to 4 and never commit only some of the required parts. This caps one split operation, not the total number of areas in the project; 2–4 parts still fail normally when margin/exit/balance conditions fail. The power limit is **not** a split criterion because splitting cannot raise the shared total. An unsupported or impossible split must return an explicit error; never return a partial split as success.
- The split decision uses the expected routed length per candidate area (area / spacing plus the across-lane extent, margin applied per child), not equal area. Equal expected length is only an estimate: the final verdict uses each circuit's actual measured 3D length, and a final verdict cannot be promised before the exit points exist.
- A new cut boundary is **artificial provenance** for its own line; never disguise it as an original CAD edge or as the side of an exclusion rectangle. The original outer CAD boundary keeps its original edge identity and parameter interval.
- Splitting does **not** increase the shared heating power: the parts keep sharing the one budget, and the area's allocated share is what caps the part count. There is no session-only split family, no `SplitFamilyKey` and no `TryApplySplitFamilyAllocation`; the allocation is recomputed for the whole confirmed area set after every route change. A proposed split whose computed circuits still fail is reported, not silently retried forever.
- Committing a split is one atomic replacement of the original area by its parts: prepare every part's geometry, lengths, exit points, generated circuits and presentation targets first, then one native commit. A failure or cancel before that commit keeps the original area, routes, lengths and selected exit points.
- Implemented 2026-11-11: `AreaSplitter.Split` computes the bands and their expected length (area / spacing plus the across-lane extent, child margin applied), `TargetPowerSplitPlanner.TryPlan` decides the part count from the required voltage (`partsByPower` was removed), and `MainWindow.Split.cs` runs the product flow: parent validation → part count → user cancel check → `SplitRegionBoundary2D.TryCreate` per band → automatic exit points → per-part `TryGenerateAreaPattern` on a detached `ForkAreaRoute` candidate → one `TryCommitAreaRouteCandidates(finalAreas, candidates)` replacement that deletes the parent route and registers every part route → shared-power allocation recomputed for all areas. Part `AreaId`s are reserved from a local counter before the commit and published to `_nextAreaId` only after it succeeds. A part count above `TargetPowerSplitPlanner.MaxParts` (4), an over-current plan, an unassignable point and a failed part generation are explicit refusals, not partial splits.
- Implemented 2026-11-11 (`SplitExitPointPlanner.TryAssign(parent, children, margin, spacing, out failure)`): the parent points are rotated into the part frame (`Serpentine.ResolveAngleDeg` with the UI-effective direction, where `Auto` means FrameX) and the sweep sign comes from the parent's start→end ordering. The allowed row band is `[max(parentLow, marginMin), min(parentHigh, marginMax)]` from `RegionOps.BuildPatternRegion(childRing, null, margin)`; the rows are the spacing grid inside that band, and the first/last populated row become the start and end rows. A start row that lands on the margin band's own boundary row is moved `RowInsetGuard = 0.01` mm inside, and intervals narrower than `MinimumLaneWidth = 0.2` mm (`UsableIntervals`) are not treated as lanes: the generator's half-open scanline test leaves the top boundary row empty, so an unguarded boundary row produced `NoLanes` for the middle part even though the band looked populated. Start stays on the side nearest the parent start point (forced to the low side when the row has several intervals and the part has no exclusions, because the no-exclusion generator requires the first interval). The end side is the last lane's exit side; with exclusions the entry side follows `ChooseEntryLow` per lane instead of simple parity. Each target is resolved by `SplitRegionBoundary2D.Project` (exact `ProjectSplitLine` first when the target lies on a cut line), and the resolved point must keep its row within `max(SideTolerance, min(1, spacing/2))` and stay on the requested side. Resolution never snaps to the nearest boundary of the whole part and never copies a parent coordinate. A row band that collapses inside the margin and a start row with no scanline interval are explicit failures.
- Automatic assignment runs only when the parent already has a usable selected pair; without one the split commits the parts with no route and the points stay for manual selection. When a pair exists, any ambiguous or unassignable child refuses the whole split before the native commit with an explicit reason, and every assigned point is also evaluated on the 3D surface (4-argument `TryEvaluateSelectedExit3D`) before that commit.
- Implemented 2026-11-11 (single-lane parts): a part thinner than one spacing legitimately produces one lane, so both selected points can share the same sweep row. `Serpentine.GenerateWithSelectedExits` now falls back to `TryBuildSingleLaneLocalRoute` only when `TryResolveSweepDirection` fails with `IndeterminateDirection`; the start point must be outside the lane interval end and the end point must lie beyond the same lane on the opposite side. An end point on the wrong side still fails with `IndeterminateDirection`, and a start point inside the lane fails with `AnchorNotAtLaneEnd`. The exclusion path (`GenerateComponentsWithSelectedExits`) still requires a resolved sweep direction; do not silently add a second lane or reverse the serpentine.
- Split parts share the parent's `FaceIdSet` and the original CAD edge references. `SplitRegionBoundary2D` implements CAD, exclusion-rectangle and `SplitLineBinding` queries; `SplitRegionContainmentBoundary` provides the exact inside test over the part ring. Each part stores `SplitCutLines` — the inherited parent cut lines plus the new full padded cut lines, never the clipped child chords — and rebuilds the same boundary on load, so a cut-line exit point keeps its line identity. A project saved before this change stored clipped chords there; a cut-line exit point saved in such a project cannot be restored and the load fails explicitly instead of rebinding to a nearby line.
- Implemented 2026-11-11 (partial fill): a split part is highlighted with its own clipped fill instead of only its 3D outline. `SplitRegionBoundary2D.TryGetRetainedHalfPlanes` turns the cut lines that actually bound the part into axis-aligned half-planes (the unsplit side is decided from the part ring, a non-axis-aligned cut line is an explicit failure, zero-length segments are skipped, and a part whose outline kept no cut segment at all is refused instead of silently falling back to the exclusion-only fill). The side test allows `RingGridTolerance = 0.01` mm because the band polygon comes from the 0.01 mm Clipper grid, so a middle band's ring sits about half a grid cell (0.005 mm) outside its cut lines; a tighter epsilon wrongly reported `분할 영역 외곽을 절단선이 가로지릅니다` and dropped that part's fill. `BuildAreaHighlightMeshNative` clips the projected face triangles with those half-planes before subtracting exclusion rectangles, so `StageAreaHighlightMesh` no longer requires exclusions; `Bridge.StageAreaHighlightMeshClipped(areaId, modelRevision, workingId, faceIds, rects, planes, frame, color)` passes them as `(axis, limit, keepLess?1:0)` triples. `ApplyAreaColors` and `UpdateActiveAreaHighlight` no longer skip `HasSplitBoundary`. This clip is display-only: it never changes the design boundary, lengths or CAD constraints. `CommitAreaHighlightNative` now separates pending from committed state, revalidates the model revision and working context, and only removes the previous presentation after the new one displays.
- Changing exclusions on a split part is explicitly rejected (`TryRefreshEffectiveBoundary`) because the cut-line provenance would have to be rebuilt; re-confirm the area instead.
- Project schema is now version 4 and stores per-area `splitCutLines`. Schema 1–4 load; new saves write version 4.
- Verified 2026-11-12 (after the row guard and the grid tolerance): Core tests 257 pass. App tests 183 pass (`MarginBandBoundaryStartRow_IsMovedInside`, `RetainedHalfPlanes_AcceptsRingOffsetByHalfGrid`, `RetainedHalfPlanes_RejectsRingWithoutCutSegment` added; the two row assertions now allow the 0.01 mm guard shift). `tests\WorkingSide.Regression` passes every group (30 sidebar, 30 area highlight, 22 exclusion+exit, 19 direct exit, 22 vertex edit, 3 electrical, 38 pattern state, 64 geometry rollback, 19 project load, 50 project-load exclusion, 173 working-side). A real saved project (`Mold_Solids.json`, schema 4, the default example with one area A carrying a 60-piece route, FrameX, margin 5, spacing 15) was opened and split through the app's own `SplitActiveArea` in the offscreen harness: 3 parts with 12 / 20 / 24 pieces, allocated 838.3 / 1399.5 / 1262.2 W, and fills with 21 / 3884 / 6874 triangles; every assigned start/end kept its original CAD edge key and parameter. Not a real mouse click. `SplitExitPointPlannerTests` was rewritten for the row/margin rule (nested 3-band→2 split, parent-range clamp, vertical layout, same-row refusal, one unresolvable child leaving siblings untouched, margin that consumes the part, usable-pair predicate, full-cut-line identity) and the area-highlight group covers the native half-plane mask, the retained side of the outline and a managed split area's clipped fill.
- Not verified: real mouse split and the automatic points/generation in the running app, the on-screen start/end markers of split parts, saving and reopening a split project with automatically assigned points and routes, automatic mapping onto original CAD sides (only cut-line cases are covered by tests), curved/concave part boundaries, DXF export of split parts, the same FACE split into several parts, and the fill shape/zoom appearance of a clipped split part.

## 분할 2D 경로 길이 균등화 (구현 2026-11-12)

> **2026-11-12 사용자 확정으로 대체됨:** 아래 1.10 초과 하드 거부와 인출점 변형 탐색은
> 제거되었다. 현재 규칙은 `## 분할 인출 방향 고정과 절단 위치 전용 탐색` 항목을 따른다.
> 이 절은 당시 구현의 이력으로만 남긴다.

- **사용자 요구:** 전기검토 NG로 영역을 분할했을 때 각 영역의 발열사 총 길이가 비슷해야 한다. 면적/간격 근사만으로 절단을 정하지 말고, 실제 선택 인출점 2D 생성기의 경로 길이를 기준으로 절단을 정한다. 사후 3D 재생성/재분할 루프는 추가하지 않는다.
- `AreaSplitter.TryBuildAtCuts`는 주어진 절단 좌표로 밴드만 구성하며 예상 길이 1.10 한도를 적용하지 않는다. `AreaSplitter.Split`은 기존처럼 면적 근사로 초기 후보를 만들고, Auto 방향은 UI/생성기와 같이 가로(FrameX)로 해석한다.
- `SplitExitPointPlanner`는 제외영역을 마진만큼 확장한 `RegionOps.BuildGenerationRegion`과 생성기와 같은 `ScanlineIntervals`로 레인 행을 판정한다. 인출점의 경계 투영 목표는 마진 적용 전 영역 외곽의 최좌/최우를 그대로 쓰고, 측면 선택만 실제 레인 구간 끝으로 한다. 0.2 mm 미만 구간을 레인에서 제외하던 필터는 제거했다(생성기와 동일 판정).
- `MainWindow.TryGenerateSelectedExitPattern`이 선택 인출점 생성의 단일 경로다. Auto는 FrameX로 정규화하며, 분할 후보 평가와 최종 생성이 같은 함수를 쓴다.
- `MainWindow.TryPrepareBalancedSplit` / `TryEvaluateSplit2D`가 후보마다 자식 경계·자동 인출점·2D 경로를 실제로 만들고 `SerpentineRoutePiece.Length` 합(발열선+턴+기타 연결선)으로 `max/min`을 계산한다. 목표 1.02, 허용 1.10. 후보 출발점은 기존 면적 균등 절단, 전체 범위 균등, 부모 선택 구간 균등, 2분할 전 범위 스캔이다. 지역 탐색은 spacing/2, /4, /8 단계의 결정적 좌표 이동이며 전역 최적을 보장하지 않는다. 평가 예산은 2~4분할 모두 96회다(최대 4개 정책에 맞춰 5개 이상 분기는 제거됨, 생성기 레인/조각 상한이 아니다).
- 허용 1.10을 넘으면 일부만 확정하지 않고 명시적으로 거부하며 부모 영역·경로·선택점을 유지한다. 성공 시 선택한 후보의 자식과 이미 생성한 2D 패턴을 그대로 최종 후보에 게시하고, 3D 인출점 검증 후 기존 단일 `TryCommitAreaRouteCandidates`로 원자적 교체한다. 확정 시 경로를 다시 생성하지 않는다.
- 검증 2026-11-12: Core 테스트 260 통과, App 테스트 188 통과(신규 `SplitLengthSearchTests` 5개 포함), `tests\WorkingSide.Regression` 기존 전 그룹 통과, App Release/x64 빌드 0 errors. 신규 테스트는 300×300 중첩 분할에서 부모 선택 구간 20..200일 때 면적 균등 절단의 실제 2D 비율보다 탐색 결과가 개선됨을 확인하고, 외곽과 맞닿은 제외영역이 있는 후보에서도 2D 경로와 같은 레인 연결선이 생성됨을 확인한다.
- 2026-11-12 원인 확인(제품 수정 없음): 바탕화면 `발열몰드 저장테스트/Mold_Solids.json`(기본 예제, 영역 A, FrameX, margin 5, spacing 15, 3.5 kW)은 분할 계획 3분할까지 진행되지만, 초기 면적 균등 절단의 실제 2D 길이는 3,976.72/6,631.06/5,886.81 mm(1.667)이고, 절단 전 범위 2 mm 스캔 14,962개 후보의 최선 2D는 5,303.96/5,303.97/5,886.81 mm(1.1099)로 허용 1.10을 넘는다. 최선 후보를 실제 3D로 커밋해 측정한 길이는 5,323.82/5,332.05/6,009.73 mm(1.1288)로 2D보다 더 나쁘다. 4분할은 2D 1.147로 더 나쁘다. 실패는 로드/경계/인출점 오류가 아니라 고정 15 mm 피치의 레인 수 양자화 때문에 1.10을 만족하는 절단이 없기 때문이다. 현재 제품은 1.10 초과 시 분할을 거부하고 부모를 유지한다. 실패 문구는 '탐색 평가 한도'를 안내하지만 전 범위 스캔으로 1.10 미만 후보가 없음을 확인했으므로, 이 사례에서는 문구가 원인을 정확히 설명하지 않는다. 이 분석은 절단 위치만 탐색하던 시점의 것이며, 이후 인출 측면·시작 레인 위상 변형 탐색(아래 항목)으로 같은 프로젝트가 2D 1.0219로 분할된다. 허용 완화와 경고 후 확정은 여전히 미승인·미구현이다.
- 미검증: 실행 중 앱에서 실제 마우스로 분할, 새 탐색을 거친 분할 프로젝트 저장/재열기, 일반적인 곡면/경사면에서 2D 균등이 3D 실측 균등으로 이어지는지(위 사례는 3D가 더 나쁨), 실제 80면 예제의 제외영역/좁은 중간 밴드, 대형 모델에서 탐색 시간, 분할 영역의 DXF/STEP 출력. 이 항목은 확인 전 '해결됨'으로 기록하지 않는다.

## 분할 절단+인출점 변형 탐색 (구현 2026-11-12)

> **2026-11-12 사용자 확정으로 대체·삭제됨:** 시작/종료 측면과 시작 레인 위상을 바꾸는
> 변형 탐색과 `SplitLengthCombinationSolver`는 제거되었다. 현재는 인출 방향을 고정하고
> 절단 위치만 탐색한다. 아래 내용은 제거 전 구현의 이력이다.

- **사용자 요구(2026-11-12):** 2D 길이 균등화에서 절단 위치만 바꾸지 말고, 같은 생성 규칙 안에서 선택 가능한 인출 측면과 시작 레인 위상까지 탐색한다. 최종 판정은 여전히 실측 3D이며, 이 단계의 2D 비율로 3D를 예단하지 않는다.
- `SplitExitPointPlanner.TryBuildCandidates(parent, children, margin, spacing, includeVariants, out lists, out failure)`가 자식별 후보 목록을 만든다. 첫 번째 후보(기본)는 기존 `TryAssign` 동작과 같다: 부모 시작점에 가까운 시작 측면, 자연 사행 종료 측면, 마진 경계 앵커. 변형 후보는 시작 레인 위상 0/¼/½/¾ 간격, 시작 측면 양쪽(제외영역 없는 다중 구간 행은 생성기 규칙상 왼쪽만), 종료 측면 양쪽이다.
- 모든 후보는 `TryResolveBoundaryPoint`로 자식의 실제 경계(원본 CAD edge/제외 변/절단선)에서 다시 구한다. 투영 후 좌표가 부모 선택 구간과 마진 적용 영역 안인지 명시적으로 재검증한다(행 허용치는 최대 1 mm, 측면 판정은 기존 5 mm 픽 허용치). 좌표가 같아도 binding identity가 다르면 다른 후보로 유지한다.
- 반대 종료 측면은 생성기의 마지막 추가 레인 규칙으로 실제 경로를 만든다. 그 레인이 영역에 없으면 생성 실패로 후보에서 제외하며, 마지막 레인을 뒤집거나 간격을 바꾸지 않는다. 시작 위상도 한 간격 미만만 옮기므로 레인을 통째로 건너뛰지 않는다.
- `SplitLengthCombinationSolver.TrySolve(lengthsByChild, ...)`가 영역별 후보 중 하나씩 골라 max/min을 최소화한다. 후보 조합을 모두 펼치지 않고, 후보 길이를 하한으로 잡아 각 영역에서 그 이상인 가장 짧은 후보를 고른다. 동률은 최대 길이, 그다음 후보 인덱스(기본 후보 우선) 순이다.
- `MainWindow.TryPrepareBalancedSplit`은 1) 기존 절단 탐색(기본 후보, 96회) 2) 상위 절단 4개에서 변형 조합 평가 3) 최선 변형 주변 절단 국소 탐색(최대 10개 절단 후보) 순으로 진행한다. 변형 생성 횟수 상한은 2,400회다. 목표 1.02, 허용 1.10은 그대로다.
- 실패 문구는 "절단 위치 N개, 인출점 배치 M개 후보를 실제 2D로 생성해 비교했지만..."으로 탐색 범위를 밝히며, 전역 불가능을 단정하지 않는다.
- 검증 2026-11-12: 바탕화면 `발열몰드 저장테스트/Mold_Solids.json`(영역 A, FrameX, margin 5, spacing 15, 3.5 kW, 3분할)을 오프스크린 하네스에서 앱의 `TryPrepareBalancedSplit`으로 재현한 결과 **성공**: 절단 `[-185.611, -42.059]`, 2D 길이 5,314.77 / 5,314.78 / 5,431.15 mm, 비율 **1.0219**, 소요 0.51초. 같은 프로젝트가 절단만 탐색하던 이전 구현에서는 1.1099로 거부됐다. 이어서 SplitActiveArea의 후속 단계(자식별 3D 인출점 검증 → 준비된 패턴 게시 → 단일 `TryCommitAreaRouteCandidates`)를 같은 하네스에서 실행해 3D 인출점 검증과 커밋이 모두 성공했고 표시 경고는 없었다(3D 실측 길이 5,334.62 / 5,343.37 / 5,543.04 mm는 참고값이며 이번 단계의 판정 기준이 아니다). 실제 마우스 클릭은 아니다.
- App 테스트 194 통과(신규 `SplitExitPointPlannerTests` 변형 2개, `SplitLengthCombinationSolverTests` 4개), Core 테스트 260 통과, `tests\WorkingSide.Regression` 전 그룹 통과(총 459 PASS, working-side 181).
- 미검증: 실행 중 앱에서 실제 마우스 분할, 변형 탐색으로 분할한 프로젝트 저장/재열기, 3D 실측 균등, 제외영역/곡면/경사면, 좁은 중간 밴드, 대형 모델 탐색 시간, 분할 영역의 DXF/STEP 출력.

## 자동분할 최대 4개 (사용자 확정 2026-11-12)

- **사용자 요구(원문):** "최대 4분할까지로 하고 그 이상은 오류 반환하자." 한 번의 자동분할 결과 영역 수 상한이며 프로젝트 전체 영역 수 상한이 아니다. 5개 이상이 필요하면 일부만 확정하거나 4개로 깎지 않고 명시적 오류로 거부하며 부모 영역·경로·선택 인출점을 그대로 유지한다.
- `TargetPowerSplitPlanner.MaxParts = 4`가 단일 기준이다. `TryPlan`은 전압으로 계산한 필요 개수가 4를 넘으면 per-part 계산 전에 `plan = null`과 "현재 조건에서는 {n}개 분할이 필요하지만 자동분할은 최대 {MaxParts}개까지 지원합니다"를 반환한다. 전류 초과 여부보다 상한 거부가 우선한다.
- `MainWindow.Split.cs`의 별도 256 상수 검사는 삭제했다. `TryPlan` 실패 상태 문구는 "분할 계획을 세울 수 없습니다 — {planError}. 기존 영역과 경로는 그대로 유지합니다."이며, `CurrentExceedsLimit` 검사는 계속 분할 불가 사유로 남는다.
- `MainWindow.TryPrepareBalancedSplit`은 `partCount < 2` 직후에 같은 상한을 방어적으로 검사하고 "자동분할은 최대 4개 영역까지 지원합니다 — 요청 {n}개."로 거부한다. `partCount`가 2~4일 때 2D 탐색 예산은 모두 96회다(기존 5~8=48/9+=8 분기 삭제). 목표 1.02/허용 1.10, 3D 실측 전기 판정, 공유 전력 배분은 변경하지 않았다.
- 검증 2026-11-12: Core 테스트 271 통과(`FourParts_IsTheSupportedMaximum`: 6,400 W/20,000 mm/Case1 → 4분할·178.885 V; `AboveMaximumParts_IsRejectedWithoutAPlan`: 5개·9개 필요 시 plan null), App 테스트 195 통과(`Search_RejectsPartCountAboveTheSupportedMaximum`: 5분할 요청 거부, 부모 선택점 Y 20/280 유지), App Release/x64 빌드 0 errors, 솔루션 MSBuild 0 errors. `tests\WorkingSide.Regression`은 `x64\Release\OccBridge.dll`을 `x64\WorkingSideBuild\OccBridge.dll`로 복사한 뒤 EXIT=0, 478 PASS — 30 sidebar, 30 area highlight, 22 exclusion+exit, 19 direct exit, 27 vertex edit, 3 electrical, 38 pattern state, 64 geometry rollback, 20 project load, 51 project-load exclusion, 183 working-side. (복사 전에는 WorkingSideBuild의 오래된 DLL 때문에 `StepExportPiece`/`StepExportArea` CS0246으로 하네스 빌드가 실패했다.)
- 미검증: 실행 중 앱에서 실제 마우스 분할, 실제 도면에서 5개 이상이 필요할 때 화면에 표시되는 오류 문구, 4분할 프로젝트 저장/재열기, 4분할 영역의 DXF/STEP 출력.

## Removed test-only code (2026-11-11)

- Deleted from `src\OccBridge`: `ProjectRoutePiece`, `ProjectPathWireOntoSelectedFaces`, `GetFaceUvBoundary`, `SampleUvTo3D`, `GetExitSideDiagnostic`, `GetFaceSelectionRefineStats`, `DebugProbeFaceIdRegion`, `DebugGetLastRectangleBaseFaceIds`, `DebugSetFaceRefineDumpPath`, `SetCameraState` and their native implementations. Their only callers were the deleted `tests\PowerLead.Regression` and `tests\PatternRegion.Repro`. Do not reintroduce a managed API without a product caller.
- `ProjectRoutePiece` the persistence DTO in `MainWindow.Project.cs` is unrelated to the removed native API; the name collision is intentional and the DTO stays.
- Kept deliberately: `GetFaceIdBuildCount` (asserts the product rectangle path never builds a face-ID image), `PickSelectedBoundaryPoint` (product code used by direct exit-point selection), and the legacy exit-face/anchor path (still driven by the visual-test scenario loader). The external power-lead feature (`ProjectPowerLead`, `ClearPowerLead*`, its XAML section and viewport mode) was removed entirely on 2026-11-12 because the user confirmed it is not used.
- `tests\HeatingMold.Application.Tests`, `Design.Tests`, `Infrastructure.Tests`, `Electrical.Tests` held only `bin`/`obj` with no source and were removed.
- `x64\UndoRemovalRegression\UndoRemoval.Regression.csproj` is an ignored scratch harness that links `tests\PowerLead.Regression\Program.cs`; it is now stale and is not part of any build.

## Visual test scenario items removed (user-confirmed 2026-11-11)

- **User requirement: delete every existing visual-test item; keep the visual-test feature itself.** The deleted content is the scenario JSON files and their recorded fixtures/expectations, not the runner, launcher, step actions or report code.
- Deleted 15 files from `tests\HeatingMold.VisualTest\scenarios\`: `basic.json` (v1 fixed 6-step flow) and `01-selection-undo.json` … `14-vertex-edit-discard.json` (v2 action scenarios). The folder stays with a `.gitkeep` so the scenario-name lookup still has a documented home. **2026-11-12에 대표 종합 시나리오 `e2e-main.json`을 새로 추가했다(아래 항목).**
- `VisualTestLauncherWindow` no longer pre-fills a scenario path; the box starts empty, the browse dialog opens at the repository root, and `Run_Click` reports `시나리오 JSON 파일을 선택하세요.` without starting a process. The run-directory default, hold seconds, step mode and the header button are unchanged.
- `tests\HeatingMold.VisualTest\Run.ps1` no longer defaults `-Scenario` to `basic`; a missing `-Scenario` throws before any build or run-directory creation. Name-only lookup (`-Scenario <name>` → `scenarios\<name>.json`) is kept, and an unknown name still fails with `Scenario not found`.
- `docs\VisualTest.Scenarios.md` now documents only the schema contract (v1 fixed steps / v2 areas + steps), the action list, the file map and the judging principles; the 14 scenario definitions, model coordinates, per-area baseline values and the verification-status table were removed. `tests\HeatingMold.VisualTest\README.md` keeps the run/screen/result-file/format documentation, requires an explicit scenario and no longer lists the deleted 7-step validation table. (2026-11-12: the new `e2e-main.json` actions, baseline and project-compare rule were added back to that document.)
- Kept deliberately: `VisualScenario.Load`, `VisualScenarioCompiler`, `MainWindow.VisualTest.cs`, `VisualTestOptions`, `VisualTestReport`, `VisualTestRunner`, and `tests\HeatingMold.App.Tests\VisualTestingTests.cs` (these test the feature contract, not a scenario item; they build their own temporary scenarios).
- Verified 2026-11-11: `HeatingMold.App` builds (Release/x64, isolated `OutDir` because the running app locks the normal output), App tests 175 pass with the OCCT/third-party DLL directories on `PATH`, and running `Run.ps1` without `-Scenario` stops with the explicit message before creating anything. Builds and tests must be redirected to an isolated `-p:OutDir=` while the app is open; the earlier 72 failures were missing OCCT DLLs on `PATH`, not a code regression.
- Not verified: a real click-through of the launcher window in the running app, and running a newly authored scenario end-to-end.

## 대표 시각테스트 (2026-11-12: `e2e-full.json`로 대체)

- **사용자 확정: 시각테스트 전용 제품 로직·다른 방식은 제거하고 실제 제품 로직을 그대로 사용한다.** 테스트는 조작 순서·입력·기대 결과 검증·기록만 담당한다. 실제 마우스/키보드/파일 대화상자 조작은 수동 체크로 분리한다.
- `VisualScenario` v2에 `pickExitPoint`, `checkElectrical`, `splitArea`, `exportStep`, `saveProject`, `loadProject`를 추가했다. `pickExitPoint`는 `exitPoint{role,uv,toleranceMm}`를 쓰고, `splitArea`는 `partCount`(2..4)·`childAliases`·확인 응답을 쓴다.
- 직접 인출점 선택은 `BeginExitPointPick` → `TryResolveExitPoint` → `TryEvaluateSelectedExit3D` → `ApplyPickedExitPoint`의 제품 경로를 공유한다. 뒤의 두 함수는 기존 UI 처리에서 추출한 공통 함수이며, 시각테스트 전용 상태 주입 경로가 아니다.
- `checkElectrical`은 제품 계산기를 다시 호출하지 않고 독립 수식 `R=rL`, `V=√(PR)`, `I=√(P/R)`과 길이비 배분, 한도 `<=`, NG 행 유지, 검토 범위 전환 시 재배분 없음을 검사한다.
- `SplitActiveArea`의 기존 raw 확인 대화상자는 `ConfirmSplit`을 거치며, 시각테스트 실행 중에는 확인 큐(`ResolveVisualTestConfirm("영역 분할")`)를 사용한다. 시나리오에는 `confirm: true`, `confirmAction: "영역 분할"`이 필요하다.
- 모델 계약의 `solidCount` 하한을 0으로 완화했다(`faceCount > 0`, `solidCount >= 0`). 기본 예제는 80면/0솔리드이며 솔리드 수를 위조하지 않는다.
- 대표 시나리오 기준: `resource/Mold_Cover_Trim.step`(sha256 `8CB03405…FCED`), 영역 A faceIds `[44,56,69]`, margin 5/spacing 15/FrameX, 시작 uv `(295.91262149879026,133.36749353890832)`, 종료 uv `(295.9106395319768,-291.6634555349192)`, Case1 3.5 kW/220 V/16 A. 생성 결과 60조각/29턴/1회로/18,015.505 mm, 전기 NG(overLimit 1). 자동 3분할 결과 A1 16조각/5,323.82 mm, A2 16/5,343.181, A3 24/6,009.727, 2D 비율 1.1288, 분할 후 3회로/16.677 m/3,500 W로 OK. 저장→같은 프로세스 재로드 비교 `checkpoint=equal`, 복원 A1 16조각/5,323.82 mm.
- 프로젝트 복원 비교는 저장 시점 JSON과 복원 후 다시 저장한 JSON을 깊은 비교하되, 재가져오기에서 바뀌는 런타임 식별자 `*Utc`, `routeContextKey`, `edgeKey`, `revision`, `modelRevision`, `workingContextId`, `workingContextRevision`, `nativePieceId(s)`는 제외한다. durable `areaId`, `faceId(s)`, topology signature, 경계 parameter, 확정 3D 길이, 전기 입력은 비교한다.
- 검증 2026-11-12: 시각테스트 22단계 e2e-main 통과에 이어, 제외영역·꼭지점 편집·전기 설정/검사 분리·분할 후 STEP·저장/재로드를 포함한 `e2e-full.json` 36단계가 전부 통과했다(`Automation=Completed`, 실패 0). App 테스트 200, Core 테스트 271 통과. WorkingSide 회귀는 EXIT=0으로 전 그룹 통과.
- **분할 후 STEP `area-nofaces:0` 원인 확인·수정(2026-11-12):** 원인은 `BRepFeat_SplitShape`가 주기적/곡면 면을 평면 절단선으로 완전히 분리하지 못해, 한 조각이 두 영역에 걸치는데 경계 표본이 영역 외곽과 일치해 Outside로 오판된 것이다. `BuildAreaColoredModelNative`를 선택 면별 `BRepAlgoAPI_Common(face, areaPrism)`으로 교체해 영역별 정확한 조각을 만들고, 선택되지 않은 면은 원본 그대로 두며, 조각 면적 합이 원본과 1 mm² 또는 1e-4 비율을 넘게 다르면 `area-face-area`로 명시 실패한다. 이 조각들을 compound로 반환한다. 분할 후 STEP과 복원 후 STEP이 모두 성공했고 native read-back 검증도 통과했다. 모델 위상은 셸이 아니라 면 compound가 된다.
- `e2e-full.json` 기준: 초기 60조각/29턴/1회로/18,015.505 mm, NG(overLimit 1). 외곽 코너 제외영역 추가 후 재생성도 동일 60/29/1/18,015.505 mm, 마진 확장 제외영역 침범 0. 초기화 후 재생성도 동일. 꼭지점 그룹 2를 +X 20 mm 이동해 60/29/1/17,979.067 mm, `RouteEdited=true`. 3분할 A1 16/7/1/5,323.82, A2 16/7/1/5,343.181, A3 24/11/1/6,009.727 mm, 분할 후 3회로 OK. 저장→같은 프로세스 재로드 비교 `checkpoint=equal`, 복원 A1 16/7/1/5,323.82 mm.
- 시나리오 계약 추가: `setElectrical`(설정 전용), `checkElectrical` + `electrical.apply=false`(읽기 전용), `electrical.circuitVerdicts[{area,verdict}]`(영역별 회로 판정), `moveVertex.cancel`(미리보기 취소 보존). 프로젝트 복원 비교는 임시 저장 없이 메모리 직렬화로 수행해 제품 경로·dirty 상태를 바꾸지 않는다. 제외영역 검사는 마진 확장 사각형을 기준으로 한다. STEP·프로젝트 산출물은 `Artifacts`에 종류별로 등록된다.
- 꼭지점 편집은 `BeginVertexDrag`/`UpdateVertexPreview`/`CompleteVertexDrag`/`OnVertexDragCancelled`의 제품 공통 경로를 UI와 시각테스트가 함께 쓴다. `_vertexDragGroup` 직접 주입은 제거했다.
- 시각테스트 기본 관찰 시간은 3초에서 **1초**로 변경했다(`VisualTestOptions.HoldSeconds`, 런처 입력란).
- 미검증: 실제 마우스 클릭·드래그·Shift/Ctrl/Enter/Esc·파일 대화상자, 앱 화면 육안 판정, 외부 CAD에서 DXF/STEP 열기, 런처 창 실제 클릭, 새 프로세스에서의 복원, 선택 면/제외 위치가 다른 두 번째 시나리오.

## 두 번째 대표 시나리오: 독립 영역 2개 보존 (사용자 요구·구현 2026-11-12)

- **사용자 요구:** "이제 시각테스트에 두번째 시나리오 추가해보자" → "상세 구현계획세워봐" → "구현시작". 목적은 **서로 겹치지 않는 두 독립 영역(가로·세로)을 만들고, 한 영역의 변경이 다른 영역의 확정 경로를 건드리지 않는지**를 자동 검증하는 것이다. 자동분할 반복은 이 시나리오 범위에서 제외했다.
- 신규 `tests\HeatingMold.VisualTest\scenarios\e2e-multi-area.json`(54단계, `schemaVersion: 2`). 영역 A `faceIds [44,56,69]` margin 5/spacing 15/**FrameX(1)**, 영역 B `faceIds [16,24,26]` margin 5/spacing 20/**FrameY(2)**. 두 영역 모두 방향을 명시해 `Auto`(0) 정규화 모호성을 피했다. 2D 비중첩 확인: A u 최대 295.914, B u 최소 304.366(간격 8.45 mm).
- 인출점: A 시작 `(295.91262149879026, 133.36749353890832)`/종료 `(295.9106395319768, -291.6634555349192)` tol 0.5(기존 검증값 재사용), B 시작 `(310, -172.51)`/종료 `(620, -182.03)` tol 1.5. B는 경계 정점 157개를 실제 측정해 고른 값이다.
- 흐름: A 생성 → B 생성 → A 보존 확인 → 전기 설정/전체 검사 → A 간격 18 초안 변경 → B/A 초안 보존·복원(`expectAreaInputs`) → A 재생성(입력 생략, 복원된 초안 사용) → B 보존 → B 외곽 홈 `[500,240,560,275]` 추가 → `expectNoPattern` → 재생성 → `checkExclusion` → A 보존 → B 간격 방향 `[20,0]` 거부 → B 진행 방향 `[0,15]` 미리보기 취소 → B/A 보존 → 전체·현재B·현재B반복·현재A·전체 전기검사 → 전체/현재 DXF·STEP → 저장·같은 프로세스 재로드(`compareProject`) → `checkAllAreas`·A/B 보존·재출력 → B 삭제 → `checkAllAreas`·A 보존·전기·DXF·STEP.
- 실측 기준값(result.json): A 60조각/29턴/발열선 30/17,323.549 mm/1회로/총 18,015.505 mm · B 32/15/16/8,120.784/1/8,467.619 · **A 간격 18 재생성** 48/23/24/13,742.906/1/14,416.975 · **B 홈 후** 32/15/16/**8,053.498**/1/**8,410.384**(홈은 레인 수를 바꾸지 않고 3개 레인만 줄인다) · 재로드 후 동일 · B 삭제 후 A 48/23/24/14,416.975.
- **전기 판정은 혼합이다.** Case1(4 Ω/m)·공유 3,500 W·ΣL 22.827 m → I≈5.75 A, **B OK(194.689 V/220 V) · A NG(약 414 V, 간격 18 이후 357 V)**. 그래서 `expectedWithinLimits`(모든 회로 동일 기대) 대신 `circuitVerdicts[{area,verdict}]`를 쓴다. B 삭제 후는 1회로 14.417 m, A NG.
- 검증 2026-11-12: `e2e-multi-area` **54/54 OK**를 서로 다른 두 실행(`20261112-210338`, `20261112-211226`)에서 통과했고, `e2e-full` **36/36 OK**(`20261112-211100`)도 유지됐다. 모두 `Status=Completed`, `VisualReview=NotRecorded`. App 테스트 **179 통과**(신규 `VisualRouteSnapshotTests` 9), `tests\WorkingSide.Regression` **EXIT=0**(30 sidebar / 30 area highlight / 19 direct exit / 32 vertex edit / 3 electrical / 43 pattern state / 63 geometry rollback / 20 project load / 51 project-load exclusion / 187 working-side, 총 478 체크, FAIL 0). Core 테스트는 이번 변경이 Core를 건드리지 않아 재실행하지 않았다.
- **영역 경로 보존 검사(신규):** `src\HeatingMold.App\VisualTesting\VisualRouteSnapshot.cs`가 확정 경로를 값으로 깊은 복사한다(영역 ID/이름/색/선택 면/유효 경계/제외/방향/적용 입력·`RouteContextKey`/표시·수동편집/회로 구성/조각별 종류·레인·2D 길이·native ID·확정 3D 길이·2D/3D 좌표·지지 면/시작·종료 인출점과 binding/인출 경계/분할 절단선). `IsUnchanged`는 **엄격 동등**(허용치 없음)으로 비교하고 첫 차이를 `"{영역}: {필드}이(가) 달라졌습니다 (before → after)"`로 보고한다. `TargetPowerW`·전압/전류·활성 영역·초안 텍스트 같은 파생·표시 값은 대상에서 제외한다(경로 변경 시 정상적으로 바뀐다).
- 보존 가드 배선: generate는 거부 시 전 영역, 성공 시 비대상 영역 / addExclusion·removeExclusion·deleteArea·pickExitPoint·moveVertex 확정은 비대상 / moveVertex 거부·취소는 전 영역 / activateArea·checkArea·checkAllAreas·expectAreaInputs·checkElectrical·saveProject·exportDxf·exportStep은 전 영역(읽기 동작이 경로를 바꾸지 않는지). 저장/로드 경계는 런타임 식별자가 바뀌므로 프로젝트 JSON 깊은 비교로 판정한다.
- **검사기 결함 수정:** `VisualCheckElectrical`이 `BuildReviewSnapshot()`(현재 범위 결과)을 전체 범위로 착각해, 현재 범위에서 `apply:false` 연속 검사 시 총 배분을 3.5 kW와 비교해 오판했다. `CheckElectricalCore(spec, entryScope)`로 분리하고 전체 기준자료는 항상 `SetVisualReviewScope(0)` 후 얻으며, 예외 시 원래 범위로 복원하고 마지막에 요청 범위로 전환한다.
- **발열선 전용 지표 추가:** `VisualAreaExpect`에 `heatingPieces`·`heatingLengthMm`(HeatingRun만, 커넥터 제외)을 넣었다. `checkExclusion`의 기존 `heatingPieces`는 HeatingRun+FixedConnector 합이라 heating-only로 쓰면 안 된다. B 홈은 총길이(8,467.619→8,410.384)와 발열선 길이(8,120.784→8,053.498)가 함께 줄지만, 커넥터가 제외를 지날 수 있는 경우 총길이만으로 효과를 판정할 수 없다.
- 꼭지점 미리보기 취소 단계는 `UpdateVertexPreview` 뒤 상태 문구에 `VertexPreviewShownFragment`("표시용 2D 미리보기", `MainWindow.VertexEdit.cs`)가 실제로 표시됐는지 확인한 다음 취소한다.
- **출력 범위 검사:** DXF는 재읽은 파일에서 `HEAT_WIRE` 폴리라인 수(=확정 조각 수)·`HEAT_AREA` 링 수(=영역 수)·영역 색 집합·`ELECTRICAL_SPEC` 표의 Y그룹별 회로 ID와 반올림 수치(F2/F1)·합계·**범위 밖 회로 ID 부재**를 검사한다. STEP은 요청 범위의 영역 수·조각 수·영역명 집합을 제품 수집 결과와 대조하고, 파일 자체 검증은 기존 native 재읽기(XDE 파트/색/기하)에 맡긴다. 전체/현재 출력 지표: 전체 DXF areas 2/rows 2/wires 80/rings 2, 전체 STEP areas 2/pieces 80, 현재 영역 DXF·STEP areas 1/wires·pieces 48, 삭제 후 1/48.
- **프로젝트 재로드 결함 수정:** `MainWindow.ProjectLoad.cs`의 경로 복원이 기하·색·길이 라벨만 복원하고 **인출점 마커를 설정하지 않아** 재로드 후 `checkAllAreas`가 "발열경로 끝점 마커 수가 영역 수와 다릅니다"로 실패했다. `ApplyAreaColors()` 뒤에 `PrepareCommittedRoutePresentation`(커밋 경로와 같은 추출 규칙)으로 모아 `Bridge.SetRouteEndpointsNoRedraw`를 호출한다. 실패는 `_routePresentationWarning`으로만 남기고 기하 복원을 되돌리지 않는다.
- **STEP 영역 색 면적 검사 수정:** 선택 면마다 `BRepAlgoAPI_Common(face, prism)` 조각 면적 합을 원본 면적과 동등 비교하던 검사는 "선택 면 전체가 영역 안에 있다"는 가정이라, 제외영역이 면을 가르면 정당하게 일부만 색칠되어 실패했다(면 16: 21,731.25→21,043.98 mm², 손실 687.27). 이제 경계 표본이 기여 영역의 어느 링에도 속하지 않으면 `clipped`으로 보고 `pieceArea > originalArea + max(1.0, 1e-4·원본)`일 때만 실패하며, 완전 포함 면은 기존 엄격 동등 검사를 유지한다. 전제는 한 STEP 안의 모든 영역이 하나의 확정 작업평면을 공유한다는 것이다.
- **빌드 잔재 함정(중요):** `src\HeatingMold.App\x64\WorkingSideBuild\OccBridge.dll`(과거 빌드 산출물, `.gitignore`의 `x64/`로 무시)이 남아 있으면 App 빌드가 이 낡은 DLL을 앱 출력으로 복사해 네이티브 수정이 반영되지 않는다. 그 디렉터리는 삭제했다. 네이티브 변경 후에는 `x64\Release\OccBridge.dll`을 앱 `bin\x64\Release\net8.0-windows\`로 복사하고 `-NoBuild`로 실행한다.
- 미검증: 실제 마우스 분할·드래그·화면 육안 판정, 새 프로세스에서의 복원(현재는 같은 실행 내 재로드), 외부 CAD에서 DXF/STEP 열기, 레인 수 자체가 바뀌는 제외영역, 곡면·경사면에서의 `clipped` 판정, 세 번째 면/인출점 조합.


## 세 번째 대표 시나리오: 마진·간격·인출점 변형 (사용자 요구·구현 2026-11-12)

- **사용자 요구(원문):** "그 다음 시나리오 3 작성해보자. 이번에는 마진/발열사간간격/ 인출점 위치도 기존과 다르게 구현해야해". 즉 `e2e-multi-area`가 검증한 기본 조건(마진 5 mm, 간격 15/20 mm, 오른쪽·아래쪽 인출)에 의존하지 않는 대조군을 만든다.
- 신규 `tests\HeatingMold.VisualTest\scenarios\e2e-margin-spacing-exit.json`(58 actions + implicit loadModel = **59단계**, `schemaVersion: 2`). 영역 A `faceIds [44,56,69]` **margin 8 / spacing 24 / FrameX(1) / 왼쪽 변 인출**(start `(-362.7109,-300)`, end `(-362.7109,-36)`, tol 0.5), 영역 B `faceIds [16,24,26]` **margin 12 / spacing 28 / FrameY(2) / 위쪽 변 인출**(start `(600,252.3764)`, end `(370,217.95)` 경사 위쪽 변, tol 1.0). 두 영역 2D 비중첩은 기존과 동일(A u 최대 295.914 < B u 최소 304.366).
- 실측 기준값(result.json): A **24조각/11턴/발열선 12/7,742.326 mm/1회로/총 8,030.735 mm**, B **20/9/10/4,907.115/1/5,239.919**. 전체 44조각/20턴/2회로/13,270.654 mm. **B 하단 홈 `[520,-200,590,-166]` 후** B 20/9/10/**4,841.326**/1/**5,174.13**(마진 12 확장 `(508,-212)-(602,-154)`, 침범 0). A 초안 10/30 변경 후 원 조건(8/24/1) 재생성은 초기와 완전 동일(결정적).
- **꼭지점 방향 규칙이 기존과 반대다.** A는 FrameX라 간격 방향(Y) `[0,20]` 이동이 no-op으로 거부되고, 진행 방향(X) `[15,0]` 미리보기는 "표시용 2D 미리보기"를 표시한 뒤 취소된다(`from (287.91,-276)` → expected `(302.91,-276)`).
- **전기 판정:** 표준 Case1(4 Ω/m·3.5 kW 공유·220 V·16 A)에서 두 영역 **A NG·B OK**(13.271 m). 허용 전류 5 A로 낮추면 **A NG·B NG**(13.205 m). 16 A 복원 시 A NG·B OK. **A 삭제 후 B 단독**은 5.174 m에 3.5 kW 전부를 배분받아 **B가 OK→NG로 바뀐다**(전압 초과). 영역 삭제가 공유 전력 재배분을 일으키는 것을 판정으로 확인하는 사례다.
- **출력·저장:** 전체 DXF areas 2/rows 2/wires 44/rings 2, 전체 STEP areas 2/pieces 44/"A,B". 현재 B DXF areas 1/wires 20, STEP areas 1/pieces 20. 저장 `variant.json` 811,584 B → 같은 실행 재로드 `checkpoint=equal`, 활성 B, 복원 44/20/2/13,204.865. A 삭제 후 DXF 1/20/1, STEP 1/20.
- **`apply:false` 전기검사 함정:** 시나리오 DTO 기본값(16 A)을 생략하면 제품 상태(5 A)와 비교해 "제품에 적용된 허용 전류가 시나리오와 다릅니다"로 실패한다. `apply:false` 검사에도 `voltageV`/`allowedCurrentA`/`allowedPowerKw`를 제품 현재값과 똑같이 명시해야 한다.
- **STEP 발열사 스윕 결함 발견·수정(제품 코드):** 이 시나리오의 A 13번째 조각(33점·2D 642.62 mm·3D 최대 꺾임 0.086°의 거의 직선인 긴 중심선)에서 `BRepOffsetAPI_MakePipeShell`의 보정 프레임이 실패해 `area-face-area`가 아니라 `조각 13의 발열사·발열경로 형상을 만들지 못했습니다(sweep)`로 STEP 전체가 거부됐다. `src\OccBridge\StepExportGeometryNative.inc`에 `MakeSweptSolidNative(spine, profile, frenet, wire)` 헬퍼를 두고 **보정 프레임 실패 시 Frenet 프레임으로 한 번만 재시도**한다. 중심선·반지름·SOLID 1개·valid·volume>0 검사는 그대로이며, 같은 중심선을 근사·분할하거나 기대값을 낮추지 않는다. 이 fallback이 없는 이전 DLL에서는 재현되고, 수정 후 A 단독 STEP과 전체 시나리오 STEP이 통과한다.
- **검증 2026-11-12:** 새 시나리오 **59/59 OK**를 서로 다른 두 실행(`artifacts\visual-test\20261112-225316`, `20261112-225450`)에서 통과. 기존 `e2e-full` **36/36 OK**(`20261112-225619`), `e2e-multi-area` **54/54 OK**(`20261112-225731`)도 유지. 모두 `Status=Completed`, `VisualReview=NotRecorded`. App 테스트 **180 통과**, `tests\WorkingSide.Regression` **EXIT=0, 485 PASS, FAIL 0**. Core 테스트는 Core를 건드리지 않아 재실행하지 않았다.
- 문서 갱신: `docs\VisualTest.Scenarios.md`에 §4.3(조건 변형 시나리오)과 세 시나리오 안내를, `tests\HeatingMold.VisualTest\README.md`에 세 번째 시나리오 실행 예를 추가했다.
- 미검증: 실제 마우스·화면 육안 판정, 새 프로세스에서의 복원(현재는 같은 실행 내 재로드), 외부 CAD에서 이 조건의 DXF/STEP 열기, Frenet fallback이 필요한 다른 형상(곡률이 큰 중심선·급격한 꺾임), 마진 12 mm에서 레인 수 자체가 바뀌는 제외영역, 네 번째 면·인출점 조합.


## 시각테스트 완료 후 종료 확인 예외 (원인·수정 2026-11-12)

- **사용자 보고:** 두 번째 시나리오(`e2e-multi-area`)를 실행하고 창을 닫으면 `예상하지 않은 확인 대화상자 요청입니다: 앱 종료`(`ResolveVisualTestConfirm`) 예외가 발생했다.
- **원인:** 확인 응답 큐는 실행이 끝날 때 `OnVisualTestFinished`가 비우지만, 세션(`VisualTestSession.Current`)은 육안 검토용으로 남겨 둔다. 종료 확인 경로(앱 종료, 경로 편집 삭제, 영역 분할)는 "세션 존재"만 보고 큐를 사용하므로, 실행 완료 후 빈 큐에서 Dequeue를 시도해 예외가 난다. 해당 시나리오는 저장 이후 B 영역을 삭제하므로 종료 시점의 미저장 변경은 정상 상태다.
- **수정:** 신규 `src\HeatingMold.App\VisualTesting\VisualTestConfirmationPolicy.cs`의 `UsesQueuedResponses(sessionExists, runner)`가 **세션이 있고 실행기가 아직 끝나지 않았을 때만** 큐 사용을 승인한다. `MainWindow.VisualTest.cs`의 `VisualTestAutomationActive` 프로퍼티가 이 판정을 캡슐화하고, `ConfirmDiscardRouteEdits`, `ConfirmDiscardSession`, `ConfirmSplit`이 `VisualTestSession.Current != null` 대신 이 프로퍼티를 쓴다.
- **자동 실행 중 창 닫기:** `OnClosing`은 자동 실행 중이면 닫기를 거부하고 상태 문구 `시각 테스트 실행 중입니다 — 완료된 뒤에 종료할 수 있습니다`만 표시한다. 단계가 큐에 넣은 응답을 종료 확인이 가로채는 것도 막는다. 예외를 무시하거나 종료를 자동 승인하지 않는다.
- **실행 종료 후 종료:** 세션은 남지만 정책이 false이므로 제품 대화상자(저장하지 않은 작업: 예/아니요/취소)가 뜬다. '아니요'면 미저장 변경을 버리고 정상 종료한다.
- **검증 2026-11-12:** App 테스트 **180 통과**(신규 `ConfirmationPolicy_UsesQueueOnlyWhileTheRunIsActive`: 세션 없음·실행기 없음·실행 중·완료 후 네 경우), App Release/x64 빌드 0 errors(기존 CS8632 경고만). `tests\WorkingSide.Regression\Run.ps1` EXIT=0, **478 PASS**(30 sidebar, 30 area highlight, 19 direct exit, 32 vertex edit, 3 electrical, 43 pattern state, 63 geometry rollback, 20 project load, 51 project-load exclusion, 187 working-side). `e2e-multi-area`를 앱 출력 사본으로 `--visual-hold-seconds 1`에서 재실행해 **54/54 OK, Status=Completed**를 확인하고, 이어서 주 창에 WM_CLOSE를 보내 `저장하지 않은 작업` 대화상자가 표시되고 '아니요' 선택으로 exit 0 종료됨을 확인했다. 실행 중(8초 후) WM_CLOSE는 거부되어 창이 살아 있었고 대화상자도 없었다.
- **미검증:** 실행 중 앱에서 사람이 실제 마우스로 종료를 클릭하는 경우, 경로 편집 삭제·영역 분할 확인의 실행 완료 후 동작(정책 공유만 확인), 다른 시나리오(`e2e-full`) 종료 시 동작. 위 종료 검증은 실제 클릭이 아니라 Win32 WM_CLOSE 메시지 기반 자동 확인이다.


## legacy-cleanup 통합 (2026-11-12)

- **사용자 요청:** 다른 브랜치 `legacy-cleanup`(불필요 코드 삭제)을 지금 병합해도 문제가 없는지 검토하고, 승인 후 구현. 검토 결과 즉시 병합은 보류하고 현재 시각테스트 작업을 먼저 커밋한 뒤 통합 브랜치에서 병합했다.
- 통합 브랜치 `integration/legacy-cleanup`에서 `legacy-cleanup`(tip `ec72c01`)을 병합했다. 텍스트 충돌은 `src\HeatingMold.App\VisualTesting\VisualScenario.cs` 1개였다. 해결 원칙은 삭제된 구형 `Exit`/`Anchor` 입력은 제거하고 최신 `Cancel`·`ExitPoint`·`Electrical`·`PartCount`·`ChildAliases`·`CompareProject`는 유지하는 것이다.
- 자동 병합된 `MainWindow.VisualTest.cs`, `MainWindow.VertexEdit.cs`, `OccBridgeApi.cpp`, `VisualTestingTests.cs`는 삭제된 구형 심볼(`_exitCandidates`, `AnchorCandidates`, `StepExportCircuit`, `ProjectElectricalMigration`, 구 generator API 등)을 참조하지 않음을 grep으로 확인했고, 최신 액션·공통 드래그·STEP piece API가 남아 있음을 확인했다.
- **주의:** 현재 `e2e-full.json` 시나리오가 `before-split.dxf` 등을 두 번 이상 출력하지 않는 것처럼, 저장/출력 대상이 이미 있으면 거부하는 제품 규칙은 그대로다. 새 실행은 항상 새 run 폴더를 쓴다.
- **전기 제원 검증 복구:** 구형 마이그레이션 삭제로 schema 6 파일의 프리셋/직경/저항 일치 검증이 사라졌으므로 `src\HeatingMold.App\Persistence\ProjectElectricalValidator.cs`를 추가했다. 검증 항목은 전기 설정 존재, `WirePresetIndex` 0/1, 저장된 직경·단위 저항의 양수 파싱과 선택 Case 값 일치다. 손상/불일치는 `ValidateProjectDocument`에서 네이티브 모델 교체 전에 `InvalidDataException`으로 거부한다. 빈 허용 전압/전류/전력 입력은 제품의 입력 오류 상태이므로 채우거나 거부하지 않는다.
- `ApplyProjectElectrical`은 검증된 `WirePresetIndex`를 그대로 적용한다(더 이상 잘못된 인덱스를 조용히 Case 1로 바꾸지 않는다).
- **STEP 리소스 줄바꿈 함정:** `core.autocrlf=true` 환경에서 `resource\*.step`을 체크아웃하면 CRLF로 변환되어 SHA-256이 달라진다(예: `Mold_Cover_Trim.step`이 1,612,847 B / `2CE884…`). 시나리오·프로젝트가 고정한 값은 LF 원본 1,581,412 B / `8CB03405…FCED`이므로, `.gitattributes`에 `resource/*.step -text`, `resource/*.stp -text`를 추가해 앞으로의 체크아웃이 원본 바이트를 유지하게 했다. 기존 체크아웃은 파일 삭제 후 `git checkout --`로 다시 받아야 한다.
- 검증 2026-11-12(통합 트리): 네이티브를 `src\OccBridge\OccBridge.vcxproj`에서 `/p:OutDir=<repo>\x64\Release\` 절대경로로 빌드하고, 솔루션을 `/p:OutDir=<repo>\x64\WorkingSideBuild\`로 빌드해 0 errors. App 테스트 **170 통과**, Core 테스트 **226 통과**(신규 `ProjectElectricalValidatorTests` 포함). `tests\WorkingSide.Regression\Run.ps1`은 EXIT=0으로 전 그룹 통과(30 area highlight, 19 direct exit, 29 vertex edit, 3 electrical, 43 pattern state, 63 geometry rollback, 20 project load, 51 project load exclusion, 187 working-side 등).
- `Run.ps1`은 솔루션 빌드가 격리 OutDir에 오래된 네이티브 중간 결과를 재사용할 수 있어, 방금 `x64\Release`에 빌드한 `OccBridge.dll`을 `x64\WorkingSideBuild`로 복사하는 단계를 추가했다(2026-11-12). 이 복사가 없으면 회귀 하네스가 이전 시그니처를 참조해 `BeginProjectRestoreCandidate(string,int)`/`StepExportArea` CS 오류로 빌드에 실패했다.
- 통합 앱으로 `e2e-full.json`을 새 run 폴더에서 실행해 **36/36 성공**, `Automation=Completed`, `VisualReview=NotRecorded`, 오류 없음. 초기 60조각/18,015.505 mm, 꼭지점 편집 후 17,979.067 mm, 3분할 A1/A2/A3 5,323.82/5,343.181/6,009.727 mm, 2D 비율 1.1288, 분할 후·복원 후 STEP과 DXF, 프로젝트 `checkpoint=equal`을 확인했다.
- 미검증: 실제 마우스·키보드·파일 대화상자, 앱 화면 육안 판정, 외부 CAD에서 DXF/STEP 열기, 새 프로세스에서의 복원(현재 `loadProject`는 같은 프로세스), 선택 면/제외 위치가 다른 두 번째 시나리오.

## Standard view menu removed (user-confirmed 2026-11-11)

- **User requirement:** "표준뷰 항목도 삭제해줘". Scope confirmed with the user: remove the header button and its menu entirely, not just the six direction items.
- Removed from `MainWindow.xaml`: the `StandardViewButton` dropdown ("표준 뷰 ▾") and its six items (`정면 (−Z)`, `후면 (+Z)`, `위쪽 (−Y)`, `아래쪽 (+Y)`, `좌측 (−X)`, `우측 (+X)`). Removed from `MainWindow.xaml.cs`: `StandardViewMenu_Click` and `StandardView_Click`.
- Kept deliberately: `Bridge.SetViewFrame` (still called by `tests\WorkingSide.Regression\Program.cs`), `WorkingFront_Click` ("작업 정면으로 돌아가기", a separate product feature), and the vertex-edit guard text that only the removed handler used. `Bridge.GetAutoAxis` and its native implementation were removed on 2026-11-12 after the user confirmed there are no external consumers.
- No other entry point sets a standard view; the app now changes the view only through the viewport itself and the working-front button.
- Verified 2026-11-11: `HeatingMold.App` builds Release/x64 with 0 errors, App tests 175 pass (isolated `-p:OutDir=` and OCCT DLLs on `PATH`).
- Not verified: a real click-through of the header bar in the running app; `tests\WorkingSide.Regression` was not rerun (it does not call the removed handlers).

## Build configuration (verified)

- `HeatingMold.Mvp.sln` declares exactly one solution configuration, `Release|x64`. Do not re-add Debug/Any CPU/x86 rows without verifying every project exposes them.
- The right-hand side of each solution→project mapping must be a configuration the project actually exposes:
  - `src\OccBridge\OccBridge.vcxproj` defines only `Release|x64` → map to `Release|x64`.
  - `src\HeatingMold.App\HeatingMold.App.csproj` and `tests\HeatingMold.App.Tests\HeatingMold.App.Tests.csproj` declare `<Platforms>x64</Platforms>` → map to `Release|x64`.
  - `src\HeatingMold.Core`, `src\HeatingMold.Export`, `tests\HeatingMold.Core.Tests` declare no `<Platforms>` → map to `Release|Any CPU`.
- A mapping to a configuration the project does not expose produces VS's "unknown project configuration mapping" dialog; `dotnet build`/`msbuild` on the solution can still succeed, so only reopening in VS validates this.
- **Native output trap (verified 2026-11-12):** building `src\OccBridge\OccBridge.vcxproj` without `OutDir` writes `src\OccBridge\x64\Release\OccBridge.dll`, but `HeatingMold.App.csproj` references `x64\Release\OccBridge.dll`. Building only the native project therefore leaves the app and visual tests on the previous DLL; the split-after STEP check then fails with `area-nofaces:0` even though the fix is present. Always build native with `/p:OutDir=x64\Release\` (as `Run.ps1` does) and rebuild/copy to the app's `bin\x64\Release\net8.0-windows` output before running the app or launcher.

## Project persistence (save + load implemented)

- The product project file is one UTF-8 JSON document that references the external original STEP by relative path and SHA-256; CAD/BREP geometry is not embedded.
- Save uses a same-directory temporary file, flush, and atomic replace. A save failure must preserve the current work and the previous JSON file; never overwrite the source STEP.
- Project load verifies the JSON format/schema, the source STEP length and SHA-256, the import policy version, and the saved topology identity before restoring. It may ask the user to reselect the original STEP but never substitutes a different hash.
- Load prepares an isolated native STEP candidate, remaps saved Face/Edge signatures to fresh runtime IDs, commits the candidate once, restores the exact saved working-plane frame, and rebuilds areas, boundaries, committed 3D route chains, lengths, endpoint provenance, electrical inputs, and active area.
- Committed routes are staged with their saved support-face provenance and published with one native route commit. Missing support-face provenance is an explicit load failure; do not approximate it from nearest surfaces.
- Load rebuilds the effective heating-area boundary from the saved exclusion rectangles (applied region minus exclusions, before generation margin). With no separate exit face (`ExitFaceId < 0`) the exit boundary uses that same effective outline; a saved exit-face boundary is kept separately. Saved selected, pattern, and circuit exit points are then evaluated against the restored boundary using their saved CAD/artificial provenance, and the committed route mapping is captured only afterwards. The route boundary follows the saved committed context's recorded mode (an explicit saved flag, with legacy inference from its selected exits), not whatever draft selections exist at load time. An unsupported exclusion or invalid saved endpoint is an explicit load failure; never silently clear the point or fall back to the pre-exclusion outline.
- Failure before the native candidate commit preserves the current document. A failure after that commit cannot restore the previous native model; the load reports the failure and clears the incomplete restored document explicitly.
- Do not persist native pointers, temporary stage IDs, AIS/presentation objects, caches, hover/preview state, or runtime-only FACE/Edge IDs as durable identity.

## 프로젝트 로드 후 작업 정면 뷰 정렬 (사용자 확정 2026-11-12)

- **사용자 요구:** 프로젝트를 열면 저장된 작업면을 화면 정면으로 자동 정렬한다. 모델 열기(`LoadFile` → `ApplyWorkingSide`)가 이미 하는 동작과 같다.
- 구현: `MainWindow.LoadProject`가 복원된 작업 컨텍스트를 `RestoreConfirmed`로 확정한 직후 `_prepareWorkingFront(context.Id, context.Revision)`(= `Bridge.ReturnToWorkingFront`)를 한 번 호출한다. 이 호출은 직교 투영 설정 + `SetProj`/`SetUp`(작업 법선/프레임 Y) + `FitAll`이며 표시 전용이다.
- 뷰 정렬 실패는 기하/경로 복원을 되돌리지 않는다. 예외나 `false` 반환은 완료 상태 문구에 `· 경고: 작업 정면 뷰를 정렬하지 못했습니다`로만 덧붙인다.
- 검증 2026-11-12: App Release/x64 빌드 0 errors(기존 CS8632 경고 7건). `tests\WorkingSide.Regression` 전 그룹 통과 — project load 20건(신규 `project load aligns the view to the restored working front`: 뷰 준비 호출이 복원된 컨텍스트 ID/리비전으로 정확히 1회 실행됨), project load exclusion 31건, working-side 181건, 나머지 그룹 동일.
- 미검증: 실행 중 앱에서 실제 프로젝트 열기 시 화면이 작업 정면으로 보이는지. 카메라 상태를 읽는 네이티브 API가 없어 자동 확인은 뷰 준비 호출 여부까지만 검증한다.

## Topology identity (implemented for load)

- Saved FACE/Edge runtime IDs are not durable. Projects store per-Face and per-Edge topology signatures and remap them to fresh runtime IDs after reimport; raw IDs are diagnostic provenance only.
- An edge signature includes its adjacent Face signatures. Face/edge signatures must be nonempty, unique, and byte-stable across reimport of the same STEP, hash, and import policy. Missing, duplicate, ambiguous, or changed signatures are explicit load-time failures.
- Every exit-edge copy must preserve `EdgeSignature`; never reconstruct a saved binding by nearest XY or nearest-edge matching.
- The exact saved working frame and per-point route support-face provenance are persisted and restored; raw runtime IDs are rebased.

## Circuit generation contract

- A circuit is one continuous serpentine path chunk made of multiple lanes joined by connectors. One straight lane fragment, or one area split by an exclusion, is not automatically a separate circuit.
- Use one sequential generation rule whether or not exclusion regions exist. An exclusion removes heating lanes only; connectors between lanes and across same-lane cuts may pass through the margin-expanded exclusion region. Never split a circuit, detour, or reject generation merely because a connector crosses an exclusion.
- Heating runs must stay outside the margin-expanded exclusion region. A lead added to reach a selected exit is a connector, not a restored heating run.
- Never reverse only the last lane to face the selected exit. Keep the established pitch and natural serpentine alternation. If the natural last heating run ends on the near side of the selected end, connect it directly; if it ends on the far side, generate exactly one further lane at the same pitch following normal progression, then connect back to the exact selected end. That finishing lane may pass the selected end's sweep coordinate. Any heating interval at that exact row is used even when short; only a row with no heating interval at all is an explicit generation failure that preserves the existing result. Final heating runs stay axis-parallel; the terminal lead is a separate real conductor connector, never an endpoint replacement or a retagged heating run.
- Do not reintroduce fixed generation-count caps into the product generation path: lane count, split/fragment count, total interval count, the 20-node exact-search limit, the 4000-fragment fallback, or a 100000-lane loop bound. End lane generation from the actual sweep range and spacing. Numerical exhaustion or resource failure must be explicit; never return a partial route as success.
- A generated area is exactly one continuous circuit covering every route piece in its original order. Preserve the single `Pattern` + `Circuits`/`CircuitPieceIndices` structure, original CAD endpoint binding, surface mapping, actual 3D length, and atomic commit policies. Do not reintroduce a turn-enabled split/merge state.
- Multiple confirmed areas may reference the same CAD Face. Their effective heating regions (`applied region − exclusions`, before generation margin) are a user input precondition: they must not overlap. Do not add automatic overlap rejection, region allocation, detours, or partial-region drafting tools.
- Connector exclusion penetration is not a reason to reclassify a conductor as a dashed guide or to change its length semantics.
- The legacy compatibility APIs `GenerateComponents`, `ComputeCandidatesComponents`, and `GenerateAnchoredComponents` still use the exact DP/candidate path. Their limits are not the product policy for the selected-exit generation path; do not copy those limits into new generation code.

## 꼭지점 편집의 발열사 간격 유지 (사용자 확정, 2026-11-12 복원)

- **사용자 요구:** 발열경로를 수동으로 수정할 때 미리 설정한 발열사 간격(레인 피치)이 유지되어야 한다.
- 이력: `0901c5e`가 간격 고정을 추가했고, 자식 커밋 `c12b595`가 이를 전부 되돌렸다(간격 유지가 동작하지 않던 원인). 2026-11-12에 같은 규칙을 복원했다.
- `RouteVertexMoveConstraint.TryConstrain(Vec2 original, Vec2 requested, double angleDeg, out Vec2 constrained, out string failure)`(Core)가 변위의 진행 방향 성분만 남기고 수직 성분을 버린다. 축 정렬 방향은 `AxisTolerance = 1e-12`로 잠긴 좌표를 그대로 복사하고, 비유한 입력은 한국어 실패 문구로 명시적으로 거부한다. 생성 방향의 정본은 확정된 `SerpentineResult.AngleDeg`이며, 가로 생성이면 X, 세로 생성이면 Y만 움직인다.
- 앱 경로(`MainWindow.VertexEdit.cs`)는 드래그 미리보기(`UpdateVertexPreview`→`TryBuildVertexPreview2D`), 후보 생성(`TryBuildVertexCandidate`), 확정(`CommitVertex`)에서 같은 제약을 다시 계산한다. 미리보기와 커밋이 서로 다른 좌표를 쓰지 않도록 하는 것이 목적이며, 확정 단계에서 제약을 생략하지 않는다.
- 제약 결과가 원래 점과 `VertexMoveEpsilon = 1e-6` 이내면 패턴 재생성도 Undo 기록도 만들지 않고 `VertexSpacingLockedMessage`("발열사 간격 유지 — 생성 방향과 수직인 이동은 적용되지 않았습니다")만 표시한다. 시작점/종료점은 종전대로 전용 선택 버튼으로만 바꾼다(제약 대상이 아님).
- `SerpentineVertexEdit.ApplyVertexEdits`는 `CandidateId`를 결과에 그대로 넘긴다. 커밋 시 `CaptureAppliedInputs`가 `BuildRouteContextKey`로 입력 동일성을 판정하므로, 이 값을 잃으면 편집 직후 경로가 stale로 오판된다.
- UI 안내 문구(`MainWindow.xaml`의 `VertexEditModeBox`)와 `docs\VertexEditing.md`는 "발열 진행 방향으로만 이동(가로 생성=좌우, 세로 생성=상하) → 발열사 간격 유지"를 명시한다. 굽힘 반경·자기교차·제조성 검증은 여전히 비범위다.
- 검증 2026-11-12: Core 테스트 268 통과(신규 `RouteVertexMoveConstraintTests` 7개, `ApplyVertexEdits_PreservesTheCandidateIdentity`), App 테스트 194 통과, App Release/x64 빌드 0 errors(변경 파일 관련 경고 없음). `tests\WorkingSide.Regression` 전 그룹 통과 — 30 sidebar, 30 area highlight, 22 exclusion+exit, 19 direct exit, 27 vertex edit, 3 electrical, 38 pattern state, 64 geometry rollback, 19 project load, 50 project-load exclusion, 181 working-side. 회귀 픽스처는 실제 평행 2레인 경로(피치 20 mm)로 되돌려, 수직 드래그가 no-op이 되는지와 진행 방향 드래그가 이웃 레인 Y를 바꾸지 않는지를 함께 확인한다.
- 회귀 하네스가 참조하는 `x64\WorkingSideBuild\OccBridge.dll`이 오래되면 한글 경로 STEP 내보내기 검사가 실패한다. 네이티브 변경 후에는 `x64\Release\OccBridge.dll`을 복사한 뒤 회귀 프로젝트를 다시 빌드해야 한다(기존 문서화된 함정과 동일).
- 미검증: 실행 중 앱에서 실제 마우스 드래그, 화면 안내 문구 렌더링, 시나리오 JSON이 모두 삭제된 상태에서 visual-test `moveVertex` 액션의 종단 실행.

## 꼭지점 편집 실시간화 (사용자 요구·구현 2026-11-12)

- **사용자 요구 원문:** "현재 꼭지점 수정 기능이 너무 느림 거의 실시간에 가깝게 구현하고 싶은데 방법 찾아봐". 구현은 드래그 미리보기와 MouseUp 확정을 분리하는 방향으로 진행했다. 발열사 간격 유지·표면 검증·원자적 커밋 규칙은 그대로다.
- **드래그 미리보기는 표시 전용 2D 폴리라인이다.** `MainWindow.VertexEdit.cs`의 `UpdateVertexPreview`가 `TryBuildVertexPreview2D`로 확정 작업평면 u,v 점만 만들고, `ShowVertexPreview2D`가 `Bridge.ReplaceVertexPreview2D(workingId, revision, uv, offsets, invalid)`를 한 번 호출한 뒤 `RedrawView`를 한 번 호출한다. 드래그 중에는 `CommittedRouteContext.Capture`, `BuildSurfaceMapDelegate`, `SurfacePathMapper.TryMapRoute`, `PrepareDisplayedEditPiece`, 조각별 `AIS_Shape` 생성이 전혀 실행되지 않는다.
- 네이티브 `WorkingSideNative.inc`의 `VertexPreview2DNative`(AIS_InteractiveObject 상속)는 `Graphic3d_ArrayOfPolylines`로 폴리라인을 그리고 `Topmost` 레이어에 고정한다. 객체는 최초 1회만 `Display`하고 이후에는 `Redisplay`만 하므로, 드래그 중 BRep 형상을 다시 만들지 않는다. `ComputeSelection`은 비어 있어 선택되지 않는다. 입력 검증(offsets 단조 증가·각 구간 2점 이상·uv 유한·개수 일치, context/revision 일치)에 실패하면 이전 미리보기를 유지하고 `false`를 반환한다.
- 미리보기 선은 **실제 모델 표면 위의 점이 아니며 검증되지 않았다.** 길이·전기 계산·저장·DXF/STEP 출력·strict 표본 캐시 승격에 절대 쓰지 않는다. `ClearVertexPreview2D`는 `ExitVertexEdit`, `BeginVertexDrag`, `CompleteVertexDrag`, `OnVertexDragCancelled`와 `ClearRoutePiecesNative`(따라서 overlay 전체 clear)에서 호출된다.
- **확정은 영역 단위 전체 교체가 아니라 조각 단위 유지(retain)를 쓴다.** `PrepareCandidateRouteGeometry`는 target과 candidate의 `BuildRouteContextKey` 접두부(`WorkingArea.RouteRetainContextKey`), 회로 구성, 조각의 종류/레인/2D 좌표가 **비트 단위로 동일**할 때 그 조각을 다시 스테이징하지 않고 `retainedIds`로 넘긴다. 유지 조각은 이전 `ProjectedPieces`/`RoutePieceLengths`/native ID를 그대로 쓰고, `SetConnectorEnabled`도 다시 호출하지 않는다. 조각 간 연결 검사(`RouteJoinTolerance` 0.1 mm)는 유지 조각에도 그대로 수행한다.
- 재사용 증빙은 이번 세션에서 실제 커밋까지 성공한 영역에만 존재한다. `RouteRetainContextKey`는 프로젝트에 저장하지 않으므로 **로드 직후 첫 편집은 기존 전체 스테이징 경로**를 쓰고, 그 커밋이 성공한 다음부터 retain이 가능하다. 증빙이 하나라도 어긋나면 자동으로 전체 스테이징으로 돌아간다.
- 네이티브 `CommitWorkingGeometryDelta`는 retained가 현재 커밋된 ID이고 final과 겹치지 않으며 기존 커밋 조각 전체를 retain∪replace∪delete가 정확히 덮을 때만 성공한다. 분류가 틀리면 커밋이 실패하고 기존 영역·경로·길이·선택점은 그대로 남는다(네이티브 API 변경 없음).
- `_validatedRouteSamples`(strict 표본 캐시)는 마지막 커밋 준비가 실제로 요구한 키만 남긴다. 이전에는 clear/prune 호출이 없어 편집마다 과거 키가 누적되고 트랜잭션마다 사전 전체를 복사했다. 캐시 미스는 재매핑일 뿐 검증 결과를 바꾸지 않는다.
- 진단: `MainWindow.RouteRetainedPieceCount`/`ResetRouteRetainedPieceCount()`는 마지막 커밋에서 유지한 조각 수를 센다. 제품 판정에는 쓰지 않고 회귀/자동화가 retain 발동만 확인한다.
- **검증 2026-11-12:** 네이티브 Release/x64 빌드 0 errors. App Release/x64 빌드 0 errors(CS8632 경고 14건, 기존). App 테스트 **170 통과**. `tests\WorkingSide.Regression` **EXIT=0** — 30 sidebar, 30 area highlight, 19 direct exit, **32 vertex edit**(기존 29 + retain/캐시 수명 검사 3), 3 electrical, 43 pattern state, 63 geometry rollback, 20 project load, 51 project-load exclusion, 187 working-side. 대표 시각테스트 `e2e-full.json` **36/36 OK**, `Status=Completed`(36단계: 생성·제외·초기화·꼭지점 편집·전기·3분할·저장/재로드·DXF/STEP).
- 새 회귀 3건: 실제 커밋으로 baseline을 만든 뒤 두 번째 편집에서 유지 조각 수 ≥ 1, 유지 조각의 native ID·길이 불변, 표본 캐시가 마지막 커밋이 쓴 키만 보관(≤ 2). 가짜 fixture(ProjectedPieces 비어 있음)는 전체 스테이징 fallback 경로를 계속 검증한다.
- **미검증:** 실제 마우스 드래그의 체감 속도와 프레임 시간(p50/p95), 대형 다중 영역 프로젝트에서의 확정 시간, DPI·줌·창 크기 변경 시 미리보기 선 두께/가독성, 화면에서의 2D 미리보기와 확정 3D 경로의 시각적 일치. 시간 계측을 실행하지 않았으므로 개선 배율을 수치로 단정하지 않는다.

## DXF export scope (user-confirmed)

- Product DXF output is exactly **one plan view on the confirmed working plane**, plus the confirmed heating routes and the electrical specification table. The previous four orthographic views are not part of the product output.
- Mold outline and heating routes must use the same confirmed working-plane origin and X/Y axes. Do not renormalize DXF coordinates to a min-corner origin.
- On-screen heating route overlays and plan-view heating-route entities use the owning area's opaque color, the same color as the selected-area outline. This is display/export color only and never changes geometry, lengths, IDs, or endpoint binding.
- Export heating geometry from the committed native route chains (ordered 3D points preserved at staging/commit), not from regeneration, remapping, or display tessellation. Exclude staged, missing, stale, disabled and preview geometry explicitly.
- The plan-view length dimensions and the electrical table must use the committed 3D route lengths. Table rows must match the exported circuit IDs one-to-one in order.
- Plan-view dimension text height is 20 mm. The vertical dimension line sits on the left of the mold with its extension lines extending right; the horizontal dimension line stays below the mold.
- The plan view draws the effective heating-area boundary on the `HEAT_AREA` layer: the applied region minus exclusions, with no generation margin. Each boundary uses its area's viewport color as a true-color entity; the layer color is only a neutral fallback. Multiple or degenerate rings, exclusions that leave a hole or split the area, and working-plane mismatches are explicit export failures.
- Degenerate or missing projections, duplicate/missing circuit piece indices, and context/model-revision mismatches are explicit export failures that preserve the previous file state.

## Verification environment (verified)

- Build `src\OccBridge\OccBridge.vcxproj` with Visual Studio `MSBuild.exe`, not `dotnet build`; the C++ project requires the Visual C++ targets.
- Run native/App tests with the OCCT `win64\vc14\bin` and bundled third-party DLL directories on `PATH`. `tests\WorkingSide.Regression\Run.ps1` sets this up and builds into isolated output, avoiding locks from a running app.
- The OCCT 7.9.3 `win64\vc14\bin` folder must also contain `freetype.dll`, `FreeImage.dll`, `openvr_api.dll`, `avcodec-57.dll`, `avformat-57.dll`, `swscale-4.dll`, `avutil-55.dll` and `vcruntime140_1.dll`. `TKService.dll` imports the seven media DLLs, so without them `TKService`, `TKV3d`, `TKOpenGL`, `TKDESTEP` and then `OccBridge.dll` fail to load with `0x8007007E`; the app then dies at startup with a `XamlParseException` whose inner error is `FileNotFoundException: Could not load file or assembly '<run folder>\OccBridge.dll'` even though that file exists (verified 2026-11-11). This is a missing-runtime-file failure, not a managed/native build mismatch — check the full dependency set before suspecting the DLL version. `third_party/` is git-ignored (no history to restore from); flattened copies of the eight files exist in `tests\HeatingMold.App.Tests\bin\x64\Release\net8.0-windows\` and `tests\WorkingSide.Regression\bin\Release\net8.0-windows\`.
- The WorkingSide regression harness invokes private UI methods by name with fixed parameter counts. Keep existing parameterless entry points when adding overloads that share precomputed state.
- `tests\WorkingSide.Regression\WorkingSide.Regression.csproj` references `../../x64/WorkingSideBuild/OccBridge.dll`. After a native change, rebuild the solution with `/p:OutDir=x64\WorkingSideBuild\` or copy the new `x64\Release\OccBridge.dll` there; running only `dotnet run` loads the previous DLL and fails the related checks (verified 2026-11-12). A newer file timestamp is not proof that the fix is present: on 2026-11-12 the WorkingSideBuild copy was newer yet lacked the UTF-8 path fix, and the harness aborted with `0xE0434352` at the Korean-path STEP export check; copying `x64\Release\OccBridge.dll` fixed it.
- `Run.ps1` sets `$ErrorActionPreference='Stop'`, so a failure-path check that intentionally writes an `[Error] ...` line to stderr can abort the script at its `dotnet run` line even though the check passed. Run the harness through cmd with merged output instead: `cmd /c "chcp 65001>nul & dotnet run --project ... > log 2>&1"` (verified 2026-11-12).
- Hand-built regression routes must map their 2D endpoints to 3D through `Bridge.MapWorkingPointOntoFaces`; working-frame coordinates are not world model coordinates, and staging raw frame u/v as world points fails.

## STEP trim tool `tools\step-trim` (verified 2026-11-11)

- Independent console tool (`step-trim.cpp` + `build.ps1` + `run.ps1` + `Test.ps1`); it does not reference or modify the app, the solution, or `src\OccBridge`. The source STEP is read-only; output defaults to a separate file.
- `--mode inspect` reports structure, names, colors, assembly paths, tolerances, free boundaries and axis-aligned projection surplus without writing anything. `--mode trim` requires explicit `--origin`/`--normal`; it never guesses the working plane. The 100 mm margin is planar distance on that plane only.
- Verified on `resource\Mold_Item.step` (Mold = open shell 87 faces, Item = closed solid 85 faces): projected outline area 392,451.928 mm², one wire; 100 mm offset area 713,959.155 mm²; squared-corner boundary 722,473.233 mm² with boundary distance 99.999997–141.421356 mm (141.421 = 100·√2 at right-angle corners); trimmed Mold 80 faces / 958,841.060 mm² (removed 470,155.479); contact area before = after = 48,442.074 mm²; BRepCheck valid, one connected component, one shell. Output `resource\Mold_Item_trim100.step` written 2026-11-11; the source hash is unchanged.
- **Corners are square, not arcs (user-confirmed 2026-11-11).** `BRepOffsetAPI_MakeOffset` only produces corner arcs, so after the offset each corner arc is replaced by the intersection of the two adjacent straight lines and the boundary is rebuilt. Only corners whose both neighbours are lines are squared; others are reported with the reason and keep the arc. On this model 4 of 6 candidates were squared; the two skipped ones are the narrow groove near local (256.8, -431) whose neighbours are line/circle and circle/circle. Never silently keep a corner arc when the user asked for square corners.
- `HLRBRep_HLRToShape::CompoundOfEdges(type, visible, In3d=false)` returns 2D edges without usable 3D curves and crashed the plane splitter; `In3d=true` returns the original 3D curves (measured 211 mm off the working plane), so project them with `BRepProj_Projection` before use. Only the `OutLine` type plus planar silhouette edges (adjacent-face normal sign test) may be used as splitter tools; feeding all sharp/smooth/sewn/outline curves corrupts the arrangement (unsplit original face plus overlapping cells).
- `BRepOffsetAPI_MakeOffset` with `SetApprox(false)` fails on this outline in every join/distance combination; only `SetApprox(true)` + `GeomAbs_Arc` on a face input succeeds. Measured deviation is 99.999997–100.000003 mm over 297 samples, so always re-measure and report the deviation instead of assuming exactness.
- STEP round trip of this file changes the Item by about 11.58 mm³ volume and −0.42 mm² area, so the tool compares the written Item against a baseline round trip of the unmodified document, not against the in-memory value.
- `BRepAlgoAPI_Common` may re-approximate original faces: 78 of 80 Mold faces reuse the original surfaces; the two rebuilt Extrusion faces stay within 0.021040 / 0.016298 mm of the original Mold.
- Unconfirmed by the user: whether 100 mm is planar distance or distance along the surface, and which working plane is authoritative. The committed example run uses origin `(-4183.68, -843.20, 1396)`, normal `(1,0,0)`, X axis `(0,1,0)` chosen only because the YZ projection is the only axis with more than 100 mm surplus in all four directions.

## Route length labels (implemented 2026-11-11, merged into `new` at `aaf466f`)

- **User requirement:** each heating lane and each turn connector length must be readable directly on the route, without a separate number-to-table lookup, both on screen and in the exported plan DXF. A lane number sits in the free space above the lane; a turn number sits outside the turn, so the route line is never covered.
- Displayed value source: `WorkingArea.RoutePieceLengths` (committed actual 3D length) plus the committed native 3D chain from `Bridge.GetCommittedRouteChains`. Never use `SerpentineRoutePiece.Length` (2D), a projected 2D length, a tessellation length or a screen distance as the shown value.
- Core `RouteLengthLabels` (2D working-plane layout) emits exactly one label per `HeatingRun` and per `TurnConnector` piece, text is the length with one decimal in invariant culture, orientation is normalized to (-90, 90]. A lane uses the canonical above-line side; a turn uses the side opposite the adjacent heating runs (found from the neighbouring pieces of the same area, not from an assumed U-shape or the model centre). Collision handling moves along the line, widens the offset, then adds a short leader; it never shrinks or drops the number.
- `FixedConnector` pieces are not labelled but still act as obstacles. A lane split by an exclusion is labelled per continuous fragment, not as a merged per-lane total. Both are implementation defaults, not user-confirmed.
- Screen presentation is a native `AIS_TextLabel` set plus an optional segment leader (`AIS_Line` between two points), both on the Topmost layer, zoomable, and deactivated so they are never pickable. `Bridge.ReplaceRouteLengthLabels` takes anchors, texts, rgb, the flat leader endpoint pairs and a per-label leader flag, and swaps the whole set atomically after validating context/revision; `ClearRoutePiecesNative` also clears labels and leaders, so area, context and model clears remove them. Label construction uses the area color blended 55 % toward white.
- Screen text height is a **model-space** value in millimetres, not screen pixels: `AIS_TextLabel::SetHeight` scales with the model, so a fixed 16 mm is about 1.6 % of the 980 mm default example. The same value is passed into the core layout (`TextHeightMm`) so the collision boxes match what is drawn, and the offset is 0.75 × height so the text never covers the route line. Do not reintroduce a separate layout height; that mismatch lets labels overlap while the layout believes it is clear.
- After a route commit: prepare/publish the committed geometry first, then replace the label set, then redraw. A label failure never rolls back geometry: the labels are cleared so no stale number survives and the caller keeps the presentation warning. Project load appends a label warning to the completion status instead of reporting success silently.
- DXF: `HEAT_LENGTH_LABEL` layer with a dedicated text style, labels are `Text` with `MiddleCenter` alignment and the same rotation, optional leader polylines, and the note `단위: mm / 실제 경로 길이` once above the mold outline. `DxfView.HeatLabels`/`HeatLabelLeaders` are excluded from `AllPolylines`, so mold dimensions (20 mm text), the electrical table and the drawing bounds do not change. A label that cannot be placed fails the export before the atomic replace, preserving the previous DXF.
- Verified 2026-11-11 on the feature branch, then again after rebasing onto the automatic-split-exit commit and fast-forward merging into `new`: Core tests 253 pass (including `RouteLengthLabelsTests` 9), App tests 182 pass after the merge (175 on the branch, plus the 7 split-exit tests; `DxfExporterTests` 15 included), App Release build 0 errors / 6 pre-existing nullable warnings. `tests\WorkingSide.Regression` passes every group (173 working-side, 34 pattern state, 64 geometry rollback, 19 project load, 50 project load exclusion, 3 electrical refresh, 27 sidebar, 20 area highlight, 22 exclusion+exit, 19 direct exit, 22 vertex edit, 461 PASS lines). With `HEATINGMOLD_OCC_LOG` set, the native label log shows `route-length-labels: replaced count=5` for a generated multi-piece pattern and `count=1` for the single-piece synthetic routes, so the replacement really runs inside the commit path; deleting a generated area raises no presentation warning.
- Not verified: real on-screen readability, size against the model scale, overlap at zoom/rotation/resize and DPI change, leader appearance, DXF readability in an external CAD viewer, label appearance while dragging vertex edits (committed labels are not regenerated for a draft preview), project save/load round trip with labels, and the 0-solid default example scenario.
- Implementation defaults (not user-confirmed): screen text height 16 mm model-space, screen offset 12 mm, one decimal place, DXF text height 8 mm, `FixedConnector` not labelled.

## 3D STEP export (implemented 2026-11-11)

- **User requirement:** export one STEP file that shows the 3D model together with the heating wire (발열사) and the heating route (발열경로). This is a separate feature from the DXF plan export; DXF keeps its plan view and electrical table.
- Output composition is one STEP document with **independent parts**: the loaded model as-is (an open shell stays open, no thickening, no Boolean union with the wire), one **wire solid** per confirmed area at the selected case diameter (`HeatingWireSpec.DiameterMm`), and one **centreline wire** per confirmed area carrying the committed route polyline. Parts carry the model file name / `<area> 발열사` / `<area> 발열경로` and the area colour. The route centreline is a separate part instead of being moved or thickened to improve visibility: STEP has no always-on-top, and the committed coordinates must not change for presentation.
- Geometry source is the committed 3D chain from `Bridge.GetCommittedRouteChains` (same collection the DXF export uses, refactored into `CollectExportCircuits` in `MainWindow.RouteExport.cs`). No regeneration, remapping, working-plane projection, camera transform or origin normalisation; model coordinates and millimetres are preserved. Length labels, dimension lines and the electrical specification table are **not** part of STEP.
- The electrical review table, the shared power limit and the OK/NG verdict are **not** STEP preconditions: STEP needs only the measured route geometry and the wire diameter. An unselected wire case is still an explicit refusal because it supplies the diameter. Area scope follows the existing review scope (전체/현재 영역). Areas in the `Missing` state (no generated route) are skipped; `Stale`, `Unverified` and `InvalidLengths` areas refuse the whole export with the area name and review-state reason.
- Native: `src\OccBridge\StepExportNative.inc`, public API `Bridge::ExportStepWithRoutes(targetPath, modelName, wireDiameterMm, areas, out failureReason)` with `StepExportArea(name, areaName, r, g, b, pieces)` and `StepExportPiece(name, points)`. The older `StepExportCircuit` overload was removed on 2026-11-12; `tests\WorkingSide.Regression` now builds one `StepExportArea`/`StepExportPiece` per synthetic route. It builds an XDE document (`XCAFApp_Application` → `NewDocument("MDTV-XCAF")`, `XCAFDoc_ShapeTool`/`ColorTool`), writes with `STEPCAFControl_Writer` (name/colour/layer/props on, `Transfer(doc, STEPControl_AsIs)`, default AP214IS schema, mm), and never touches `g_shape` (the model is exported from a `BRepBuilderAPI_Copy`).
- Wire solid construction: one `BRepPrimAPI_MakeCylinder` per non-zero segment plus a `BRepPrimAPI_MakeSphere` at each direction change (skipped when the normalized direction dot > 0.999999), fused with `BRepAlgoAPI_Fuse`; if the fuse does not complete, the same overlapping solids are written as a compound so the wire shape is complete rather than partial. **OCCT rotation primitives do not build a shape in their constructor: call `Build()` and then read `Shape()` (checking `IsDone()` alone reports false and silently fails).**
- Self-verification before the file is accepted re-reads the written STEP with a **separate** `STEPControl_Reader` (it never replaces the current model): non-empty file, root count == model + wires + centrelines, bounding box within `max(0.05 mm, diagonal × 1e-6)`, at least one SOLID when wires exist, and at least one face-less free edge per centreline. Failure tokens are ASCII (`no-model`, `invalid-diameter`, `invalid-path`, `no-circuits`, `invalid-points`, `model-copy`, `xde-application`, `xde-document`, `xde-tools`, `wire-build:N`, `step-transfer`, `step-write`, `verify-empty`, `verify-read`, `verify-parts`, `verify-shape`, `verify-bounds`, `verify-solid`, `verify-centreline`, `native-throw:<msg>`) and the Korean message is built in `MainWindow.StepExport.cs`.
- App flow (`MainWindow.StepExport.cs`): `STEP 내보내기…` button on the export tab → `TryEnterBusy` → snapshot → area selection → wire case → `CollectExportCircuits` → save dialog (`.step`/`.stp`) → refuse the source STEP path → refuse a model-revision change → write to `<file>.<guid>.step.tmp` → `File.Replace` (existing) or `File.Move` (new, refused if a file appeared meanwhile). Cancel or failure preserves the previous file and the current work; the temp file is always deleted.
- Piece joins inside one circuit: consecutive committed pieces whose end/start gap is within `RouteJoinTolerance` (0.1 mm, the same tolerance the commit accepts) are bridged by a straight wire segment and exact duplicate points are dropped; a larger gap refuses the export naming the area and piece index instead of welding or lengthening the route.
- Part names are written as raw UTF-8 (OCCT 7.9 has no STEP write code page setting; reads default to `Resource_FormatType_UTF8`), so the app round-trips its own Korean names. External CAD reading of non-ASCII product names is unverified.
- **파일 경로 인코딩 (2026-11-12 확인·수정):** 네이티브에 파일 경로를 넘길 때 `msclr::interop::marshal_as<std::string>`을 쓰면 ACP(이 시스템 CP949) 바이트가 만들어지는데, OCCT의 `char*` 경로 규약은 UTF-8이다(`OSD_OpenFile.hxx`; `OSD_OpenStream(char*)`는 `TCollection_ExtendedString(name, Standard_True)`로 UTF-8 해석). 그래서 한글 경로에서 `Step File could not be created` → `step-write`로 실패했다. 기본 파일명이 항상 `..._발열경로.step`이라 사용자 저장은 항상 이 경로를 지난다. `OccBridgeApi.cpp`의 `ToUtf8Path`(`WideCharToMultiByte(CP_UTF8)`)를 `ExportStepWithRoutes`, `LoadStep`, `BeginProjectRestoreCandidate`에 적용했고, `StepExportNative.inc`의 자체 검증 probe는 MSVC `std::ifstream(char*)`가 ACP 기준이라 `OSD_OpenStream`으로 바꿨다(경로 변환만 고치면 `verify-empty`로 실패한다). 새 네이티브 파일 경로 처리도 이 변환을 써야 한다.
- 검증 2026-11-12: 실제 저장 프로젝트의 60조각 확정 경로(2,021점)를 한글 폴더 + 한글 파일명 + 앱의 `<file>.<guid>.step.tmp` 이름으로 내보내 모두 성공했고 ASCII 경로와 바이트 크기가 동일하다(16,204,950B, 약 4.5–5.6초). 한글 경로 STEP 열기(80면)와 프로젝트 복원 후보 생성도 성공. 수정 후 `tests\WorkingSide.Regression`은 전 그룹 통과했고 working-side 181건에 한글 경로 내보내기·비어 있지 않은 파일·한글 경로 STEP 열기·80면 검사가 포함된다. App/Core 테스트는 네이티브 전용 변경이라 재실행하지 않았다. 실행 중 앱에서 버튼을 직접 클릭한 검증은 아직 없다.
- Native link deps added for this feature: `TKPrim`, `TKBO`, `TKCDF`, `TKLCAF`, `TKCAF`, `TKXCAF` (`BRepAlgoAPI_Fuse` is in TKBO; `TKBool` alone gives LNK2019).
- Verified 2026-11-11: a native probe exported the default example plus two synthetic circuits (model 1 + wires 2 + centrelines 2 = 5 parts, 1,616,262 bytes) and the self-verification passed; the STEP text carries `PRODUCT('Mold_Cover_Trim',…)` and the per-area names, red is written as `DRAUGHTING_PRE_DEFINED_COLOUR('red')` and blue as `COLOUR_RGB`. `tests\WorkingSide.Regression` passes 177 working-side checks plus every other group, including 4 STEP checks (model+wires written, non-empty file, `invalid-diameter` for a zero diameter, `wire-build:0` for a degenerate path). Verified again 2026-11-12 after the split branch was rebased onto this one and fast-forward merged into `new`: Core tests 257 and App tests 183 pass, App Release/x64 builds with 0 errors, and the native DLL was rebuilt with the merged `OccBridge.vcxproj` link set.
- Not verified: a real click-through of the export tab in the running app, importing the produced STEP in an external CAD viewer (part names, colours, centreline visibility), split-area and exclusion-bridge projects, vertex-edited routes from a saved project, export performance on a large multi-solid model, and whether external CAD keeps the centreline on top of the wire.
- Implementation defaults (not user-confirmed): `.step` extension as the dialog default, model part flattens to a single part (no assembly hierarchy), wire solid and centreline as two parts per area, area colour applied to both, and no per-part source metadata beyond the name.

## STEP 조각 단위 출력 (사용자 요구 2026-11-12, 구현 2026-11-12)

- **사용자 요구(원문):** "각 발열선이 짧은 길이로 끊겨서 아주 많은수의 선이 생성되는 형식인거야?" → "각 레인과 턴커넥터는 되도록이면 한개의 객체로 처리하는게 맞아. 선 형태가 아주 정확할 필요는 없으니 정확성을 좀 포기하고 근사로 가더라도 그게 맞는방향임". 즉 확정 샘플 선분마다 형상을 만들던 방식을 버리고, 레인/턴커넥터 조각 하나를 중심선 1개 + 발열사 솔리드 1개로 출력한다.
- 조각 구분은 생성기가 아니라 내보내기 수집 단계의 `RouteExportPiece.Kind`(HeatingRun/TurnConnector/FixedConnector)를 그대로 쓴다. 생성 규칙·분할·실측 길이·전기 계산·DXF·프로젝트 schema·원본 모델은 바꾸지 않는다.
- 네이티브 `src\OccBridge\StepExportGeometryNative.inc`(신규)가 조각 형상을 만든다. 끝점 2개를 `AppParCurves_PassPoint`로 고정한 `AppDef_Variational`(MaxDegree 6, MaxSegment 24, GeomAbs_C1, WithCutting, Tolerance 0.05, NbIterations 2)로 B-spline을 적합한다. 0.02 mm 이내로 거의 직선인 조각은 `Geom_TrimmedCurve` 직선을 쓴다. 원호 단계는 넣지 않았다(실제 레인 3점 원호 적합 오차가 0.29–6.64 mm로 부적합).
- 조각 acceptance는 명시적 실패값이다: poles ≤ 32, 원본 polyline 대 곡선 **양방향** 최대 편차 ≤ 0.10 mm, 길이차 ≤ 1.0 mm, 점 진행 파라미터 단조, 첫·끝점 일치. 원본 점만 비교하거나 허용치를 자동 완화하지 않는다. 실패 reason은 `points`/`fit`/`fit-segments`/`poles`/`deviation`/`length`/`order`/`endpoint`다.
- 발열사는 시작점 접선을 축으로 하는 반지름 d/2 원 프로파일을 `BRepOffsetAPI_MakePipeShell`로 sweep하고 `MakeSolid()`한다. 조각당 SOLID 정확히 1개, `BRepCheck_Analyzer` valid, 부피 > 0을 요구하며 겹친 솔리드 compound fallback은 없다. 이 때문에 `TKOffset.lib` 링크와 `TKOffset.dll` 배포가 필요하다.
- 조각 접합은 중점 정규화하지 않는다. 조각마다 독립 솔리드이므로 커밋이 이미 허용한 ≤0.1 mm 간격을 그대로 둔다. 관리 빌더 `StepExportPayload.cs`가 조각 간 0.1 mm 초과를 영역/조각을 지목해 거부한다.
- XDE 구조: 모델 free shape 1개 + 영역별 assembly 2개(`<area> 발열사`, `<area> 발열경로`). 각 assembly 안에 조각 수만큼 컴포넌트가 들어가고 이름은 `<area> 레인 N` / `<area> 턴 N` / `<area> 연결 N`, 중심선은 같은 이름 + ` 경로`다. 색은 영역 색을 두 컴포넌트 모두에 준다.
- 작성 후 별도 `STEPCAFControl_Reader`로 다시 읽어 free shape 수 `1+2N`, assembly/컴포넌트 이름 일대일, 컴포넌트당 SOLID 1·EDGE 1·VERTEX 2, valid·부피>0, 색(선형 RGB, Gen/Surf/Curv 3타입 모두 해석), 모델 bbox를 검증한다. 재읽기에서 EDGE 컴포넌트는 COMPOUND 래퍼로 돌아오므로 occurrence 타입을 신뢰하지 않고 내부 토폴로지를 센다. 색은 저장 시 sRGB, 읽을 때 선형으로 변환되므로 기대값도 `Quantity_Color(r/255,…,sRGB)`로 만들어 비교한다.
- 관리 API는 `StepExportPiece(name, points)` + `StepExportArea(name, areaName, r,g,b, pieces)` + `ExportStepWithRoutes(..., array<StepExportArea^>^, ...)` 하나만 남는다. 구형 `StepExportCircuit` 오버로드와 `wire-build:0` 토큰 매핑은 2026-11-12에 삭제했다. App은 `BuildStepPayload`가 `StepExportPayload.Build`에 위임한다.
- `OccBridge.vcxproj`에 `/bigobj`를 추가했다(헤더 증가로 C1128 발생). `StepExportGeometryNative.inc`는 `OccBridgeApi.cpp`의 익명 namespace 안에서 include되므로, `.inc`의 표준/OCCT include는 `STEP_EXPORT_INCLUDES_DONE` 매크로로 감싸고 실제 include는 `OccBridgeApi.cpp` 전역에 둔다. 이 가드 없이 표준 헤더를 익명 namespace 안에서 포함하면 `anonymous-namespace::std` 모호성 오류가 난다.
- 검증 2026-11-12: 실제 저장 프로젝트의 60조각(2,021점)으로 진단 probe 실행 결과 60/60 성공, 최대 poles 23·degree 6, 적합 52 ms·sweep 261 ms. 같은 60조각 + 기본 모델을 새 writer로 내보내면 read-back 검증까지 통과하고 **1,248,309 B / 약 0.7 s**(기존 평탄 출력 16,204,950 B / 4.5–5.6 s). `tests\WorkingSide.Regression` EXIT=0, 461 PASS(신규 `STEP piece export writes one solid per piece`, `STEP piece export file is non-empty` 포함, 한글 경로/80면 검사 유지). App 테스트 194 통과, App Release/x64 빌드 0 errors.
- 테스트 픽스처 변경: STEP 회귀의 합성 회로를 90° 코너 폴리라인에서 완만한 5점 폴리라인으로 바꿨다. 실제 확정 조각(레인/턴)에는 날카로운 코너가 없고, 코너가 있는 합성 조각은 0.10 mm 편차 한도를 넘어 `piece-build:...:deviation`으로 거부되기 때문이다. 이는 한도를 완화한 것이 아니라 픽스처를 실제 데이터 형태에 맞춘 것이다.
- **FreeCAD 색상 호환 (2026-11-12 확인·수정):** FreeCAD 1.1.4(OCCT 7.8.1)는 STEP 파트 색을 어셈블리 occurrence 라벨이 아니라 **referred shape 라벨**에서 찾는다(`ImportOCAF2::getColor`: `TopLoc_Location`을 제거한 shape로 `XCAFDoc_ColorTool::GetColor` 조회). 이전 writer는 occurrence 라벨에만 `XCAFDoc_ColorGen`을 설정해 FreeCAD에서 발열사 솔리드가 기본 회색(0.447, 0.475, 0.502)과 기본 edge 색(0.098, 거의 검정)으로 보였다. `WriteStepDocumentNative`가 컴포넌트 라벨과 referred shape 라벨 양쪽에 이름·색을 설정하고, edge-only 중심선에는 `XCAFDoc_ColorCurv`도 준다. `VerifyStepDocumentNative`는 referred 라벨의 색(SOLID는 Surf/Gen, 중심선은 Curv)을 추가로 검사한다.
- **FreeCAD edge-only 파트 미지원 (확인 2026-11-12):** FreeCAD는 STEP의 edge-only 파트를 아예 가져오지 않는다. FreeCAD 자신이 내보낸 단일 edge STEP도 다시 가져오면 객체가 0개였다. 따라서 60개 `발열경로` 중심선(EDGE)은 FreeCAD 트리에 나타나지 않고, `발열사` 솔리드만 영역 색으로 표시된다. STEP 파일 자체에는 두 파트가 모두 있고 OCCT 7.9.3 reader로는 읽히므로 파일 구조 오류가 아니라 FreeCAD importer 제약이다. FreeCAD에서 경로 선을 보이게 하려면 별도 solid/ribbon 출력 같은 제품 변경이 필요하며, 현재 범위에서는 하지 않았다.
- 검증 2026-11-12: 2영역/3조각 probe를 새 writer로 내보내 FreeCAD 1.1.4로 import한 결과 발열사 솔리드의 `ShapeColor`가 (1,0,0)과 (0,0.5,1)로 읽혔고(수정 전에는 기본 회색), 경로 edge 파트는 트리에 없었다. `tests\WorkingSide.Regression` EXIT=0, working-side 183 PASS(한글 경로/80면/STEP 조각 검사 포함).
- 미검증: 실행 중 앱에서 STEP 버튼 클릭, 외부 CAD에서 레인/턴 단위 객체 선택과 표시, 분할 영역·제외영역 프로젝트의 조각 출력, 저장 프로젝트의 정점 편집 경로, 대형 다중 솔리드 모델의 시간/파일 크기, 조각 간 ≤0.1 mm 틈이 외부 CAD에서 시각적으로 이어져 보이는지, FreeCAD에서 60개 발열사 솔리드가 실제 화면에서 충분히 보이는지.

## 발열경로 표면 검증 간격 1mm와 편집 표본 재사용 (사용자 확정 2026-11-12)

- **사용자 확정 요구:** "1mm 이하의 틈이나 끊김은 그냥 오류로 보는게 맞는 것 같아". 경로 3D 매핑의 보강 표본 간격을 1mm로 두고, 그 폭 이하의 표면 틈·끊김도 오류로 거부한다. 자동 보정·메우기·근사 대체는 없다.
- 이 값은 **허용 공차가 아니라 조회 간격**이다. `GeometryTolerancePolicy.DistanceTolerance`(0.1mm: 경계 침범 깊이, CAD 평가/투영 잔차, 지지면 거리, 조각 간 실제 3D 간격)와 `RouteSurfaceValidationPolicy.ChordError`(0.05mm)는 그대로 유지한다. 일반 dense 매핑의 `MaxPlanarSpacing`(0.1mm)도 바꾸지 않았다.
- 신규 `src\HeatingMold.Core\Geometry\RouteSurfaceValidationPolicy.cs`: `Version = "route-surface-v2"`, `GapSpacing = 1.0`, `ChordError = 0.05`, `MaxProbes = 100000`. `SurfacePathMapper.TryMapRouteStrict`의 기본값과 제품 호출부 두 곳(`MainWindow.Areas.cs`의 스테이징, `MainWindow.VertexEdit.cs`의 후보 검증)이 이 값을 쓴다. 보강 표본은 성공 여부 판정에만 쓰이고 확정 경로 좌표에는 추가되지 않는다.
- **표본 한계:** 1mm 간격 조회로도 조회점 사이의 더 좁은 결함 검출은 보장되지 않는다. "1mm 이하 전부 검출"로 기록하거나 사용자에게 약속하지 않는다. 기존 정확 CAD 경계 검사와 native 지지면 검사는 표본 사이 구간을 대신 검증하지 않는다(2026-11-12 확인).
- 검증 키 접두부를 `strict-v3`로 올리고 정책 값에서 직접 생성한다(`route-surface-v2`, gap, chord, max-probes, support/datum 0.1, selected-global, `GeometryTolerancePolicy.Version`). 0.1mm 시절 표본과 1mm 표본이 같은 키로 섞이지 않는다.
- **꼭지점 편집 중복 매핑 제거(2026-11-12 구현):** `TryBuildVertexCandidate`가 후보 검증에 쓴 strict 표본을 `preparedSamples`(검증 키별)로 반환하고, `CommitVertex`가 `_pendingValidatedSamples`에 넣어 `RebuildNativePatternGeometry`를 호출한 뒤 finally에서 비운다. `TryCommitAreaRouteCandidates`는 후보 회로 갱신·커밋 컨텍스트 캡처 전에 이 표본을 트랜잭션 로컬 사전에 병합하고, 스테이징(`StageCurvedRoutePiece`)은 키가 정확히 같을 때만 재매핑을 생략한다. 키가 다르면 기존 strict 매핑으로 폴백하며, 실패 후보의 표본은 확정 캐시에 남지 않는다.
  - 후보 표본 키는 `PrepareDisplayedEditPiece`가 인출점 보정을 적용한 표시 좌표로 만든다. 따라서 인출점과 인접한 조각(회로 첫/마지막)은 스테이징 키와 달라져 재매핑될 수 있고, 보정이 없는 내부 조각만 재사용된다. 이것은 의도된 안전 폴백이다.
  - `RoutePendingSampleReuseCount`/`ResetRoutePendingSampleReuseCount`는 진단용이며 제품 판정에 쓰지 않는다.
- 검증 2026-11-12 (worktree `route-perf`, HEAD `4b6c929` + 이 변경): 네이티브와 솔루션 Release/x64 빌드 0 errors, Core 테스트 271 통과, App 테스트 195 통과, `tests\WorkingSide.Regression` 전 그룹 통과(vertex edit 29건 — 신규: 픽스처 확정 회로 구성, 후보 표본 재사용 발동). 재사용 계측은 처음에 병합 블록이 대기 사전을 먼저 비워 항상 0으로 보였고, 해제를 finally 한 곳으로 옮겨 실제 발동을 확인했다.
- `docs\VertexEditing.md`도 현재 구현에 맞췄다: Undo/Redo 설명 제거, `_pendingStageSamples`/`_vertexBaselineSamples`(존재하지 않는 캐시) 설명을 위 재사용 규칙으로 교체, 0.1mm 표현을 정책 값으로 교체.
- 미검증: 실행 중 앱에서 실제 마우스 드래그의 체감 속도, 지정 프로젝트(`바탕화면\발열몰드 저장테스트\Mold_Solids.json`)의 생성·편집 시간 실측과 개선 배율(시간 계측을 실행하지 않았다. 1mm 변경은 보강 조회 횟수를 약 1/10로 줄이는 부분 최적화일 뿐 전체 10배를 의미하지 않는다), 저장/재열기, 곡면·좁은 영역·sliver 모델에서 1mm 간격의 검출 결과, 커밋 후 표시 경고 동작.
- **2026-11-15 최신 구분:** 발열경로 정밀도 요구가 "대략적으로 보이면 됨"으로 정정되었고, 그 뒤 사용자 승인으로 표면 미검출 짧은 구간의 **제한 근사 연결**(2D 진행/3D 거리 ≤ 10 mm, 누락 개수 무제한)이 구현되었다. 이 절의 1mm/strict 규칙은 strict API 계약으로 그대로 유지되며, 제품 스테이징은 strict 실패 뒤에만 근사를 적용한다. 아래 "발열경로 제한 근사 연결 구현" 항목과 `docs/CurrentStatus.md`(§2·§3)를 참고한다.

## STEP 발열영역 색상 (사용자 요구 2026-11-12, 구현 2026-11-12)

- **사용자 요구(원문):** "그래 그럼 최종구현계획 세워줘. 그리고 발열영역의 색상도 표시되어야해". STEP 파일에서 발열영역(선택한 발열면)이 영역 색으로 보여야 한다. 발열사 SOLID와 발열경로 중심선의 기존 출력은 그대로 둔다.
- 범위는 **적용영역 − 제외영역(마진 적용 전)**, 즉 `WorkingArea.EffectiveRegion()`이다. 표시용 mesh·화면 삼각분할을 설계 경계로 쓰지 않고 **원본 CAD 표면**을 구획해 색칠한다. 같은 자리에 중복 면을 겹쳐 깜빡이게 하는 방식은 쓰지 않는다.
- 앱은 `WorkingArea`를 `StepExportArea(name, areaName, r, g, b, pieces, faceIds, frame, ring)`(9인자)로 넘긴다. `frame`은 origin 3 + axisX 3 + axisY 3, `ring`은 프레임 2D의 u,v 쌍 flat이다. `faceIds`는 `FaceIdSet` 정렬본이다. 6인자 생성자는 영역 색 없는 기존 호출 호환 경로로 남아 있다.
- 네이티브 `src\OccBridge\StepExportAreaNative.inc`(신규): ① 영역 링을 프레임 2D→3D 폴리곤 face로 만들고 법선 방향 ±(모델 대각 × 1.5) 압출한 prism 생성 ② 영역 prism마다 `BRepAlgoAPI_Section`(`Init1(model)`, `Init2(prism)`, `ComputePCurveOn1(true)`, `Approximation(false)`, `Build()`) ③ 모은 edge로 `BRepFeat_SplitShape`를 **한 번** 실행 ④ `splitter.Modified(face)`로 조각별 faceId 소유권 유지 ⑤ 조각 표본점(경계 edge 5등분 + UV 중앙점)을 프레임 2D로 사영해 coverage 판정 ⑥ `Covered` 조각만 `NativeStepAreaFaceColor`로 반환.
- **`BRepAlgoAPI_Common`/`Cut`은 열린 셸에서 0면, `BRepAlgoAPI_Splitter`는 열린 셸을 분할하지 못한다.** `Section` + `BRepFeat_SplitShape`만 열린 셸과 솔리드 모두에서 성공한다(80→93면 shell 1개 유지, 574→591면 solid 30 유지, 체적 −0.002%). `TKFeat.lib` 링크가 필요해 `OccBridge.vcxproj`에 추가했다.
- prism 좌표는 반드시 작업 프레임 2D(`origin + u*axisX + v*axisY`)를 쓴다. 모델 bbox 절대 좌표를 쓰면 prism이 모델 밖에 생겨 전부 빈 결과가 되고, 이를 코드 결함으로 오판하기 쉽다.
- coverage 판정은 **경계 허용치(`AreaRegionBoundaryTolerance = 0.05` mm) 검사를 먼저** 한다. 절단선 위 점의 u가 조각마다 `-2.27e-13`/`+0`으로 뒤집혀 even-odd 내부 판정이 갈라지므로, 경계 위 점은 내부/외부 증거에서 제외한다. 허용치를 키워 해결하지 않는다. 남은 `Partial`(한 면이 두 영역에 걸침)과 두 영역 동시 `Covered`는 `area-partial`/`area-overlap` 명시적 실패다. 전체 면 색칠로 대체하지 않는다.
- 모델은 **사본을 만들지 않고 `g_shape`/`g_faces`를 직접** 넘긴다(split/section 도구는 입력을 변경하지 않고 XDE 문서는 참조만 한다). 영역 색이 하나도 없으면 기존처럼 `BRepBuilderAPI_Copy`를 쓴다. 사본을 쓰면 면 순서·identity 대조가 필요해져 `area-face` 오탐이 났다.
- 재읽기 bbox 검증의 기대값은 **분할된 도형이 아니라 분할 전 원본 `g_shape`** 이다. `BRepFeat_SplitShape`가 만든 절단 edge의 BSpline 극점 때문에 in-memory bbox가 실제 기하보다 0.08~0.09 mm 크다. 허용치는 `max(0.05, diagonal × 1e-6)` 그대로이고 `BoundsOf`는 `BRepBndLib::Add(shape, box, Standard_False)`(triangulation 미사용)다.
- 면 색은 `XCAFDoc_ShapeTool::AddSubShape(modelLabel, face, faceLabel)` 후 `colors->SetColor(faceLabel, Quantity_Color(r/255, g/255, b/255, Quantity_TOC_sRGB), XCAFDoc_ColorSurf)`로 기록한다. `AddSubShape`는 "새로 추가됨"을 반환하므로 반환값을 실패로 보지 말고 out 라벨 null 여부로 판정한다.
- 재읽기 검증 확장: 재읽은 모델의 Surf 색 면 수가 요청한 `faceColors` 수와 같아야 하고, 각 색이 어떤 영역 색과도 일치해야 하며, region 데이터가 있는 영역마다 색칠 면이 1개 이상이어야 한다(실패 토큰 `verify-facecolor`, 면 등록 실패 `xde-face`).
- 실패 토큰(모두 ASCII, 앱에서 한국어 변환): `area-frame:<a>`, `area-ring:<a>`, `area-face:<a>`, `area-bounds`, `area-prism:<a>`, `area-section:<a>`, `area-section`, `area-split`, `area-partial:<a>`, `area-overlap:<a>:<b>`, `area-nofaces:<a>`. `MainWindow.StepExport.cs`의 `DescribeAreaStepFailure`가 영역 인덱스를 영역명으로 바꿔 안내한다.
- 앱 쪽 유효 영역 조건: `EffectiveRegion()`이 **단일 링이고 3점 이상**이어야 한다. 2개 이상 링이면 `"영역 N의 유효 발열영역이 M개 외곽으로 나뉘어 있어 STEP 영역 색을 표시할 수 없습니다."`로 명시적 실패한다(제외영역으로 구멍/분리를 만들 수 없다는 기존 제품 규칙과 일치). `faceIds`가 비면 선택 면이 없다는 오류다.
- 검증 2026-11-12(자동): 실제 모델·실제 작업 프레임으로 만든 probe에서 단일 영역 전체 색칠(A), 절반 2영역(B), 절반 1영역(D), 3밴드(E)가 모두 성공하고 영역 밖 링은 `area-nofaces:0`으로 거부됐다. FreeCAD 1.1.4로 import한 결과 모델 면 색이 A=80면 전부 빨강, B=89면 중 빨강 45 + 파랑 `(0,0.502,1)` 44, D=89면 중 빨강 45 + 기본 회색 44, E=98면 중 빨강 45 + 초록 9 + 파랑 44로 **sRGB 값 그대로, 색 번짐 없이** 읽혔다. 발열사 SOLID도 영역 색으로 읽힌다.
- 검증 2026-11-12(회귀): `tests\WorkingSide.Regression` EXIT=0, working-side 187 PASS(신규 4건: 전체 표면 색칠 성공, 파일 비어있지 않음, 영역 밖 링 `area-nofaces:0` 거부, 작업 mesh 사영 비퇴화). 나머지 그룹 모두 통과(30 sidebar, 30 area highlight, 22 exclusion+exit, 19 direct exit, 27 vertex edit, 3 electrical, 38 pattern state, 64 geometry rollback, 20 project load, 51 project load exclusion). App 테스트 195, Core 테스트 271 통과, App Release/x64 빌드 0 errors(기존 CS8632 경고만).
- 검증 2026-11-12(실제 프로젝트): `C:\Users\yong753\Desktop\발열몰드 저장테스트\Mold_Solids.json`(schema 4, 영역 A, faceIds 3개, 제외·분할 없음, 60조각)을 오프스크린 하네스에서 앱의 `LoadProject` → `ExportStepCore`로 그대로 실행해 **Success**, 2,842,018 B. FreeCAD 1.1.4에서 모델 80면 중 **영역 A의 선택 면 3개만 빨강**, 나머지 77면은 기본 회색, 발열사 SOLID 60개는 모두 빨강으로 확인했다. 즉 이 프로젝트의 발열면은 80면 전체가 아니라 3면이다.
- 미검증: 실행 중 앱에서 STEP 버튼을 실제 마우스로 클릭, 분할 영역·제외영역이 있는 실제 프로젝트의 화면 표시, FreeCAD Flat Lines에서 발열사 윤곽이 여전히 검게 보이는지와 Shaded 표시, FreeCAD 이외 CAD의 면별 색 해석, 대형 다중 솔리드 모델의 구획 시간, 원본 대비 체적 변화의 실측(이번 실행은 분할이 없어 80면 그대로였다).

## 분할 인출 방향 고정과 절단 위치 전용 탐색 (사용자 확정 2026-11-12)

- **사용자 확정 요구(원문):** "발주처에서 어느정도 편차는 용인해주기로 했으니까. 최대한 각 발열경로의 길이를 맞추되. 인출점을 변경하면서 까지 맞출 필요는 없어". 즉 새 영역의 시작/종료 인출점은 분할 전과 같은 측면(방향)을 유지하고, 길이 균등화는 절단 위치만 바꿔 수행한다. 길이 편차가 남아도 그 수치를 표시하고 분할을 확정한다.
- 신규 `src\HeatingMold.App\SplitExitSidePolicy.cs`: 부모의 저장된 시작/종료점을 확정 작업평면·생성 방향으로 회전해, 그 점이 놓인 행의 영역 외곽 최좌/최우와의 거리로 측면(low/high)을 한 번만 판정한다. 진행 방향 끝(위/아래 변)에 놓인 점은 그 행의 교차가 1개 이하이므로 영역 전체 최좌/최우로 판정한다. 좌우 구분이 모호하거나 폭이 없으면 명시적으로 실패하며, 임의 측면으로 대체하지 않는다. 자식마다 부모 좌표와의 거리를 다시 비교해 측면을 뒤집지 않는다.
- `SplitExitPointPlanner`: `TryBuildCandidates(parent, children, margin, spacing, out candidates, out failure)`가 자식마다 **하나의** 후보만 만든다. 시작 행은 마진 적용 생성 영역의 첫 레인 행(경계 행은 기존 0.01 mm 안쪽 보정 유지), 시작/종료 측면은 `SplitExitSidePolicy`가 정한 방향으로 고정한다. 시작 측면 반전, 자연 사행 종료 측면 선택, 시작 레인 위상 ¼·½·¾ 변형은 제거했다. 고정 종료 측면에 도달하지 못하는 절단은 실제 생성 실패로 후보에서 제외되고, 절단 탐색이 다른 위치를 시도한다.
- `MainWindow.SplitSearch`: 인출점 변형 조합 탐색과 `SplitVariantCandidate`, 변형 평가 상한(4개 seed/10개 cut set/2400회 생성)을 제거했다. 탐색은 절단 위치만 96회(2~4분할 공통) 평가하고, 목표 1.02에 도달하면 조기 종료, 아니면 탐색 범위에서 가장 좋은 절단을 쓴다. **비율이 1.10을 넘어도 실패하지 않는다.** 2026-11-12에는 `AreaSplitter` 코어에 남아 있던 `HardLengthBalanceLimit`와 `DescribeImbalance`도 삭제해, 수동 인출점이 없는 경로도 가장 좋은 유효 절단을 그대로 확정한다. 유효한 2D 경로를 만드는 후보가 하나도 없을 때만 실패한다.
- `SplitLengthCombinationSolver.cs`와 그 테스트는 인출점 후보 조합이 사라져 호출자가 없으므로 삭제했다.
- `MainWindow.Split.cs` 완료 문구: 시작/종료점이 분할 전과 같은 측면을 유지한다는 설명과 함께 `실제 2D 경로 길이 {min}~{max} mm, 편차 {ratio:F3}배`를 표시한다. 별도의 편차 확인 대화상자는 수치가 사용자 확정되지 않아 추가하지 않았다(커밋 전 확인을 새로 넣지 않음).
- 검증 2026-11-12: App Release/x64 빌드 0 errors(기존 CS8632 경고만). Core 테스트 271 통과, App 테스트 191 통과(제거한 solver 4건·변형 2건, 추가한 방향 2건 반영). `tests\WorkingSide.Regression` 전 그룹 통과(30 sidebar, 30 area highlight, 22 exclusion+exit, 19 direct exit, 29 vertex edit, 3 electrical, 38 pattern state, 64 geometry rollback, 20 project load, 51 project load exclusion, 187 working-side).
- 실제 프로젝트 검증(오프스크린, 임시 검사 후 삭제): `C:\Users\yong753\Desktop\발열몰드 저장테스트\Mold_Solids.json`을 `LoadProject`로 열어 `TryPrepareBalancedSplit`(3분할, margin 5, spacing 15, 3.5 kW)을 실행했다. 자식 A-1/A-2/A-3의 시작·종료 X가 모두 작업평면 오른쪽(295.9126/295.9136, 영역 최우 295.9100)이고, 시작/종료 Y는 부모 선택 구간 안이며, 모든 점이 자식 경계 위에서 3D 표면으로 평가됐다. 2D 길이는 5,304.0~5,886.8 mm, 편차 **1.1099배**로, 같은 측면을 유지한 채 절단만 탐색한 최선값이다. 이 값은 기존 1.10 하드 한도를 넘지만 새 정책에서는 확정된다. (참고: 과거 변형 탐색이 만들던 1.0219배는 인출 측면을 바꾼 결과이므로 새 정책의 기대값이 아니다.)
- 미검증: 실행 중 앱에서 실제 마우스로 분할 클릭, 새 정책으로 분할한 프로젝트의 저장/재열기, 화면 인출점 마커 표시, 3D 실측 길이 균등, 제외영역/곡면/경사면, 좁은 중간 밴드, 대형 모델 탐색 시간, 분할 영역 DXF/STEP 출력.

## 전기 사양표 판정별 행 색 (사용자 확정 2026-11-12)

- **사용자 요구(원문):** "여기서 판정이 NG인 회로들은 해당 행의 텍스트 색을 전부 빨간색. OK인 회로들은 초록색으로 표시해줘". 대상은 검토 탭의 회로별 사양표 한 행 전체의 텍스트이며, 셀 단위가 아니라 행 단위다.
- 구현: `MainWindow.xaml`의 `Window.Resources`에 `VerdictNgBrush`(`#C62828`)·`VerdictOkBrush`(`#1B7F3B`)를 두고, 두 DataGrid(`ElectricalSpecGrid`, `ExpandedElectricalGrid`)의 `DataGrid.RowStyle`에 `Verdict` 바인딩 DataTrigger를 각각 추가했다. 판정 문자열은 `MainWindow.Electrical.cs`의 `ElectricalVerdictText`가 만드는 `OK`/`NG`/`—`/`""`이며, 합계 행은 `IsTotal=true`·`Verdict=""`라 색이 적용되지 않고 굵게만 표시된다.
- 행 Foreground는 셀 텍스트로 상속된다. .NET 8 WPF 오프스크린 probe로 확인: DataGridRow Foreground DataTrigger가 셀 TextBlock까지 전달되고(OK 초록/NG 빨강), 선택된 행에서는 DataGridCell 테마 트리거가 우선해 기본색으로 돌아간다(어두운 배경 위 빨강 텍스트 방지). 그래서 CellStyle이 아니라 RowStyle에 색을 둔다.
- **사용자 요구(같은 날, 추가):** 표 아래 두 안내 문단(`L은 각 회로의 인출점 사이 실제 도체 전체 길이이며…`, `이 영역이 배분받은 발열 전력으로 필요한 분할 수를 계산합니다…`)을 삭제한다. 두 TextBlock만 지웠고, 그 사이 `Separator`는 표와 자동 분할 버튼 사이 구분선으로 남겼다.
- 검증 2026-11-12: `tests\WorkingSide.Regression` EXIT=0, 478 PASS — pattern state 38→43(신규 5건: 검토 탭·확대 보기 각각 NG 빨강/OK 초록 트리거의 실제 `Setter` 색, 행 판정 문자열이 `OK`/`NG`/`""` 키 집합인지). App 테스트 191 통과, App Release/x64 빌드 0 errors(기존 CS8632 경고만). 네이티브 변경 없음.
- 미검증: 실행 중 앱 화면에서 실제 렌더링(줌·DPI·행 선택 조합)을 육안 확인하지 않았다. 삭제한 두 문단이 사라진 검토 탭의 세로 배치도 실제 화면에서 확인하지 않았다.

## 레거시 코드 정리 2차 (사용자 확정·구현 2026-11-12, worktree `legacy-cleanup`)

- **사용자 확정:** "일단 외부 소비자는 없어. 이 프로젝트 내의 코드는 이 프로젝트에서만 쓰는 거야", "외부 전원 접속선 붙이는 기능은 사용 안 함", "구형 로직들은 다 필요 없어. (시각테스트 관련 기능 제외하고.)" 따라서 외부 ABI 가능성만으로 API를 보존하지 않는다.
- **별도 worktree:** 이 정리는 다른 세션이 시각테스트를 최신화하는 동안 `C:\Users\yong753\.local\share\opencode\worktree\b777a2\legacy-cleanup`에서 수행했다. 메인 작업 트리의 미커밋 시각테스트 변경은 건드리지 않았다. 시각테스트 최신화가 끝난 뒤 구형 시각테스트 의존 로직의 삭제 여부를 다시 결정한다.
- **삭제한 것:**
  - 인출면 영구 색칠: `SetExitFaceHighlight`/`ClearExitFaceHighlight`, `g_exitFaceHighlightFace/Color`, `IsExitFaceHighlightNative`, 색상 복원·hover 예외 분기.
  - `Bridge.GetAutoAxis`와 `GetAutoAxisNative`.
  - 구형 STEP 출력: `StepExportCircuit` DTO와 `ExportStepWithRoutes(StepExportCircuit[])` 오버로드. `tests\WorkingSide.Regression`은 `StepExportArea`/`StepExportPiece`로 이관했고, 퇴화 경로 기대 토큰은 `piece-build:`로 바꿨다.
  - 외부 전원 접속선 기능 전체: XAML 전원 연결 구역과 `PowerSetupButton`/`PowerCancelButton`/`PowerMigrationHint`, `MainWindow.xaml.cs`의 `PendingLead`/`_pendingLeads`/`_leadLengths`/`_powerEditingLocked`/`CancelPowerSetup`/`SetPowerEditingLock`/`PowerPrompt`/`PowerSetup_Click`/`PowerCancel_Click`/`OnPowerLocation*`/`UpdatePowerLocation`, `OccViewport.PowerLocationMode`/이벤트/100ms preview 타이머, 네이티브 `g_powerLeads`/`ProjectPowerLead`/`ClearPowerLead*`/`ProjectPowerLeadNative`. `WorkingRouteMigrationComplete` 상수와 `_insertionDepth` 필드도 삭제했다. 삽입 깊이 입력 상자와 "깊이 0만 지원" 가드는 일반 생성 경로에 남아 있다.
  - 구형 프로젝트 지원: `ProjectElectricalMigration.cs`와 그 테스트, `TargetPowerText`, `AppliedVoltageText`, `ExitBoundaryKind`/`RegionExitBoundaryKind`/`RegionCadBoundaryKind`, `BoundaryKind`/`RegionBoundaryKind`. `ProjectRestoreNative`의 `importPolicyVersion <= 1` 주축 정렬 분기와 `BeginProjectRestoreCandidate`의 정책 인자를 제거했고, 로드는 정책 2만 받는다.
  - 구형 인가전압 계산: `ElectricalCalculator.Compute`, `PowerSupplySpec`, `ElectricalSpecTableBuilder.Build`(AppliedVoltage), `ElectricalCircuitInput`, `ElectricalCalculationMode`, `ElectricalSpecTable.Supply`/`Mode`/`IsTargetPowerMode`, `ElectricalResult.TargetPowerMode`. Core/DXF 테스트는 `ComputeFromTargetPower`/`BuildFromTargetPower`로 이관했다.
  - `tests\Phase1Curved.Repro` 전체와 `docs\Phase1.CurvedSurfaceRouting.md`의 실행 안내. `tests\RegionBoundary.Repro`는 현재 원본 edge parameter/방향 검증이므로 유지했다. (곡면 라우팅 **기록 문서**도 2026-11-15 문서 정리에서 삭제했다. 재현 **프로젝트** 삭제는 위의 이전(2026-11-12 legacy-cleanup) 정리 이력이다. 현재 구현·한계는 `docs/CurrentStatus.md` §9를 본다.)
  - `AreaSplitter.HardLengthBalanceLimit`/`DescribeImbalance`. 자동 인출점이 없는 경로도 가장 좋은 유효 절단을 확정하고, 유효한 밴드가 없을 때만 실패한다.
- **중복 2D 산술:** `PlanarMath2D`(App)로 `DistanceToSegment`/`Lerp`/`Dot`/`Cross`/`IsFinite`를 통합했다. `EffectiveRegionBoundary2D`, `SplitRegionBoundary2D`, `SplitRegionContainmentBoundary`가 이 공통 구현을 쓰며, CAD/artificial 바인딩·허용치 정책은 각 호출부가 그대로 소유한다.
- **시각테스트 의존 제거(2026-11-12):** 다른 세션의 v2 대표 시나리오가 완료되어 `VisualScenario` v1, `pickExit`/`cancelExitPick`, 구형 인출면/앵커 후보(`ComputeCandidates`, `GenerateAnchored*`, `ApplyExitCandidate`, `ApplyAnchorCandidate`, `TrySelectAnchorCandidate`, `SelectedAnchorCandidate`, `AnchorCandidateList`)와 관련 프로젝트 필드를 모두 제거했다. 3차 정리 항목을 참고한다.
- **검증 2026-11-12:** 네이티브 Release/x64 빌드 0 errors. Core 테스트 **272** 통과, App 테스트 **178** 통과. `tests\WorkingSide.Regression\Run.ps1` EXIT=0, **505 PASS**, working-side 187건, project-load 20건, project-load exclusion 51건. 로그의 `FAIL` 문자열은 PASS 라벨과 의도된 `[Error]` 실패 경로이며 실제 실패는 없다. `wglMakeCurrent`는 종료 시 그래픽 정리 메시지다.
- 커밋: `1a29a84 chore: 레거시 전원·프로젝트·계산 경로 제거 및 2D 산술 통합` (branch `legacy-cleanup`, 37 files, +173/−2116). 아직 메인 `new`에 병합하거나 원격에 push하지 않았다.
- **미검증:** 실행 중 앱에서 전원 연결 UI가 사라진 화면, 구형 프로젝트를 열 때의 명시적 거부 대화상자, 스키마 6 저장→재열기 실제 UI, 외부 CAD에서의 STEP, 시각테스트 최신화와의 병합 후 재검증, 분할 영역 STEP 출력(다른 세션의 `area-nofaces` 한계).

## 레거시 코드 정리 3차: 시각테스트 v1·구형 인출면/앵커 경로 제거 (사용자 확정·구현 2026-11-12)

- **사용자 확정:** 다른 세션의 시각테스트 최신화가 끝났으므로, v1 시나리오와 `pickExit`/`cancelExitPick` 호환, 구형 인출면/앵커 기반 생성·복원 경로를 더 삭제한다. 현재 제품은 `schemaVersion: 2`의 `pickExitPoint` 직접 인출점 선택만 사용한다.
- **선행 병합:** 메인 `new`의 `3f05803 test(visual): 실제 제품 경로를 사용하는 대표 종합 시나리오 추가`를 `legacy-cleanup`에 병합한 뒤 작업했다. 메인 작업 트리의 미커밋 파일은 건드리지 않았다.
- **시각테스트 정리:** `VisualScenario`의 v1 고정 필드(`area`/`exit`/`anchor`/`split`/`electrical`/`expect`)과 `ValidateLegacy`, `VisualAreaSpec`, `VisualExitSpec`, `VisualAnchorSpec`, `VisualExpectSpec`, `VisualScenarioCompiler.IsLegacy`/`legacyStepNames`, `MainWindow.VisualTest`의 `ExecuteLegacyVisualStepAsync`, `StepSelectArea`, `StepSelectExitAndAnchor`, `StepGeneratePattern`, `StepCheckElectrical`, `StepExportDxfAsync`, `VisualPickExit`, `VisualCancelExitPick`, `ApplyExitAndAnchor`, `AnchorMatches`, `FindExitCandidate`를 제거했다. `schemaVersion`은 2만 허용하며, `pickExit`/`cancelExitPick` 행동과 `expect.exitFaceId`도 제거했다. `docs\VisualTest.Scenarios.md`와 `tests\HeatingMold.VisualTest\README.md`에서 v1 설명을 삭제했다.
- **제품 경로 정리:** `WorkingArea`의 `ExitFaceId`/`ExitFaceRevision`/`ExitChainFaceIds`/`AnchorCandidates`/`SelectedAnchorCandidate`/`ExcludedCandidates`/`ExcludedPlan`/`ExcludedCandidateFailure`/`ExcludedPlanKey`/`HasExitFace`/`HasExitAnchors`를 제거했다. `MainWindow.ExitFace`의 인출면 후보 선택·적용, 앵커 후보 계산·선택·적용, 제외영역 앵커 계획을 제거하고 직접 인출점 선택 경로만 남겼다. `MainWindow.xaml`의 접힌 `AnchorCandidateList`와 이벤트를 제거했다.
- **생성/저장 정리:** `TryGenerateAreaPattern`은 `HasSelectedExits` 경로만 사용한다. `AreaRouteSnapshot`, `CommittedRouteContext`, `MainWindow.Areas`/`Project`/`ProjectLoad`/`RouteTransactions`/`Split`/`VertexEdit`에서 구형 인출면/앵커 필드와 복원·키 항목을 제거했다. 프로젝트 schema 6에서 `exitFaceId`, `exitFaceRevision`, `exitChainFaceIds`, `selectedAnchorCandidate`, `regionMode`를 더 이상 저장·복원하지 않는다. `RegionExitBoundary`/`ExitBoundary`는 현재 직접 인출점 경계로 유지한다.
- **Core 정리:** `Serpentine.GenerateComponents`, `ComputeCandidates`, `ComputeCandidatesComponents`, `GenerateAnchored`, `GenerateAnchoredComponents`, 관련 DP 후보/체인 헬퍼, `AnchoredCandidate`, `ExcludedAnchorCandidate`, `ExcludedCandidatesResult`, `SerpentineComponentsResult`/`SerpentineComponentsFailureReason`을 제거했다. `GenerateWithSelectedExits`, `GenerateComponentsWithSelectedExits`, `TryBuildAnchoredLocalRoute`, `BuildLocalRoute`, `TryBuildSingleLaneLocalRoute`는 현재 제품 경로라 유지했다.
- **네이티브 정리:** `PickExitCandidate`, `HighlightExitCandidate`, `PickSelectedBoundaryPoint`, `GetSharedBoundaryEdges`/`SharedBoundaryNative`와 전용 상태를 제거했다. `ShowExitCandidates`/`ClearExitCandidates`는 직접 인출점 마커 표시에 쓰이므로 유지했고, `GetBoundaryExitSides`는 원본 경계 회귀 검사에서 쓰이므로 유지했다.
- **테스트 정리:** `BoundExitStateTests.cs`, `BoundaryComponentsTests.cs`, `BoundSerpentineTests.cs`, `tests\WorkingSide.Regression\ExclusionExitChecks.cs`를 삭제했다. `SerpentineTests`/`RegionOpsTests`의 구형 anchored 테스트를 제거하고 현재 생성·경계 테스트만 남겼다. `SelectedExitStateTests`, `ProjectLoadBoundaryTests`, `VisualTestingTests`, `GeometryRollbackChecks`, `ProjectLoadChecks`, `PatternStateChecks`, `VertexEditChecks`, `SidebarTabChecks`를 현재 직접 인출점/스키마 6 계약에 맞췄다. `VertexEditChecks`는 `SelectedStartExit`/`SelectedEndExit`/`RegionExitBoundary`를 실제로 설정한다.
- **검증 2026-11-12:** 네이티브 Release/x64 빌드 0 errors, App Release/x64 빌드 0 errors. Core 테스트 **226** 통과, App 테스트 **151** 통과. `tests\WorkingSide.Regression\Run.ps1` EXIT=0, **482 PASS** — 30 sidebar, 30 area highlight, 19 direct exit, 29 vertex edit, 3 electrical, 43 pattern state, 63 geometry rollback, 20 project load, 51 project-load exclusion, 187 working-side. 구형 `exclusion+exit` 그룹은 삭제됐고, 현재 직접 인출점 그룹 19건은 통과한다. 대표 시각테스트 `e2e-main.json`은 `-NoBuild -TimeoutSeconds 300`으로 **22/22 단계 OK**, `Automation=Completed`(기본 90초 제한은 22단계 소요 시간보다 짧아 첫 실행이 `No result.json yet`으로 끝났을 뿐, 단계 기록은 모두 성공이었다).
- **미검증:** 실제 마우스/키보드, 실행 중 앱 화면, 외부 CAD에서 DXF/STEP 열기, 분할 영역 STEP 출력(기존 `area-nofaces` 한계), 그리고 이 3차 변경을 원격에 push/병합한 뒤의 재검증. 커밋은 아직 하지 않았다.

## 시각테스트 4번: 자동분할 안전성 (사용자 요청·구현 2026-11-12~13)

- **사용자 요청(원문):** "시각 테스트 시나리오 4 작성하자. 기존 시나리오들과 차별되도록 만들어야해." 기존 1~3번이 분할 성공 후 상태를 확인하는 것과 달리, 이 시나리오는 **분할이 거부·취소되는 경로**, **분할로 새로 생긴 영역의 독립성**, **절단선 인출점의 저장·복원**을 검사한다.
- 신규 `tests\HeatingMold.VisualTest\scenarios\e2e-split-safety.json`(40단계). 전선은 Case 2(Ø0.15 mm / 8 Ω/m), 공유 3.5 kW. 영역 A `[44,56,69]` FrameX margin 5 / spacing 15는 보존 대조군, 영역 B `[16,24,26]` FrameY margin 5 / spacing 20이 분할 대상이다.
- 흐름: A 생성(60조각/29턴/1회로/18,015.505 mm)·B 생성(32/15/1/8,467.619) → Case 2 220 V 기준 두 영역 NG → 허용 전류 3 A에서 분할 거부(`분할해도 해결되지 않습니다`) → 허용 전압 60 V에서 5개 필요 상한 거부(`최대 4개까지 지원합니다`) → 200 V에서 `confirm: "no"` 취소(모든 영역 경로·길이·인출점 깊은 보존) → 같은 조건 승인으로 B를 B-1(16조각/4,097.746 mm)·B-2(16/4,351.326)로 교체(각 1회로, 절단선 u=460.106, 2D 길이 편차 1.0619배) → B-1 종료 인출점을 새 절단선 위 `(460.106, -175)`로 재선택(`bindingKind: "split"`, parameter 0.0968131) 후 재생성 16조각/7턴/4,102.751 mm, B-2 보존 → 3영역 전기검사 A NG·B-1 OK·B-2 OK → 전체 DXF/STEP·현재 영역 DXF → 저장 후 같은 실행에서 재로드(`compareProject`) → 복원 B-1 재생성이 같은 값 → B-1 삭제 후 A·B-2 보존, 재배분 뒤에도 A NG·B-2 OK, 전체 DXF.
- **검사기 보완(`MainWindow.VisualTest.cs`):** `splitArea`가 사유를 `SplitStatusText`(제품 분할 결과 위젯)에서 읽고, 거부·취소는 영역 목록 정확 일치 + `VisualRouteSnapshot` 깊은 비교로 판정하며, 성공 시 비대상 영역 깊은 보존·자식별 1회로·동일 `FaceIdSet`·시작→종료 축 방향 유지를 검사한다. `generate`는 시나리오 정의가 없는 분할 자식도 지원하도록 `RequireAreaDefinition`을 선택적으로 바꾸고 `ActivateArea`가 복원한 상속 입력을 쓴다(분할 자식 재생성은 확인 대화상자를 소비하지 않으므로 `confirm`을 넣으면 `사용되지 않은 확인 응답`으로 실패한다). `pickExitPoint`에 `exitPoint.bindingKind`(`any`/`cad`/`artificial`/`split`)를 추가해 실제 provenance 종류가 기대와 다르면 실패한다.
- **자동 배정 인출점은 원본 CAD 경계에 놓인다.** 절단선 binding은 사용자가 절단선 위를 직접 선택할 때 생기므로, 분할 성공 검사에 절단선 binding을 강제하지 않는다(강제했다가 2분할 성공을 오탐했다).
- **정정(2026-11-13): 위 "3·4분할 불가" 기록은 틀렸다.** 실제 원인은 `SplitRegionBoundary2D`가 정확 CAD 투영점을 자식 근사 링까지의 거리 `OnRingTolerance = 0.02` mm로 재검사해 버리던 것이었고, 그 검사를 정확 반평면·구간 판정으로 교체한 뒤 B의 **4분할이 정상 동작**한다(아래 `## 분할 영역 소속 판정: 정확 CAD 경계` 항목). 남은 미해결 제약은 **B의 3분할(110 V)이 `Serpentine`의 마지막 추가 레인 `NoLanes`로 실패**하는 것과, A의 4분할이 같은 `NoLanes` 계열로 실패하는 것이다 — 별개 원인이며 이번 변경 범위 밖이다. 또한 분할 영역이 포함된 STEP은 영역 구성에 따라 `영역 <이름>의 유효 발열영역에 해당하는 CAD 면이 없습니다`로 실패한다(분할 자식 단독, A+B-2 두 영역에서 재현. 세 영역 전체는 성공). 미해결 상태로 기록한다.
- **검증 2026-11-13:** `e2e-split-safety`를 서로 다른 실행 폴더로 두 번 독립 실행(`artifacts\visual-test\safety-a`, `safety-b`)해 모두 **41/41단계 OK**, `Status=Completed`, 오류 없음. 기존 `e2e-full`·`e2e-multi-area`·`e2e-margin-spacing-exit` 회귀는 모두 exit=0. App 테스트 **182** 통과(bindingKind 허용·거부 2건 추가). `tests\WorkingSide.Regression` exit=0, **477 PASS**(신규 `SplitBoundaryChecks` 19건 포함).
- **미검증:** 실제 마우스로 분할·인출점 재선택, 새 프로세스에서의 프로젝트 복원, 외부 CAD에서 이 시나리오의 DXF/STEP 열기, 화면 육안 판정. B의 3분할과 분할 영역 단독 STEP은 위 제한대로 실패한다.
- **후속 시나리오(2026-11-13):** `tests\HeatingMold.VisualTest\scenarios\e2e-split-four.json`(19단계) 추가 — 같은 A/B를 만든 뒤 Case 2 80 V에서 **B를 세로 4분할**(자동분할 최대 개수)해 B-1~B-4를 만든다. 실측: 8조각/3턴/발열 4/1회로, 총 길이 2,000.729 / 2,116.686 / 2,158.419 / 2,187.093 mm, 2D 편차 1.0931배, 절단선 u=395.775 / 467.183 / 548.592. 독립 2회 실행 모두 19/19 OK이고 두 실행의 `splitArea` 데이터가 바이트 단위로 동일하다. 네 자식 모두 `FaceIdSet`이 원본 `16,24,26`과 같고 margin 5 / spacing 20을 유지한다.

## 분할 영역 소속 판정: 정확 CAD 경계 (사용자 승인·구현 2026-11-13)

- **사용자 보고:** "3,4 분할 실패부분이 이해가 안돼" → "애초에 오차가 생긴이유 말이야" → "정확한 원인을 먼저 분석해봐" → "상세 구현계획세워봐" → "구현 진행해". 즉 3·4분할 실패의 **원인 분석과 수정**이 승인 범위다.
- **확정 원인:** `SplitRegionBoundary2D.Project`(그리고 `ResolveOutward`/`ProjectBound`/`Evaluate`)가 `_inner.Project`로 얻은 **정확 CAD 투영점 하나**를 `IsOnRing`(자식 근사 링 폴리곤까지 거리 `OnRingTolerance = 0.02` mm)로 재검사해 통과할 때만 인정하고, 아니면 그 CAD 후보를 버리고 두 번째 후보를 찾지 않았다. 부모 영역 폴리곤은 `GeometryTolerancePolicy.DistanceTolerance = 0.1` mm deflection의 `GCPnts_QuasiUniformDeflection` 근사 + Clipper 0.01 mm grid로 만들어지므로, 정확 CAD 점이 자식 링에서 0.02 mm 넘게 벗어나는 일이 정상적으로 생긴다. 실측 탈락 거리: 3분할 중간 밴드 **0.0415 mm**, 4분할 첫 밴드 **0.0513 mm**(정적 추정 0.049 / 0.062와 같은 수준).
- **철회한 설명:** 과거 기록의 17.5 mm / 5.013 mm를 "근사 폴리곤과 CAD의 오차"로 본 것은 **틀렸다**. 그 값은 CAD 후보가 탈락한 뒤 남은 **다른 절단선까지의 거리**였고 5 mm 검색 반경 밖이라 NoIntersection이 됐을 뿐이다. "곡선을 직선으로 바꿔 큰 오차가 생겼다"는 설명도 근거가 없어 철회한다.
- **구현(신규 `src/HeatingMold.App/SplitBoundarySnapshot.cs`):** 불변 스냅샷. ① 절단선을 `SameLine`으로 중복 제거하고 각 선의 **유지 반평면**을 자식 링 꼭짓점의 cross min/max로 결정(양쪽에 걸치면 "분할 영역 외곽을 절단선이 가로지릅니다.") ② `dividers`(절단선 + 제외 사각형 4변)로 각 원본 CAD edge의 파라미터를 **구간 분할** ③ 구간 중간점을 `IsInsideGeometry`(반평면 + 제외 엄격 내부)로 판정해 자식에 남는 구간만 `CadInterval`로 보관. 질의는 **구간별**로 하고, 성공한 `ExitEdgeBinding`은 항상 **원본 edge의 `FirstParameter`/`LastParameter`**를 유지한다(서브트림을 저장 identity로 쓰지 않음 — topology remap과 `ExitBoundaryConstraint` equality가 이 값으로 참조를 찾는다).
- **`SplitRegionBoundary2D` 변경:** `TryCreate`가 스냅샷을 만들고 실패하면 생성도 실패한다. `Project`/`ResolveOutward`는 CAD(구간별) + 제외변 + 절단선 후보를 **모두 모아** 가장 가까운 하나를 고르며, 0.1 mm 이내에 서로 다른 점이 있으면 `MultipleIntersections`로 명시 실패한다. `ProjectBound`/`Evaluate`는 `_inner` 결과를 `_snapshot.IsInside`로 검사한다(링 거리 검사 제거). 제외변 전용 질의(`ProjectArtificial`/`EvaluateArtificial`)는 기존 `IsOnRing`을 유지한다(제외변은 이미 자식 링에 0.005 mm 이내로 맞춰져 있다).
- **containment는 폴리곤 유지(의도적 결정).** `CreateContainment()`는 `SplitRegionContainmentBoundary`(자식 링 폴리곤) 그대로 둔다. 근거: `MainWindow.Circuits.cs`의 `ValidateSegments`가 HeatingRun 조각의 **모든 인접 점 쌍(수백~수천)** 에 대해 ray/probe를 호출하므로 네이티브 정확 질의로 바꾸면 호출이 폭증한다. `ExactRegionContainment`가 쓰는 0.1 mm OnBoundary 허용치와 0.1 mm deflection 폴리곤의 오차 수준이 같아 정책상 일관적이다. **인출점 binding만 정확 스냅샷으로 교체**하는 것이 이번 범위다.
- **네이티브 추가 API:** `Bridge.IntersectExitEdgeLine(id, revision, reference, ax, ay, bx, by)` → `ExitEdgeRayResult`. 기존 ray 질의와 달리 **원점을 포함한 무한 직선 전체** 교차를 반환하고 커서 tolerance 인자를 받지 않는다(내부 `GeometryEpsilon = 1e-7`만 사용). 접선·겹침은 `TangentOrOverlap`, 같은 2D 다른 parameter는 `Ambiguous`로 명시 실패. 기존 `IntersectExitEdgeRay`/`ProjectExitEdge`의 의미는 바꾸지 않았다. 구현: `ProjectedExitCurveNative.inc`의 `IntersectLine`, `ExitEdgeQueriesNative.inc`의 `IntersectExitEdgeLineNative`, `OccBridgeApi.h/.cpp`.
- **부수 효과(정상):** 2분할 자동 배치가 좋아졌다. `e2e-split-safety`의 B 세로 2분할이 절단선 `u=514.190`(B-1 20조각/5,166.151, B-2 12/3,285.543, 2D 편차 1.5724)에서 **`u=460.106`(B-1 16조각/4,097.746, B-2 16/4,351.326, 편차 1.0619)** 로 바뀌었다. 이전에는 후보 인출점 해석이 0.02 필터에 막혀 대부분의 절단 후보가 "유효한 2D 경로를 만들 수 없는 후보"로 탈락했고, 그래서 더 나쁜 절단이 선택됐다. 시나리오 기준값은 이 실측값으로 갱신했다.
- **검증(2026-11-13):**
  - 신규 `tests\WorkingSide.Regression\SplitBoundaryChecks.cs`(그룹 `SplitBoundaryChecks`, 19건 PASS). 실모델 `resource\Mold_Cover_Trim.step`, faces `16,24,26`, 실제 작업 프레임으로 3분할 중간 밴드/4분할 첫 밴드를 만들어 **원본 CAD edge identity로 해석되고 거리가 0.02 mm보다 크면서도 유효**함을 확인한다. 실측 `target=418.340,-178.420 → resolved=418.338,-178.461 ringGap=0.0415`, `target=369.999,-175.848 → resolved=369.996,-175.899 ringGap=0.0513`.
  - WorkingSide 전체 **EXIT=0, 477 PASS**, `CHECK-GROUP FAILED` 0건.
  - App 테스트 **182 통과**, `SplitRegionBoundaryTests`의 `RetainedHalfPlanes_RejectsRingWithoutCutSegment`를 `TryCreate_RejectsRingCrossedByCutLine`로 교체(이제 `TryCreate` 단계에서 거부).
  - `e2e-split-safety` 2회 독립 실행 **41/41 OK**, `e2e-split-four` 2회 독립 실행 **19/19 OK**(데이터 동일), `e2e-full`·`e2e-multi-area`·`e2e-margin-spacing-exit` 모두 exit=0.
- **함정(재발 방지):** `dotnet build tests\WorkingSide.Regression`은 `HeatingMold.App.dll`/`HeatingMold.Core.dll`을 갱신하지 않는다. App 코드를 고친 뒤에는 `x64\Release\OccBridge.dll` → `x64\WorkingSideBuild\`, 그리고 `HeatingMold.App.dll`/`HeatingMold.Core.dll`을 회귀 하네스 출력으로 **수동 복사**해야 한다. 복사하지 않으면 낡은 DLL이 돌아 진단 로그조차 나오지 않는다. `dotnet test`도 같은 이유로 `dotnet build` → 수동 복사 → `dotnet test --no-build` 순서가 필요하다.
- **미검증:** 실제 마우스로 4분할 클릭, 화면 육안 판정, 새 프로세스에서의 복원, 외부 CAD에서 DXF/STEP 열기, 중첩 분할(한 자식을 다시 분할)에서의 정확 구간 판정, 비축정렬 절단선(제품은 축정렬만 만든다).

## 배포 패키지 구성 (사용자 승인·구현 2026-11-13)

- **사용자 요청:** "그럼 배포 구성시작해". Windows 10 64비트 사용자에게 ZIP 한 개로 전달하고, 런타임/VC 문제를 사용자 PC 설치 없이 해결하며, 실제 화면 스크린샷이 들어간 매뉴얼까지 포함하는 것이 범위다.
- **도구 위치는 `tools\deploy\`** 다. `.gitignore`의 `[Rr]elease/` 규칙 때문에 `tools\release\` 는 git이 무시하므로 쓰지 않는다. 사용자가 직접 패키지를 다시 만드는 절차는 `tools\deploy\README.md` 에 정리되어 있다.
- **VC 재배포 방식(계획 변경, 확정):** vc_redist.x64.exe 를 동봉해 관리자 권한으로 설치하는 대신, VS 재배포 폴더의 **CRT/OpenMP DLL 을 앱 폴더에 복사(app-local)** 한다. 관리자 권한·UAC·사전 설치가 모두 사라진다. 설치 파일은 패키지에 넣지 않는다.
- **배포 형태:** self-contained win-x64, **multi-file**(단일 파일/trimming/AOT/ReadyToRun 금지), 오프라인. .NET 데스크톱 런타임과 VC 런타임이 모두 패키지 안에 있다.
- **구조:** 루트에 `HeatingMold.Start.exe`(Win32 /MT, asInvoker, .NET·VC·OCCT 무의존), `처음_읽어주세요.txt`, `ReleaseNotes.txt`, `THIRD-PARTY-NOTICES.txt`, `build-info.json`, `manifest.sha256`, `Manual\`(index.html·manual.css·images 18장·PDF), `App\`(publish 전체 + OccBridge.dll + ijwhost.dll + 앱 DLL 36 + VC DLL 4 + `deployment.json` + `resource\Mold_Cover_Trim.step` + `occt-resources\` 11폴더), `Licenses\`.
- **시작 도구 동작:** 아키텍처·Windows 버전 확인 → 패키지 파일 존재 → `manifest.sha256`(bcrypt SHA-256) 무결성 → `CreateProcessW`(작업 폴더 `App`) → 15초 뒤 정상 종료. 실패 코드 2/3/4/5/6. `--check-only`, `--diagnostics`, `--skip-verify`, `--quiet` 는 앱에 전달하지 않는다.
- **로그:** `%LOCALAPPDATA%\HeatingMold\Logs\<yyyyMMdd-HHmmss>-<pid>\` 에 `app.log`, `environment.json`(packaged/baseDirectory/occtResources/OS/런타임/명령줄), `occ_viewer_log.txt`. 런처는 `Logs\launcher.log`. 로그 실패는 앱 실패로 이어지지 않는다.
- **배포 모드 판정:** `App\deployment.json` 이 있으면 배포 모드로, 모든 경로를 `AppContext.BaseDirectory` 기준으로 해석한다. 개발 폴더/전역 OCCT 로 폴백하지 않으며, 설정이 깨졌으면 명시적으로 실패한다. 파일이 없으면 기존 개발 탐색을 그대로 쓴다.
- **명령(검증됨):**
  - 빌드 `powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 [-Version x] [-SkipNativeBuild] [-NoZip] [-SkipVerify] [-ManualPath <index.html 있는 폴더>]`
  - 검증 `powershell -ExecutionPolicy Bypass -File tools\deploy\Test-Package.ps1 [-PackagePath <zip|폴더>] [-RunLauncher]`
  - 화면 캡처 `powershell -ExecutionPolicy Bypass -File tools\deploy\Capture-Manual.ps1 [-PackagePath <zip|폴더>] [-Scenario e2e-full] [-HoldSeconds 2.5]`
  - 매뉴얼 `powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Manual.ps1 [-NoPdf]`
  - 배포본으로 시각테스트 `Run.ps1 -Scenario <name> -AppExe <pkg>\HeatingMold.Start.exe -ModelPath <pkg>\App\resource\Mold_Cover_Trim.step -RunDir <dir> -HoldSeconds 1 -TimeoutSeconds 600 -ExpectPackaged`
- **산출물:** `artifacts\deploy\<Version>\<PackageName>\` + `.zip` + `.zip.sha256.txt` + `build-report.txt`. `build-info.json` 에 git commit/dirty, 브리지 SHA-256, DLL 수, VC 재배포 버전, 예제 해시, publish 파일 수가 들어간다.
- **검증 2026-11-13:** 패키지 `HeatingMold-Test-20261113-0316-cb5cc60-dirty-win-x64`(manifest 538파일, 242.1 MB) ZIP SHA-256 `1284B4806D0B248FA2AEB26494C300AB3FD254DF34270768CD359B00785F279E`. `Test-Package.ps1` 539파일 / problemCount 0 / 런처 `--check-only` exit 0. 추출본 + 시작 도구 경유로 5개 시나리오 전부 통과: `e2e-full` 36/36, `e2e-multi-area` 54/54, `e2e-margin-spacing-exit` 59/59, `e2e-split-safety` 41/41, `e2e-split-four` 19/19 (합계 209단계, 실패 0, 모두 `packaged=true`, baseDirectory=패키지 `App\`). 매뉴얼 PDF 2,258,167 B / 내부 이미지 18개 / 페이지 18.
- **의존성 폐포는 매 빌드마다 자동 계산한다**(`tools\deploy\generated\native-dependencies.json` 은 참고용). 실측: 앱 DLL 36개(OCCT 28 + tbb12·jemalloc·TKFillet·TKDE·TKCDF·TKVCAF·TKCAF·openvr_api·FreeImage·freetype·avcodec-57·avformat-57·swscale-4·avutil-55 포함), VC 런타임 4개(msvcp140, vcruntime140, vcruntime140_1, vcomp140 — FreeImage 가 OpenMP 를 import), publish 제공 1개(ijwhost.dll), 시스템 DLL 21개 제외. **AGENTS.md 가 기록한 8개 누락 함정이 모두 자동 발견된다.** 직접 링크 목록만 보고 파일을 정하면 안 된다.
- **함정(재발 방지):**
  - PS 5.1은 BOM 없는 .ps1 을 CP949로 읽는다. `tools\deploy\*.ps1` 은 **UTF-8 BOM** 으로 저장한다(JSON/HTML 은 BOM 없음).
  - StrictMode 에서 `@(...) | Where-Object` 는 결과가 1개면 배열이 아니어서 `.Count` 접근이 실패한다. 파이프라인 전체를 `@()` 로 감싼다.
  - PowerShell 함수 파라미터에 `[byte[]]` 형을 붙이고 큰 배열을 넘기면 호출마다 전체 복사가 일어난다(수 MB DLL 파싱이 27초). `Read-AsciiZ` 는 형 선언을 쓰지 않고 512바이트 슬라이스 + `IndexOf([char]0)` 로 처리한다. `File.ReadAllBytes` 반환은 `return ,(...)` 로 감싼다.
  - exec/셸에서 자식 PowerShell 오류는 `#< CLIXML` 로 잘려 사라진다. 진단은 스크립트가 파일에 직접 쓰게 하고 그 파일을 읽는다.
  - `Capture-Manual.ps1` 은 System.Drawing 로드 전에 `SetProcessDpiAwarenessContext(-4)`(per-monitor v2)를 호출해야 한다. 이 PC는 1920×1080 실제 해상도에 125% 배율이라, 없으면 1536×864 로 가상화되어 창 오른쪽·아래가 잘린다.
  - `steps.jsonl`/`result.json` 은 UTF-8 이다. `[IO.File]::ReadAllLines/ReadAllText` 로 읽어야 한글 단계 이름이 깨지지 않는다.
  - `*>` 리다이렉션은 UTF-16LE로 파일을 쓴다(UTF8 로 읽으면 깨진다).
  - 시작 도구는 앱을 띄운 뒤 15초 뒤 정상 종료한다. 시각테스트 대기 로직은 이 종료를 실패로 보면 안 된다.
- **매뉴얼 화면 가공(검증됨):** 매뉴얼 그림은 실제 배포본 실행 화면이며, ① 모델 절대경로(x145~905, y42~118) ② Windows 정품 인증 안내(x1482~1912, y928~1002) 두 영역만 흰색으로 가린다. 18장 전부에서 해당 영역이 순백임을 픽셀 검사로 확인했다. 수치·UI·경로 내용을 바꾸지 않는다. 화면 캡처용으로 `--visual-hide-bar` 옵션(`VisualTestOptions.HideBar`)을 추가해 노란 시각테스트 진행 바만 숨긴다(실행·판정 로직 불변).
- **헤더 버튼 변경(2026-11-13):** 상단 `STEP 열기…`와 `저장` 버튼을 삭제하고 `다른 이름으로 저장…`만 남겼다. `tools\deploy\assets\Manual\index.template.html`의 헤더 목록(83행)과 모델 열기 항목(93행)은 갱신했다. 기존 18장 스크린샷은 상단 헤더에 옛 버튼이 찍혀 있으므로, 다음 매뉴얼 작성 때 `Capture-Manual.ps1`로 전량 재캡처하고 `Build-Manual.ps1`로 HTML·PDF를 다시 만들어야 한다. 재캡처 전 기존 패키지의 `Manual\`을 재사용하면 문서와 화면이 불일치한다.
- **미검증:** 깨끗한 Windows 10 x64 PC(별도 장비/VM)에서의 실행, 관리자 권한 없는 표준 사용자 계정, 100%/150% DPI 화면, 한글·공백 계정 경로, 새 프로세스에서의 프로젝트 복원, 외부 CAD에서 DXF/STEP 열기, 백신/보안 정책 간섭.
- **남은 배포 게이트:** .NET 8 지원은 2026-11-10 종료됐다. 외부 배포 전에 유지 승인 또는 지원 버전 전환 후 재검증이 필요하다. 제3자 라이선스(FFmpeg·FreeImage·FreeType·OpenVR·oneTBB) 원문과 CAD 모델 외부 전달 권한도 배포 담당자 확인 항목으로 `THIRD-PARTY-NOTICES.txt`·`ReleaseNotes.txt`에 남겨 두었다.



## RemainingWork 1·2단계 진행 (2026-11-13)

- 1단계 완료: 두 실패를 별도 시나리오 `repro-b-four-split-anchor.json`(18/18), `repro-b-exclusion-surface.json`(20/20)로 재현했고, `tests\WorkingSide.Regression\Fixtures\remaining-failures.json`와 `RemainingFailureFixtureChecks` 312건으로 입력·보존 조건을 고정했다. 실행 DLL은 OccBridge `D5E5…E9AE`, App `399F…2023`, Core `2D9C…083E`.
- 2단계 완료: `LanePitchSpec`을 분할 탐색의 단일 기준으로 사용한다. `TryPrepareBalancedSplit`/`TryEvaluateSplit2D`는 `LanePitchSpec`을 받고, 영역 분할·레인 격자·자동 인출점 행은 `CenterPitchMm`, 실제 생성만 `OuterGapMm`/`WireDiameterMm`를 쓴다. `spacing + wireDiameter` 이중 덧셈은 제거됐다.
- 2단계 검증: App 테스트 184 통과. 외곽 5 mm에서 Case1(Ø0.30) 레인 중심거리 5.30 mm, Case2(Ø0.15) 5.15 mm, 동일 입력 재평가 좌표 동일. 실제 모델 2분할은 B-1 4,276.956 mm / B-2 4,465.184 mm, 적용 외곽 19.35 mm·직경 0.15 mm로 저장·재로드 후에도 동일했다. WorkingSide 전 그룹 통과(split boundary 28, fixture 312, working-side 187 등).
- 3단계 결정(사용자 확정): 자동 인출점의 목표 레인 끝은 자식 외곽 극값이 아니라 그 행의 실제 스캔라인 발열 구간 끝이다. 시작 행은 생성기가 허용하는 첫 구간, 종료 행은 마지막 구간의 해당 측면 끝을 쓴다.
- 3단계는 같은 날 구현 완료됐다(아래 `## RemainingWork 3단계 정확 행 교차`). 정확 교차 중복 판정은 provenance+선택 parameter, 접선·겹침은 다른 교차가 있어도 즉시 실패, 후보는 측면 유지→목표 구간 끝 최근접→동률 provenance 모호성 실패 순서로 고쳤다.
- 단계별 검증 정책(사용자 확정 2026-11-13, 원래 `docs\RemainingWork.Steps.md` §0.5·§0.6에 기록했으나 그 단계 기록은 2026-11-15 문서 정리에서 삭제됨 — 현재는 이 문장과 `docs/CurrentStatus.md`가 정본): 1~6단계의 "통과"는 그 단계의 필수 테스트·통과 기준과 변경 영향 범위 회귀의 충족을 뜻하며, 시각테스트 5개 전체 통과를 뜻하지 않는다. 5개 각 2회 통과는 7·8단계 기준이다. 계약 변경으로 낡은 기대값은 7단계에서 재측정하고, 원인 미확인 실패는 다음 단계로 넘기지 않는다. "로직이 바뀌었다"는 설명만으로 실패를 면제하지 않는다.
- 미완료/갱신 예정: `e2e-split-safety`의 B-1 기대 4,280.265 mm는 피치 수정 후 실제 4,276.956 mm가 되어 현재 실패한다. 7단계에서 실측값으로 재확정하며 제품 회귀로 되돌리지 않는다.


## RemainingWork 3단계 정확 행 교차 (2026-11-13)

- `ExitBoundaryIdentity`가 인출 경계 후보의 동일성을 판정한다. 2D 좌표와 표시용 숫자 parameter만 비교하지 않고, CAD edge identity·trim·선택 parameter, 제외 사각형 identity, 절단선 identity를 각각 보존한다. CAD와 절단선이 같은 좌표에 있어도 서로 다른 후보로 유지된다.
- `SplitBoundarySnapshot.AddLineCandidate`와 `SplitRegionBoundary2D.AddLineCandidate`가 이 판정을 사용한다. `SplitRegionBoundary2D.IntersectLine`은 접선·겹침이 다른 교차점과 함께 있어도 `TangentOrOverlap`으로 즉시 실패한다.
- 자동 인출점의 목표는 자식 외곽 극값이 아니라 그 행의 실제 스캔라인 발열 구간 끝이다. 시작은 생성기가 허용하는 첫 구간, 종료는 마지막 구간의 요청 측면 끝을 쓴다. 후보 선택은 요구 측면 필터 → 목표 구간 끝 최근접 → 1e-6 동률의 서로 다른 provenance 모호성 실패 순서다.
- App 테스트 190 통과: CAD edge/parameter identity, 다른 provenance, 겹침+수직 교차, 오목 행의 실제 구간 끝 선택을 포함한다. WorkingSide는 split boundary 28, fixture 312, working-side 187을 포함해 전 그룹 통과했다. 실제 모델 2분할 저장·재로드 시나리오는 22/22 Completed였다.
- 4단계 분석 대상: B 세로 4분할 재현은 이제 원래 B-4 `AnchorNotAtLaneEnd` 대신 B-1 `RequiredExtraLaneOutsideRegion`으로 거부된다. 실제 구간 끝 목표로 바뀌면서 실패 지점이 이동한 것이며, 제외영역 재현은 20/20으로 그대로 통과한다. 시나리오 기대 문구를 임의로 바꾸지 않는다.

## RemainingWork 4단계 완료 (2026-11-13)

- **실패 1은 정상 거부로 판정했다.** B 세로 4분할의 B-1은 마지막 레인이 종료점 반대편에서 끝나 같은 피치 레인을 한 줄 더 만들어야 하지만 그 위치에 발열영역 구간이 없다. 마지막 레인 뒤집기·피치 변경·부분 분할을 하지 않는 현재 생성 규칙에서는 성립하지 않는다. 자동 배정 좌표는 B-1 `(329.934254,-173.664867)→(379.634254,-176.421829)`, B-2 `(391.78,-177.071972)→(466.33,-180.873877)`, B-3 `(473.43,-181.221536)→(547.98,-182.026687)`, B-4 `(551.79,-182.026687)→(601.49,-182.026687)`다. `repro-b-four-split-anchor.json`의 기대 문구를 `마지막 레인이 종료점 반대편`으로 갱신해 18/18 Completed였고, fixture에는 현재 실패 지점과 함께 `historicalFailurePoint: B-4`, `historicalFailureClass: AnchorNotAtLaneEnd`를 남겼다.
- **실패 2는 유효 경로 오판을 수정했다.** 실패 조각 B 3/26 HeatingRun의 2D `(304.37,-132)-(630,-132)`, 실패 uv `(630,-132)`는 `MapWorkingPointOntoFaces` direct와 `QueryWorkingSurface` global이 모두 Miss였지만 인접 `(629.95,-132)`는 Face 24에 Hit였다. 내부 표면 틈이 아니라 선택 영역 경계 위 정확한 점을 네이티브 면 교차가 시작점으로 처리하지 못한 것이다. `MainWindow.RouteMapping.cs`의 `BuildSurfaceMapDelegate`는 direct/global이 모두 Miss일 때만 `context.Boundary.Project(uv, ExitBoundaryConstraint.Tolerance)`로 경계 provenance를 얻고, 투영점이 요청 uv와 tolerance 이내로 같으면 `TryEvaluateExitSample`로 원본 edge parameter의 3D 점을 반환한다. 내부 표면 틈을 최근접 점으로 대체하지 않는다.
- 수정 후 B 홈 재생성은 **26조각/12턴/1회로/총 4,687.678mm, 발열 13/4,255.385mm, 적용 외곽 27.7mm·직경 0.30mm**로 성공했고 A의 23/11/8,207.764mm는 보존됐다. `repro-b-exclusion-surface.json`은 성공 회귀 시나리오로 갱신해 최신 DLL로 20/20 Completed였다. fixture failure 2는 `resolved: true`와 기대값·원인을 기록했고 `RemainingFailureFixtureChecks`는 resolved 항목에서 마지막 일치 단계의 expect를 비교한다.
- **검증:** App 테스트 190 통과, WorkingSide EXITCODE=0 — 30 sidebar, 30 area highlight, 19 direct exit-point, 32 vertex edit, 28 split boundary, 340 remaining failure fixture, 3 electrical refresh, 43 pattern state, 63 geometry rollback, 20 project load, 51 project load exclusion, 187 working-side. 재현 1·2 실행 DLL SHA-256: App `BD49288672C613B7F78C8F16B28C45522B70DC3AE54EDF4D55CB0C72F3875022`, Core `2D9CA87B3302C36EFB3A7D05C5B1C310A5491231085D3C013F938F72ABD0083E`, OccBridge `D5E5ECE7B37039437DD2BEF5C48F69BFC6A7300DD8F8073B8B801773991BE9AE`.
- **미검증:** 실제 마우스로 이 두 실패 경로를 클릭하는 것, 새 프로세스에서의 복원, 화면 육안 판정. 다음 단계는 5단계(간격 분석 누락·부분 성공 제거)이며 아직 시작하지 않았다.

## RemainingWork 5단계 완료·6-1 승인 (2026-11-13)

- **5단계 완료(자동검증).** 간격 분석을 설정 간격·직경 그룹 단위에서 같은 작업평면 전체로 넓혔다. 분석 입력은 영역 ID·조각 번호·확정 간격·직경·생성 방향을 보존하고, 결과는 방향 그룹별 쌍(min/max)을 제공한다. 직경이 다른 쌍은 `중심선 거리 − (d1 + d2) / 2`로 계산한다.
- **평행 판정과 그룹 가입.** 평행은 0.1°(180° 모듈로)이며 그룹 가입은 구성원 전체와의 평행을 요구한다. 대표 각도 하나만 비교하면 0°, +0.09°, −0.09°가 한 그룹에 섞여 서로 0.18° 차이나는 레인까지 비교되는 전이 결함이 생긴다(독립 리뷰 지적, 수정·경계 테스트 추가).
- 방향이 다른 배치만 있으면 `DirectionSeparated`로 보고하고 수치 정상·비정상을 만들지 않는다. 방향 그룹이 둘 이상이면 요약에 "서로 비교하지 않았습니다"를 덧붙인다.
- **누락의 경고 승격.** 표시 중 영역의 패턴·조각 ID/길이·확정 입력·native chain·좌표 누락은 조용히 건너뛰지 않고 사유를 모아 경고로 실패시킨다. 일부 수집 실패 시 부분 결과를 게시하지 않는다. 수집 로직은 `SpacingReviewCollector`(App)로 분리해 단위 테스트한다. 레거시 4인자 분석 API는 의미를 그대로 유지한다.
- 분석 지문은 마커 갱신 성공 이후에만 게시한다(실패 시 빈 문자열 유지). 이 순서는 자동 테스트 없이 코드 검토로만 확인했다.
- **검증(2026-11-13).** Core 245, App 200 통과. WorkingSide EXIT=0, 총 846 체크, 그룹 실패 0 (sidebar 30, area highlight 30, direct exit 19, vertex edit 32, split boundary 28, remaining fixture 340, electrical 3, pattern state 43, geometry rollback 63, project load 20, project load exclusion 51, working-side 187). 로드 DLL SHA-256: OccBridge `D5E5ECE7…E9AE`, App `F4840977…5B9F`, Core `C400AB9D…CC9E`.
- **픽스처 보강 기록:** `GeometryRollbackChecks`의 합성 영역이 `PatternDisplayed=true`인데 패턴·적용 입력이 없어 새 수집기가 정상 경고를 냈다. 실제 제품은 생성·복원 모두 두 정보를 채우므로 픽스처를 보강했고 제품 규칙·기대 문구는 그대로다.
- **6-1 사용자 확정(2026-11-13): 레인 쌍마다 최소 간격 한 곳만 표시하는 현재 동작을 유지한다.** 최대·변동 구간 표시는 추가하지 않는다. 검토 탭에 별도 간격 점검 구역을 만들지 않는다(기존 확정).
- **6-2 미착수.** 지문에 모델·작업 컨텍스트·프레임·전체 중간 좌표를 포함하고 실제 비교, UI 스레드/native 분리, 백그라운드 분석, 늦은 결과 미게시, 삭제·초기화·복원·종료 정리, 표시 실패 경고 유지, 임의 표본 확대 금지가 남아 있다.
- **미검증:** 실제 마우스·화면 육안, 새 프로세스 복원, 외부 CAD 출력, 6-2 전체. `e2e-split-safety` B-1 기대값(4,280.265 mm)은 7단계에서 실측 재확정 대상이다.

## RemainingWork 6단계 완료 (2026-11-13)

- **6-2 완료(자동검증).** 분석 지문(`SpacingAnalysisFingerprint`, 정책 `spacing-v3`)은 정책 버전·모델 리비전·작업 컨텍스트 ID/리비전·작업평면 프레임·영역/조각 identity·확정 간격·직경·방향·색·중간점 포함 전체 2D 좌표를 담는다. 지문은 캐시 재사용과 게시 시점에 실제로 비교한다.
- **스레드 분리.** native chain 조회와 화면 좌표 변환·마커 교체는 UI 스레드, 순수 2D 간격 분석과 마커 후보 산출은 불변 스냅샷을 받은 백그라운드 작업에서 수행한다. 게시 직전 세대 번호와 지문을 다시 대조해 늦게 끝난 이전 결과를 버리고, 같은 지문의 직전 결과는 재분석 없이 재사용한다(`SpacingReviewCoordinator`).
- **정리 경로.** `ClearAllAreas`(영역 삭제·전체 초기화·모델 교체·프로젝트 복원 공통)와 `OnClosing`에서 대기 분석을 취소하고 마커를 지우며 지문·요약을 비운다. 표시 실패는 경고만 남기고 확정 기하를 롤백하지 않는다. 표본 간격 1 mm는 그대로다. 수집 시간과 분석 시간은 분리 계측한다.
- **취소 지원:** `RouteSpacingAnalyzer.Analyze`가 선택적 `CancellationToken`을 받고 레인마다 취소를 확인한다. 교체되는 취소 토큰 소스는 취소 후 Dispose한다.
- **독립 리뷰 3건:** 중간(분석 내부 취소 미확인), 낮음(토큰 소스 미정리)은 수정·재검증했다. 낮음(비동기 취소·늦은 완료·종료 후 콜백 통합 테스트)은 **미해결** — App 테스트가 `MainWindow`를 생성하지 않는 제약 때문에 코디네이터 동기 검증과 Core 취소 경계 테스트로만 고정했다.
- **검증:** Core 246, App 206 통과. WorkingSide EXIT=0, 846 체크, 그룹 실패 0(5단계와 동일 구성). 로드 DLL SHA-256: OccBridge `D5E5ECE7…E9AE`, App(회귀) `5CC2E2D7…D7AFE`, Core(회귀) `877B7B93…B713E`.
- **미검증:** 실제 마우스·화면 육안(마커 위치·가독성, 백그라운드 전환 깜빡임), 대량 입력 체감 지연·시간 실측, 새 프로세스 복원, 외부 CAD 출력, 시각테스트 5개 전체(§0.5). 다음은 7단계(시나리오 5개 실측 재확정·각 2회)이다.

## RemainingWork 7단계 완료 (2026-11-13)

- **5개 시나리오 × 2회 모두 Completed.** 새 실행 폴더 A/B에서 `e2e-full` 36/36, `e2e-multi-area` 54/54, `e2e-margin-spacing-exit` 59/59, `e2e-split-safety` 42/42, `e2e-split-four` 20/20. 실패 단계 0.
- **결정성:** 단계 이름·성공 여부·단계 데이터가 두 실행에서 동일하다. 차이는 산출물 경로와 DXF 헤더 `$TDUPDATE`(시각) 때문에 생기는 파일 바이트 수 3~4 B뿐이다. 엔티티·영역·회로·조각 수는 동일하다.
- **2단계 이후 실측 재확정(측정 전 기대값 수정 금지 준수, 시나리오 JSON 3개만 변경).**
  - `e2e-full` 3분할: A1 20조각/9턴/6,746.533 mm, A2 16/7/5,405.521, A3 24/11/6,250.376, 2D 1.251. 복원 A1 동일.
  - `e2e-split-safety`: B-1 4,276.956 mm/발열 4,135.48, B-2 4,465.184/4,324.503.
  - `e2e-split-four` A 4분할: A-1 2,689.544, A-2 16/7/5,395.236(5,289.263), A-3 16/7/5,424.17(5,279.503), A-4 20/9/4,883.415(4,544.904), 2D 2.006(보고 2.0168).
- **schema 2·`marginMm` 거부 유지:** App 테스트 `Load_RejectsSchema2WithMigrationMessage`가 통과한다(App 206 전체 통과 상태).
- **줄어든 검증 범위:** `e2e-split-four`는 세로 B 4분할 → 가로 A 4분할로 대상이 바뀌었고 편차가 이전 B 기준 1.0931보다 나쁜 2.0168이다. 현 정책(절단 위치만 탐색·편차 표시 후 확정)상 결함으로 되돌리지 않고 수치만 기록한다. `e2e-margin-spacing-exit`는 B 하단 제외영역 블록이 초안 변경 테스트로 대체된 범위를 유지한다.
- 7단계에서 제품 코드는 변경하지 않았다. 다음은 8단계(최종 전체 검증·완료 보고)다.

## RemainingWork 8단계 완료 (2026-11-13)

- **최종 검증 완료.** 네이티브 Release/x64 빌드 SHA-256 `D5E5ECE7…E9AE`, Core 테스트 246 통과/0 실패, App 테스트 206 통과/0 실패, WorkingSide EXIT=0 846 체크(그룹 실패 0), 시나리오 5개 × 2회 모두 `Status=Completed`(36/54/59/42/20단계, 실패 0). 두 실행의 단계 데이터 차이는 산출물 경로와 DXF `$TDUPDATE`(시각) 때문에 생기는 `dxf`/`path`/`bytes`뿐이다.
- **로드 DLL:** App 테스트 출력 `HeatingMold.App.dll` `13FCF6A6…3FDD`, `HeatingMold.Core.dll` `877B7B93…B713E`, `OccBridge.dll` `D5E5ECE7…E9AE`.
- **최종 독립 리뷰 수정 2건(8단계에서 App 코드 변경):**
  - 중간: 최소 간격 표본이 두 번째 레인에 놓여도 마커가 첫 레인의 영역 ID·조각 번호·색을 쓰던 문제. `SpacingPairSample`에 `AtRunIndex`를 추가하고, 표본이 놓인 레인의 소유 정보와 반대 레인 번호를 쓰도록 고쳤다. 마커 위치·간격 수치는 그대로다.
  - 낮음: 프로젝트 복원 완료 문구가 간격 표시 실패도 "경로 길이 라벨을 표시하지 못했습니다"로 표시하던 문제. 실제 경고 문구를 그대로 덧붙이도록 고쳤다.
  - 수정 후 App 206, WorkingSide EXIT=0, 시나리오 10회를 다시 실행해 모두 통과했다.
- **두 원래 실패 처리:** 세로 B 4분할은 마지막 레인 반대편에 발열 구간이 없어 정상 거부로 판정하고 재현 시나리오 기대 문구를 고정했다. 경계 위 정확한 점의 표면 매핑 실패는 경계 provenance로 수정해 성공 회귀 시나리오로 갱신했다(4단계).
- **성능:** 수집/분석 시간을 분리하는 계측 필드만 구현했고 실측값은 측정하지 않았다. 개선 배율을 주장하지 않는다.
- **미검증:** 실제 마우스 조작, 화면 가독성·줌/DPI, 새 프로세스에서의 복원, 외부 CAD에서 DXF/STEP 열기, 대량 입력에서의 체감 지연. 승인 대기 항목은 없다(6-1 확정 완료).

## 발열사 케이스는 생성 입력, 검토 행은 유지 (사용자 확정 2026-11-13)

- **사용자 확정:** 발열사 케이스는 경로를 만들 때 정하는 생성 입력이다. 선택 상자는 경로 탭에 두고(표시명 `발열사 케이스 — 전체 영역 공통`), 검토 탭에는 두지 않는다. 컨트롤 이름 `WirePresetBox`, 이벤트 `WirePreset_Changed`, 자동화 이름은 그대로 유지한다.
- **사용자 확정:** 생성 후 케이스를 바꿔도 확정 경로·길이·인출점·수동 편집을 지우거나 자동 재생성하지 않는다. 직경이 적용값과 달라지면 그 영역은 `재생성 필요`가 되고, 검토 표는 **회로 ID와 확정 실측 길이를 유지**한 채 전력·저항·전압·전류를 공란, 판정을 `—`, 비고를 `케이스 변경 — 재생성 필요 · 이전 확정 길이`로 표시한다. 원래 케이스로 돌아오면 다른 입력 변경이 없는 한 재생성 없이 계산 표시가 복원된다.
- 케이스 변경 처리는 표·영역 상태·생성 힌트·분할/편집 버튼·내보내기 안내를 같은 시점에 갱신하고, 재생성 필요가 되면 진행 중 꼭지점 편집을 종료한다. 생성 당시 적용 직경을 새 직경으로 덮어쓰지 않는다.
- 구현: `ReviewSnapshot.DisplayCircuits`/`DisplayReason`은 **표시 전용** 확정 회로 목록이고, `ElectricalReviewRows`가 두 표에 같은 행을 만든다. 계산·합계·내보내기 자격은 계속 `ReadyAreas`/`Table`/`CanExport`가 판정하므로 **행이 보인다고 분할·편집·DXF/STEP 자격이 생기지 않는다.** 전기 입력 오류·계산 대상 없음일 때도 회로 ID·길이는 남기고 합계를 0 W로 표시하지 않는다.
- 전력 배분·3,500 W 표시·계산식·프로젝트 schema·네이티브 API는 이 변경에서 바꾸지 않았다.
- 검증: WorkingSide 회귀 832 PASS·그룹 실패 0(신규/수정 7건 포함), App 테스트 206, Core 테스트 246 통과. 시각테스트 5개를 `Run.ps1 -NoBuild -HoldSeconds 1`로 재실행해 모두 `Status=Completed`·실패 단계 0이었다(`e2e-full` 36, `e2e-multi-area` 54, `e2e-margin-spacing-exit` 59, `e2e-split-safety` 42, `e2e-split-four` 20단계).
- 시각테스트 실행 환경: 앱 `bin\x64\Release\net8.0-windows`에는 DLL 7개뿐이고 OCCT 런타임이 없으므로 `tests\HeatingMold.App.Tests\bin\x64\Release\net8.0-windows`를 `PATH` 앞에 두어야 한다(없으면 모델 로드 전에 프로세스가 종료되어 `run.json`만 남고 RC=3). `tests\WorkingSide.Regression` 하네스의 첫 인자는 `tests\WorkingSide.Regression` 폴더이며 `layers.stp` 등 `.stp` 픽스처가 그 폴더 바로 아래에 있어야 한다. 이 픽스처는 추적 대상이 아니므로 검증 후 지운다.

## 인출점 선택 실패: 원본 parameter 직접 투영으로 교체 (사용자 보고·구현 2026-11-14)

- **사용자 보고:** `C:\Users\yong753\Desktop\발열몰드 저장테스트\261116_Test.json`(schema 7, 기본 예제 `Mold_Cover_Trim.step`, 영역 A 59면·영역 B 29면)에서 인출점 선택 자체가 되지 않고 `원본 CAD 인출 경계를 평가하지 못했습니다.`가 표시됨. 저장된 원본 외곽선(단일 폐합 루프)과 작업 프레임은 정상이고, 생성 경로·선택 인출점은 저장되어 있지 않았다.
- **확인된 원인(재현):** 기존 `BuildProjectedCurve`가 `GeomProjLib::ProjectOnPlane(KeepParametrization=true)`의 결과 곡선과 원본의 D1 투영을 17개 표본에서 점·미분 residual `1e-7`로 비교했다. 작은 경사 원호(예: A edge22/23, B edge58/61/62/63)는 라이브러리가 BSpline 근사로 바꾸면서 미분 residual이 `1.1e-7`~`5.2e-3`로 커져 `GeometryFailure`가 되었고, `CadExitBoundary2D.Project`가 **모든 원본 경계를 먼저 조회한 뒤 하나라도 실패하면 전체 선택을 거부**해서 클릭 위치와 무관하게 인출점 선택이 막혔다. A에는 추가로 작업평면에서 한 점으로 접히는 실제 3D 선(edge21, 길이 0.0083mm)과 A edge45의 endpoint/extremum 중복 후보(parameter 차 3.7e-9)가 있었다. 제외영역으로 제거되는 경계도 필터 전에 조회되지만, 이 파일에서는 **남아 있는** 유효 경계에도 문제가 있어 제외 필터만으로는 해결되지 않는다.
- **구현(네이티브):** `src\OccBridge\ProjectedExitCurveNative.inc`를 재작성했다. 별도 근사 곡선 대신 `ProjectedCurveAdaptor : Adaptor2d_Curve2d`가 **원본 곡선을 같은 parameter로 평가해 작업평면 X/Y에 직접 내적**한다(D0~DN, Trim, Continuity/Intervals는 `GeomAdaptor_Curve` 위임, `GetType=GeomAbs_OtherCurve`). `BuildProjectedCurve`는 더 이상 투영 곡선을 만들지 않고, 원본 trim의 3D bbox 꼭짓점을 투영한 **보수적 2D bound**와 `collapsed`(두 extents ≤ 1e-7)만 계산한다. 최근접은 `Extrema_ExtPC2d`(동일 adaptor, 명시 구간), 교차는 `Geom2dInt_GInter` + explicit `IntRes2d_Domain`(닫힌 곡선은 `SetEquivalentParameters`)을 쓴다. 직선의 endpoint/extremum 중복은 `SnapToEndpoint`가 **원본 3D 점이 같은 경우에만** endpoint로 합친다(전역 parameter epsilon 확장 없음). 원형 중심 모호성·접선/겹침·같은 2D 다른 깊이 모호성은 그대로 유지한다.
- **질의 정책 분리:** `src\OccBridge\ExitEdgeQueriesNative.inc`에 보수적 bbox 도달 판정(`ExitCurveMayReachPoint`, `ExitCurveMayHitLine` slab)을 추가했다. 원본 참조 검증(stale/missing/invalid)은 항상 먼저 실패한다. 그 뒤 **이 질의가 도달할 수 없는 접힘 경계는 `Miss`**, 도달하지만 접힌 경계는 `DegenerateProjection`을 반환한다. 저장 parameter 직접 평가(`EvaluateExitEdgeParameter`)는 접힌 경계라도 `DegenerateProjection`으로 실패하며 Miss로 숨기지 않는다. 저장 trim·edge identity·parameter 계약과 공차는 변경하지 않았다.
- **부수 수정:** `src\OccBridge\ExitEdgeMatchingNative.inc`가 `GCPnts_AbscissaPoint`를 직접 include하도록 고쳤다(기존에는 투영 헤더의 간접 include에 의존해 픽스처 단독 컴파일이 실패).
- **회귀 갱신:** `tests\WorkingSide.Regression\Fixtures.cpp`의 normal 정렬 직선 기대값을 새 계약(빌드 Hit + `collapsed`, 그 점을 질의할 때만 `DegenerateProjection`)으로 바꾸고, 작은 경사 원호의 빌드/parameter 평가 검사를 추가했다. 기존 원형·겹침·폴드 모호성·sewing 검사는 그대로다.
- **검증(2026-11-14):** 사용자 파일·프레임으로 네이티브 하네스를 실행해 A/B 모두 원본 외곽선 1개 폐합 루프(A 147점/45참조, B 142점/46참조)를 확인했고, 저장 참조 중간점 조회에서 A/B **fail=0**(수정 전 4/5), 각 참조의 2D 중간점 조회에서 실패는 A edge21/24·B edge21의 접힘뿐이었다. 클릭 재현에서 A는 정확히 접힌 점 `(-630,-33.022)`만 `DegenerateProjection`, 나머지 경계점은 모두 성공했고 B는 전 지점 성공했다. `tests\WorkingSide.Regression` **860 PASS/0 FAIL/그룹 실패 0**(split boundary 28, working-side 187, direct exit 19 포함), App 테스트 **207 통과**, Core 테스트 **246 통과**. 시각테스트는 `e2e-split-safety`·`e2e-multi-area`·`e2e-margin-spacing-exit`가 `Completed`(실패 0)였다.
- **기존 실패(이 변경과 무관):** `e2e-full`(25단계 A-1 16 vs 기대 20)과 `e2e-split-four`(17단계 A-1 12 vs 기대 8)는 **수정 전 DLL `3136A92B…0F7F`로도 동일한 값**이 나와, 2026-11-13의 레인 묶음 분할 커밋 이후 갱신되지 않은 시나리오 기대값 때문임을 확인했다. 이번 변경에서 시나리오 JSON은 수정하지 않았다.
- **빌드 함정(재확인):** `dotnet test -p:OutDir=x64\WorkingSideBuild`가 관리 빌드 중 **오래된 `OccBridge.dll`을 WorkingSideBuild로 복사**해 16개 테스트가 `MissingMethodException: IntersectExitEdgeLine`으로 실패했다. 관리 빌드 후 `x64\Release\OccBridge.dll`을 `x64\WorkingSideBuild\`와 앱 `bin\x64\Release\net8.0-windows\`에 다시 복사한 뒤 `dotnet test <dll>`(빌드 없이)로 실행해야 한다.
- **로드 DLL:** 새 `OccBridge.dll` 4,583,424 B `7E505E32C50127E7B5639806EB74087BFFEE54F50F86D66D60DD3C2D98980E20`(구 `3136A92B…0F7F` 4,572,160 B). App/UI 코드·프로젝트 schema·DXF/STEP 출력은 변경하지 않았다.
- **미검증:** 실행 중 앱에서 실제 마우스로 인출점 클릭, 다른 CAD 모델, `e2e-full`/`e2e-split-four` 기대값 재측정, 실제 마우스 분할·저장/재열기.

## 0.2mm 발열사 외곽 간격 생성의 B 3D 스테이징 실패 (사용자 보고·구현 2026-11-14)

- **사용자 보고:** `C:\Users\yong753\Desktop\발열몰드 저장테스트\261116_Test.json`(schema 7, 기본 예제 `Mold_Cover_Trim.step`, 영역 A 59면·영역 B 29면)에서 외곽 간격 `0.2`mm로 생성할 때 `영역 B 경로 조각 181/4488 (HeatingRun) 3D 스테이징 실패 ... off-surface 3.003734`가 발생했다. 저장된 JSON에는 선택 인출점·패턴·경로가 없고 두 영역 모두 `spacingText="5"`였으므로, 실패 세션의 인출점·0.2mm 입력은 저장되지 않았다. 사용자 JSON/STEP은 수정하지 않았고 테스트 사본·산출물은 `C:\Users\yong753\AppData\Local\Temp\opencode\hm-02mm\`에만 두었다.
- **재현:** 오프스크린 WPF 하네스에서 `MainWindow.LoadProject`로 사본을 열고 제품 함수 `TryResolveExitPoint` → `TryEvaluateSelectedExit3D` → `ApplyPickedExitPoint`로 인출점을 고른 뒤 `GenerateMultiFace`를 실행했다. 영역 B 시작 `(-630,-204.5086)`, 종료 `(630,-52.2136)`, Case1(Ø0.3/4Ω/m), FrameX, 0.2mm에서 **조각 379/614 HeatingRun**이 `off-surface 6.314979`로 재현됐다. 이는 사용자의 181/4488과 같은 예외 경로이며 조각 번호는 인출점 선택에 따라 달라진다.
- **확정 원인(증거):** 실패 표본 137의 3D 점 `(-3984.548138,-953.205011,1939.574219)`은
  - `BRepExtrema(vertex, g_faces[45])` 거리 **6.314979**,
  - 원본 surface 투영 거리 **0.000000**,
  - `BRepTopAdaptor_FClass2d(face 45, uv=(0.949609,0.906570), UseBnd=true)` = **IN**,
  - 모델 전체 최근접 거리 **6.314979**, 면 45의 외곽선까지 거리 **6.314979**.
  즉 `IntCurvesFace_ShapeIntersector`가 면 45의 **trim 바깥 untrimmed surface**를 교차점으로 반환했고, 그 점은 면의 surface 위·pcurve trim 안에 있으므로 `BRepExtrema`의 trimmed-face 거리만으로는 유효점을 바깥으로 오판한다. 같은 데이텀의 재조회도 같은 점을 돌려주며, Core 매핑에서 좌표/FaceId가 뒤섞인 증거는 없었다. `BRepExtrema`를 공차 확대나 최근접 면 스냅으로 덮지 않는다.
- **수정(네이티브):** `src\OccBridge\WorkingSideNative.inc`에 `PointOnSupportFaceNative`를 추가했다. 빠른 경로는 기존 `BRepExtrema` 0.1mm이고, 실패했을 때만 `BRep_Tool::Surface(face)`에 `GeomAPI_ProjectPointOnSurf`로 모든 해를 투영해 `거리 <= 0.1mm`와 `BRepTopAdaptor_FClass2d::Perform(uv, true)` IN/ON을 함께 만족하는 해가 있으면 유효 표본으로 인정한다. 허용치를 늘리지 않고, 최근접 면으로 대체하지 않으며, compound/부분 결과를 쓰지 않는다. 스테이징 실패 문구에는 실패 표본 인덱스·face·faceDistance·surfaceDistance·trimState·xyz가 남는다. `src\OccBridge\OccBridgeApi.cpp`에 `BRepTopAdaptor_FClass2d.hxx`, `GeomAPI_ProjectPointOnSurf.hxx` include를 추가했다.
- **STEP 출력 수정(같은 0.2mm 결과를 끝까지 내보내기 위해):**
  - `src\OccBridge\StepExportAreaNative.inc`: 한 면을 여러 영역이 나눠 갖거나 제외영역이 면을 가로지르면 유효 외곽선 합집합이 원본 면을 덮지 않는 것이 정상이다. 원본 면적보다 **작은** 조각 합은 허용하고(면별 `face-clip` 로그), 원본보다 **큰** 조각(중복/프리즘 오류)과 조각이 하나도 없는 경우만 `area-face-area`/`area-nofaces`로 실패한다.
  - `src\OccBridge\StepExportGeometryNative.inc`: 긴 곡선/꺾임 조각의 fit 한도를 `PiecePoleLimit 32→512`, `FitSegments 24→96`, `FitIterations 2→6`으로 늘렸다(편차 0.10mm·길이 1.0mm·순서 검사는 그대로). B-spline fit이 편차를 넘거나 pipe shell이 실패하면 확정 샘플 폴리라인을 중심선으로 쓰고, 폴리라인 pipe shell(전이 모드/프레임 조합) 또는 원기둥+구를 fuse한 **단일 SOLID**로 폴백한다. compound 폴백은 없다. `centreline`은 edge 또는 polyline wire를 받는다.
  - `src\OccBridge\StepExportNative.inc`: 중심선 재읽기 검사를 `EDGE == 1 && VERTEX == 2`에서 `EDGE >= 1 && VERTEX >= 2`로 바꿨다(폴리라인 중심선 허용). SOLID 1개·valid·volume>0 검사는 그대로다.
- **재현·검증(2026-11-14):**
  - 네이티브 Release/x64 빌드 0 errors, 솔루션 Release/x64 빌드 0 errors.
  - `tests\WorkingSide.Regression` **EXIT=0, 869 PASS, 그룹 실패 0**(기존 860 + 신규 `SupportFaceChecks` 7 + kinked STEP 2). `SupportFaceChecks`는 실제 기본 예제와 저장 프레임 `origin=(-3966.6891533290936,-843.1964433803546,1455.9999999778886)`, `axisX=(0,0,-1)`, `axisY=(0,1,0)`, `normal=(1,0,0)`에서 B 면 집합의 실패 데이텀 `u=-483.57421875, v=-110.00856769355369`을 스테이징해 성공을, 법선 방향 1mm 이동점을 거부를 확인한다.
  - Core 테스트 **246 통과**, App 테스트 **207 통과**(`x64\Release\OccBridge.dll`을 App.Tests 출력에 복사한 뒤 `--no-build`; 복사하지 않으면 이전 DLL의 `MissingMethodException: IntersectExitEdgeLine`이 난다).
  - 0.2mm 제품 경로(오프스크린, 같은 실행): 영역 A **302조각/151레인/150턴/1회로/200,183.084mm**, 영역 B **614조각/307레인/304턴/1회로/385,335.263mm**, 둘 다 `PatternDisplayed=true`. A는 하네스가 제품 `TryGenerateSelectedExitPattern`과 표면 질의로 고른 인출점 `(-630,68.1084)→(630,-7.1195)`(75.2mm 스윕)을 썼고, B는 `(-630,-204.5086)→(630,-52.2136)`(152.3mm 스윕)을 썼다. **사용자의 저장되지 않은 실제 인출점은 알 수 없으며**, 전체 스윕을 덮는 다른 인출점 조합은 여전히 `RequiredExtraLaneOutsideRegion` 또는 실제 표면 gap의 Miss로 실패할 수 있다. 이번 수정은 보고된 B의 false-hit 스테이징 실패를 고친 것이지 모든 0.2mm 인출점 조합을 보장하지 않는다.
  - 프로젝트 저장: `hm02-saved.json` 20,353,858B(schema 7), 같은 실행 재로드 후 A/B 조각 수·길이 합 **완전 일치**(A 302/200,183.084, B 614/385,335.263).
  - DXF: 8,122,916B, 재읽기 레이어 엔티티 `HEAT_WIRE=916`, `HEAT_AREA=2`, `ELECTRICAL_SPEC=38`, `HEAT_LENGTH_LABEL=1778`, `MOLD_OUTLINE=232`, `DIMENSION=2`.
  - STEP: 432,298,474B, 네이티브 `VerifyStepDocumentNative` 재읽기 검증 통과(`ExportStepCore` Success). 0.2mm 간격의 916조각 때문에 파일이 매우 크다.
  - 로드 DLL: `OccBridge.dll` 4,608,000B `4AF937507B7102066D0FAF49B49DA3F14BD8A783EF56911AFC9E8331D2C79B45`; WorkingSideBuild 앱 `1B7D7B235C9C9128E732CA066DE651FED22BA6CCC50CE30279974DBA130CDBC2`, Core `F151F4BC805747C5CD2A9FE6BC82428C86D2971EEBB60CCB70F2A8D500DB2203`; App 테스트 출력 앱 `A97F7FE86E07721E40C1EA5A8238D2CB2575B52C6689BD40C00A8B190E2A97E3`.
- **남은 한계:** B의 4488조각 원본 세션은 인출점이 저장되지 않아 그대로 재현하지 못했다. 실행 중 앱의 실제 마우스 클릭·분할·화면 육안, 새 프로세스에서의 복원, 외부 CAD에서 432MB STEP 열기, 전체 스윕 인출점 조합의 성공은 미검증이다. `e2e-full`·`e2e-split-four`의 기존 기대값 불일치는 이번 변경과 무관하게 그대로다.

## 제외영역 생성의 레인 구간 좌표 보존 (사용자 보고·구현 2026-11-14)

- **사용자 보고:** `C:\Users\yong753\Desktop\발열몰드 저장테스트\261116_Test2.json` 영역 A(59면, FrameY, 외곽간격 0.2 / 직경 0.3, 제외 사각형 3개, 저장 인출점만 있고 경로는 없음)를 생성하면 `시작 인출점이 마진 안쪽 사행 구간의 진행축 끝에 있지 않습니다`로 거부됐다.
- **실제 앱 경로 재현(오프스크린, 제품 함수 호출):** `LoadProject` → 영역 A 활성화 → `TryGenerateSelectedExitPattern` / `Generate_Click`. 저장 시작점 CAD parameter 재평가 성공·좌표차 `dPoint=0`(edgeKey 29, parameter −106.300559161514). 시작 local X `68.1962115594576`, 차감 전 첫 행 끝 `68.1962115593953`(차이 6.2e-11, 통과), **Boolean 차감 결과 첫 행 끝 `68.1978690437183`(안쪽 0.0016574843 mm, `-1e-6` 검사 실패)**.
- **확정 원인:** 제외영역 생성 경로가 `RegionOps.BuildGenerationRegion` = `Clipper.Difference`(정밀도 기본 2 = 0.01 mm 격자) 결과를 **레인 구간의 수치 근거**로 썼다. 시작점은 원본 외곽선 위에 있는데 차감 결과 끝이 격자로 이동해 안쪽으로 오판했다. 제외영역이 없는 동일 조건은 원래도 성공한다.
- **구현:** 신규 `src\HeatingMold.Core\Geometry\LaneIntervalScan2D.cs`. 차감 전 외곽선의 행 교차 구간을 구하고, 그 행을 실제로 가로지르는 제외 사각형만 1D로 차감하며, 조각마다 `ParentIntervalIndex`/원래 양 끝(`ParentStartMm`,`ParentEndMm`)을 보존한다. 짧은 구간 기준은 소비자 주입(생성기 1e-9). `Serpentine.TryBuildExcludedSequentialPieces`가 이 계산을 쓰고, 같은 CAD 구간 여부를 좌표 재비교(`FindContainingInterval`, 삭제됨)가 아니라 lineage로 판정한다. 레인 행 범위와 경계 추종 연결도 차감 결과가 아니라 원본 외곽선을 쓴다.
- **유지:** 중심 피치(spacing+직경), 레인 순서·측면 선택, 마지막 추가 레인 규칙, TurnConnector/FixedConnector 의미, 단일 회로, 3D 표면 검증·원자적 커밋, 프로젝트 schema. 공차·Clipper precision·시작점 좌표는 바꾸지 않았다.
- **문구:** `AnchoredFailureReason.AnchorNotAtLaneEnd` 안내에서 제거된 기능을 가리키던 "마진 안쪽"을 삭제해 "시작 인출점이 첫 발열레인의 진행 방향 끝에 있지 않습니다 — …"로 바꿨다.
- **검증:** Core 테스트 **254 통과**(신규 `LaneIntervalScanTests` 4, `SelectedExitTests` 4 — 반올림 전/후 대조, 0.002·0.01·0.02 mm 실제 내부점 거부). App 테스트 **207 통과**(`x64\Release\OccBridge.dll`을 테스트 출력에 복사 후 `--no-build`). `tests\WorkingSide.Regression` **EXIT=0** — 31 sidebar, 30 area highlight, 19 direct exit, 32 vertex edit, 28 split boundary, 340 fixture, 3 electrical, 48 pattern state, 63 geometry rollback, 7 support-face, 20 project load, 51 project load exclusion, 189 working-side.
- **실제 사용자 파일(사본, 2D):** 새 계산기 첫 행 `[-36.1016837183456, 68.1962115593953]`, gap `+6.2e-11` 통과. `TryGenerateSelectedExitPattern` **성공**, 2,500레인 / 5,000조각 / 2D 총 633,614.49 mm. 실행 DLL: App `5A7F99CA…9990` 605,696B, Core `F52418D4…3E15` 219,136B, OccBridge `4AF93750…9B45` 4,608,000B.
- **3D 종단은 아직 실패한다(별개 결함, 새로 노출됨).** 같은 프로젝트·같은 인출점으로 간격 2 mm에서 `Generate_Click`을 실행하면 2D는 통과하지만 `영역 A 조각 104/1088 (TurnConnector) 3D 스테이징 실패 — The path leaves the selected surface domain.`로 거부된다. 원래 2D에서 막혀 3D에 도달하지 못했으므로 이전에는 보이지 않던 실패다. TurnConnector 끝점은 차감 전 외곽선 극값이라 이번 변경으로 달라지지 않으므로 회귀가 아니라 기존 한계로 판단하지만, **증명하지는 않았다**. 0.2 mm(5,000조각) 3D 스테이징은 관찰 시간 안에 끝나지 않아 미검증이다.
- **미구현(계획 대비):** 첫 행을 원본 CAD edge/parameter의 **정확 행 교차**로 재구성하는 단계는 구현하지 않았다. 따라서 첫 행 권위는 Boolean 차감 결과가 아니라 **원본 표본 외곽선**이며, 직선 경계에서는 정확하지만 곡선 경계에서는 표본 chord 수준이다. 시작점 자체는 `ExitBoundaryConstraint.EvaluateSaved`로 원본 CAD parameter에 대해 계속 검증한다.
- **미검증:** 실제 마우스 클릭, 화면 육안, 새 프로세스 복원, 저장/재열기, DXF/STEP 출력, 분할 영역의 제외 재생성, 다른 제외 배치·곡면 영역.

## 검증 실행 가드 (Validation Guard) — 구현·자동검증 2026-11-15

- **사용자 요구(원문 요지):** "서브에이전트가 검증할 때 프로그램을 연 다음 … 오류가 날 경우 메시지박스가 뜬 다음에 닫힐 때까지 기다리거나 timeout 될 때까지 기다리고 있어. … 이거 어떻게 개선할 수 없어?" + "지금은 내가 직접 닫고있거든". 부모 답: 예상치 못한 모달을 감지하면 오류 기록 후 **해당 검증용 프로세스만** 즉시 실패 종료, 자동 확인 클릭/오류 무시 성공 금지, 일반 제품 사용 영향 없음.
- **구현 위치(제품 App/네이티브 코드 변경 없음):** `tools\validation\GuardedProcessHost.cs`(실제 감시·판정·수집, `Add-Type` 컴파일), `tools\validation\GuardedProcess.ps1`(`Invoke-GuardedProcess` 함수), `tools\validation\Invoke-GuardedProcess.ps1`(CLI). 문서 `docs\ValidationGuard.md`, `tools\validation\README.md`, 회귀 `tests\ValidationGuard\`.
- **프로세스 소유 범위:** root를 `CREATE_SUSPENDED`로 만들고 private job object에 assign한 뒤 resume한다. 자식은 job에 자동 포함된다. 창 소속 확인에는 PID를 쓰지만, **종료 범위는 job으로만 제한하며 프로세스 이름으로 전체 종료하지 않는다.** 감시 중에는 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`를 켜서 가드 host가 외부 timeout/Ctrl+C/강제 종료되면 추적 트리도 함께 정리되게 한다. 성공 시에는 **성공 보고서(JSON/TXT) 게시를 kill-on-close가 켜진 상태에서 먼저 끝내고**, 플래그가 여전히 켜져 있는지 재조회한 뒤 **마지막 단계로** 해제·재조회하고 handle을 닫는다. 따라서 보고서 I/O 도중 host가 죽어도 고아 앱이 남지 않는다. detach 실패는 `job-detach` 실패로 처리하고 kill-on-close 재설정을 시도한 뒤 트리를 정리한다. 정리 확인은 **job query가 실제로 빈 목록을 반환했을 때만** 인정하며, `OpenProcess` 실패(access denied/unknown)를 종료로 간주하지 않는다. `trackedProcessIds`(초기 추적 증거)와 `remainingProcessIds`(정리 후 남음/미확인), `cleanupStatus`를 구분해 기록하고 미확인 시 "정리 완료"로 단정하지 않는다.
- **모달 감지 규칙(200~300ms, 기본 250ms):** 추적 트리 소속 + visible top-level 창 중 ① 표준 `#32770` ② owner가 비활성화된 owned 창 ③ 제목에 `HeatingMold` + `오류`/`경고`/`시작 실패`. 정상 앱 주 창(제목 `HeatingMold`, owner 없음), owner가 활성인 owned 도구 창, IME/툴팁/메뉴/그림자 유틸리티 클래스는 제외한다. 파일/승인 대화상자도 noninteractive 검증에서는 unexpected로 실패하며 **버튼을 자동으로 누르지 않는다.**
- **본문 수집(전체 상한 하나):** UIA `ValuePattern`/`TextPattern`과 Win32 자식 컨트롤 텍스트 폴백을 **하나의 STA worker**에서 수행하고, 감시 스레드는 전체 수집 시간을 기본 1000ms(`-TextCaptureLimitMs`)만 기다린다. Win32 폴백은 자식마다 남은 시간만큼만 `SendMessageTimeout`을 쓰고 자식 수/텍스트 수에 상한이 있으며, `GetWindowTextLength` 같은 동기 메시지로 무제한 대기하지 않는다. 상한을 넘으면 수집을 중단하고 창 제목만 남긴다. 보고서 본문은 8000자에서 자르고 `bodyLength`에 원래 길이를 남긴다. WPF readonly TextBox는 실제로 UIA에서 전체 본문이 읽혔다.
- **실패 판정:** `unexpected-dialog`(모달), `timeout`(전체 대기 초과 후 트리 정리), `root-nonzero-exit`(결과 없이 비정상 종료), `early-exit`(결과 없이 정상 종료), `result-exists`(이전 결과 파일 재사용 거부, 시작 전), `job-create`/`job-assign`/`job-limit`/`job-query`/`job-detach`, `launch*`, `invalid-*`, `report-write-failed`. 결과 파일과 모달이 동시에 감지되면 모달 오류를 우선한다. 보고서 쓰기에 실패하면 이미 쓴 JSON을 `status=failed`/`reason=report-write-failed`로 보정하고 검증 트리를 정리하며, 성공으로 처리하지 않는다.
- **성공의 의미:** 가드 성공은 성공 보고서 게시 완료(또는 결과 없는 프로세스의 exit 0)만 뜻한다. 시나리오 자체의 성공 판정은 `result.json`의 `Status`를 읽는 호출자가 한다. 이 문구를 콘솔/문서에 유지한다.
- **산출물:** 실행 폴더의 `validation-guard.json` / `validation-guard.txt`(UTF-8, JSON은 BOM 없음)에 제목·클래스·PID·프로세스 경로·owner 제목/비활성·본문/출처/길이/오류·명령줄·시작 시각·`steps.jsonl` 마지막 줄·`trackedProcessIds`·`remainingProcessIds`·`cleanupStatus`·로그를 남긴다. 각 파일은 임시 파일 후 `File.Replace`(기존)/`File.Move`(신규)로 교체하며 **JSON/TXT 두 파일은 하나의 transaction이 아니다.**
- **`Run.ps1` 연결:** `tests\HeatingMold.VisualTest\Run.ps1`의 `Start-Process` + 300ms polling loop를 이 가드 호출로 교체했다. 기존 계약(시나리오 필수, 빌드/`-NoBuild`, `-AppExe` 패키지 exe, `-ExpectPackaged`, 성공 시 요약 출력과 앱 창 유지)은 그대로다. 가드 실패는 사유/대화상자/마지막 단계/보고서 경로를 출력하고 기존 "결과 없음"과 같은 종료 코드 **3**으로 끝난다. `HeatingMold.Start.exe`가 정상 종료해도 자식 앱은 같은 job에서 계속 감시한다.
- **PATH 실행 파일:** `-ExePath`에 경로 구분자가 없는 이름(`dotnet`, `cmd`)을 주면 PATH에서 `.exe`만 해석한다. PATH에서 찾은 항목이 `.exe`가 아니면 해석 단계에서 거부한다.
- **소급 적용 없음(중요):** 새 가드는 자기가 새로 시작한 프로세스 트리만 감시·종료한다. 이미 실행 중이던 앱/다른 검증 프로세스/이미 떠 있는 모달은 감지하지 않으며 종료하지도 않는다. 그런 창은 프로세스 경로·생성 시각·부모 실행 스크립트 같은 소유 근거를 확인한 뒤 별도로 처리해야 한다.
- **임시 오프스크린 하네스 지침:** `MainWindow`를 직접 만드는 하네스는 ① 이 가드로 실행하거나 ② `MainWindow` 생성 **전에** `ErrorReporter.SuppressDialogs = true`를 설정하고 제품 함수가 반환한 오류/경고를 직접 검사해 실패로 처리해야 한다. 현재 제품 구현에서 `SuppressDialogs`는 `MainWindow.StartVisualTestIfConfigured`(창이 뜬 뒤)에서야 켜지고 `OnVisualTestFinished`에서 이전 값으로 복원되므로, 그 전/후의 예외는 모달이 될 수 있다(2026-11-15 코드 확인).
- **검증 2026-11-15(자동, 제품/네이티브 빌드 없음):** `tests\ValidationGuard\Run-GuardTests.ps1` **112 PASS / 0 FAIL**. 부모가 승인된 Temp 하위의 새 실행 폴더에서 다시 실행해 EXIT 0과 로그 전체를 직접 확인했다. 포함: 표준 MessageBox `#32770`+Win32 본문, WPF reporter(owner disabled+UIA 본문+한국어 상세), owner 없는 WPF 오류 창, launcher exit 0 후 자식 모달+`trackedProcessIds`/`cleanupStatus=confirmed`, 성공 시 창 유지+`cleanupStatus=not-required`+**보고서 게시 로그가 detach 로그보다 먼저**, timeout 정리·무관 프로세스 보존·빈 job query 확인, owned 도구 창 오탐 없음, 결과 없는 exit 3/exit 0, 이전 결과 거부, 결과+모달 동시(모달 우선), 20,000자 본문 절단, 빈 본문 폴백, 1ms 수집 상한, **300개 Edit 자식 + 메시지 루프 없는 창에서 수집 상한(300ms) 적용 후 감시가 진행되어 10초 이내 실패·정리**(300ms 내 전체 종료를 뜻하지 않음), **`validation-guard.txt`를 디렉터리로 만들어 보고서 쓰기 실패 시 JSON failed 보정·트리 정리·detach 로그 없음**, **PATH 이름 `cmd` 해석**, **가드 host를 별도 프로세스로 띄워 그 프로세스만 강제 종료했을 때 추적 child 종료(kill-on-close)·무관 프로세스 유지**. `Run.ps1` 연결도 stub 앱으로 성공 요약/창 유지, 모달 시 nonzero+보고서 `unexpected-dialog`를 확인했다.
- **실제 App 7단계 스모크 검증(부모 확인, 2026-11-15):** 제품/네이티브를 빌드하지 않고 기존 App 출력(exe/App.dll/Core.dll/OccBridge.dll)을 승인된 Temp 하위 staged 폴더로 복사했으며 복사 전/후/staged SHA-256이 모두 같았다(제품 코드 무수정). 원본 `e2e-multi-area`의 첫 6개 actions와 기대값을 그대로 임시 복사한 시나리오(암묵 `loadModel` 포함 7 steps)를 `Run.ps1 -NoBuild -AppExe <staged exe> -ModelPath <repo>\resource\Mold_Cover_Trim.step -HoldSeconds 0.1 -TimeoutSeconds 180`으로 실행했다. OCCT/App.Tests 런타임 PATH와 `CSF_OCCTResourcePath`를 명시하고 sewn 환경 변수는 해제했다. 결과는 **EXIT 0, Status/Automation=Completed, 7/7 OK, VisualReview=NotRecorded**, 가드 보고서는 **succeeded / result-written / cleanupStatus=not-required / reportWriteFailed=false**였고 결과 게시 후 실제 앱 main 창이 살아 있었으며 로드된 3개 모듈이 모두 staged 경로였다. staged SHA-256: App `D0F8E301B4AC0C69BF2F86E1A5F1F92E23F2FB4DBDB75A7BC6C92C9E7F6AF5E8`, Core `7439E7210A904F7112474FBD2E0BA75CCC0CD020F749B36A2CE83AAA0049F656`, OccBridge `1EB93388F79D1EF2F3CE505F2D041B07812762A76CDDD396FBDF6BF488D9EE28`. 7단계 기준 결과는 A 60조각/29턴/1회로/18,440.993 mm이다. 테스트 후에는 정확한 경로·생성 시각·process handle 소유를 확인한 **그 스모크 프로세스만** Kill/Wait로 정리했고 사용자 앱은 건드리지 않았다.
- **미검증:** 실제 제품 오류 모달이 발생하는 경로, 전체 54단계 시나리오/5개 시나리오 전체 실행, **보고서 게시 도중 host 강제 종료**를 직접 재현한 테스트(순서/불변식 로그와 kill-host 테스트로만 확인), `job-detach`/`job-query` 실패를 강제로 만든 재현, 실제 서드파티 UIA provider가 완전히 멈추는 경우(합성 무응답 창으로만 확인), 실제 파일 열기/저장 대화상자, 실제 마우스/화면 육안 판정, 자식이 `CREATE_BREAKAWAY_FROM_JOB`로 job을 떠나는 경우. synthetic 112개 체크와 실제 App 7단계 스모크는 구분해서 해석한다.

## 고정 방향 투영 계획 0~1단계 gate (이번 라운드 수행 범위·2026-11-15)

- **실제 수행 범위:** 이번 라운드는 `docs/FixedDirectionProjection.Plan.md`의 **0단계와 1단계 gate만** 수행했다. 2단계(실패 조각 국소 연속 투영 시제품)와 3단계 이후(준비 packet·캐시·저장·출력·편집·분할)는 **미착수**다. 계획 문서는 수정하지 않았고, **당시 결과 기록은 `docs/FixedDirectionProjection.Gate.md`였으며 그 문서는 2026-11-15 문서 정리에서 삭제**되었다(핵심 결과·한계는 `docs/CurrentStatus.md` §5로 통합). 계획 문서 자체는 미구현 상태로 보존하며, 삭제된 gate 문서를 살아 있는 자료로 안내하지 않는다.
- **사용자 승인 범위와 역할 구분:** 원사용자 승인은 계획 전체의 **조건부 실행**이며, 선행 gate 조건을 충족하는 경우 **7단계의 실제 영역 전체 검증까지** 포함한다. 8단계 후속 제품화는 별도 승인이다. 부모가 이번 라운드에서 Flash에게 위임한 범위(0~1단계)는 사용자 전체 승인 범위와 별개다. **0~1단계만 승인된 것이 아니다.**
- **gate 불충족의 의미:** gate 불충족 시 2단계 이후 **구현** 중단은 유지하지만, 이는 미해결 문제 조사 전체의 종료나 해결 완료를 뜻하지 않는다. 2026-11-15 사용자 정정에 따라 **기존 원본/공차 불변 조건 안에서 원인 추적을 계속**하며, 근거 없는 gate 우회/자동 메우기는 금지한다.
- **gate 판정:** 이번 진단에서 유효한 원본 면 교차(trim IN/ON)를 확보하지 못했다 → 계획의 선행 1단계 gate 조건 불충족 → 2단계 미착수. "실제 제조상 틈", "CAD 모델 invalid", "조회 solver 결함 아님" 같은 배타적 단정은 하지 않는다.
- **제품 기본 표면 조회는 원본 `g_shape`/`g_faces`다.** 계산용 1 mm 재봉 사본은 **시험 전용 opt-in**이며, 환경변수 `HEATINGMOLD_USE_CALC_SEWN_COPY` 값이 **정확히 ASCII `1` 한 글자**일 때만 켜진다(미설정/빈 값/`0`/`true`/`false`/`off`/`2`/`11`/공백 포함/버퍼보다 긴 값은 모두 false). 이 값은 **모델을 (재)로드할 때만** 읽으므로, 이미 활성화된 작업 컨텍스트의 조회 대상을 바꾸지 않는다.
- **진단은 로드 시점 스냅샷이다.** `Bridge.GetCalcSewnSurfaceQueryDiagnostic()`은 `requested=<0|1> active=<sewn|original> faces=<n>/<m> modifiedFaces=… modifiedEdgeOccurrences=… reason=…`를 로드 시점 값으로 보고한다(환경변수만 바꿔도 `requested`/`active`가 어긋나지 않는다). 재봉 실패로 원본에 폴백하면 `active=original`이고 면 수는 실제 원본 수(80/80)를 보고한다. 이 문자열은 표시·시험 assert용이며 **제품 분기 근거가 아니다**(판정은 `IsCalcSewnSurfaceQueryActive()` 같은 상태값과 typed 결과로 한다).
- **문서 1단계 대상 실패 재현(측정):** 기본 예제(`resource\Mold_Cover_Trim.step`, 원본 sha256 `8CB03405…FCED`, 실행 후 불변) + 저장 프레임으로 0.2 mm 생성 시 2,500레인 / 5,000조각이고, zero-based 536(1-based 537) `HeatingRun` lane 268 조각의 적응 표본 uv `(-489.81391714892175, -32.960668248954725)`에서 선택 면 조회와 전역 조회가 모두 Miss, 정확 경계 폴백은 NoIntersection이다. `gapSpacing` 0과 1 mm 모두 같은 표본에서 실패하며 0.1 mm 등분 1,105표본에서는 이상이 없다. 실패 시 영역 B는 472조각/`80189.26162223217` mm로 보존되고 A는 게시되지 않는다(부분 성공 없음).
- **원본 모델 직접 조사(측정):** 제품과 같은 `IntCurvesFace_ShapeIntersector`로 선택 59면·전체 80면 모두 hit 0. `GeomAPI_IntCS`(원본 untrimmed surface, 원본 location 적용) 그룹별 집계는 **선택 59면과 전체 80면을 분리**해 계산했다: 선택 59면 faces 59 / nullSurface 0 / done 59 / notDone 0 / 광선 내 점 24 / 광선 밖 6 / IN 0 / ON 0 / OUT 24 / UNKNOWN 0 / overlapped 0, 전체 80면 faces 80 / nullSurface 0 / done 80 / notDone 0 / 광선 내 점 33 / 광선 밖 7 / IN 0 / ON 0 / OUT 33 / UNKNOWN 0 / overlapped 0. face 57과 face 58은 각각 `IsDone=1`, `NbPoints=0`, `NbSegments=0`이다. 선택 그룹 통계를 "원본 모델 모든 면"으로 일반화하지 않는다.
- **해석 한계(중요):** 실패 광선은 위상적으로 **같은 edge를 공유**하는 두 BSpline patch(face 57/58) 사이로 지나간다. 그 edge의 기록 tolerance는 `0.0002640079095307815` mm, vertex tolerance는 `0.0098711376547345152` / `0.0024373442180428313` mm이고, 두 면 pcurve를 같은 매개변수에서 3D로 평가한 **2,000 등분 표본** 차이는 max `0.00026400519175864238`, mean `0.00012996879147971838` mm다(표본 측정값이며 certified maximum이 아니다). OCCT 7.9.3 `BRepCheck_Analyzer.hxx` 143~146행의 유효성 계약은 edge 3D 곡선과 각 면 pcurve 편차가 edge tolerance 이하이면 되므로, **이 표본 차이만으로 CAD 모델 invalid나 물리적 구멍을 확정할 수 없다.** `GeomAPI_IntCS::IsDone`은 계산 완료만 뜻하고 광선 구간 전체의 비피복을 증명하지 않는다. 공유 edge tolerance를 근거로 tolerance 이내를 연결하는 정책은 **채택하지 않았다**(계획 §2·§7의 공차 기반 메우기 금지 유지).
- **재봉 사본 모드의 `GeometryFailure`:** 재봉은 9개 면(17, 33, 34, 45, 46, 57, 61, 62, 70)을 새 TShape로 교체하고(`modifiedFaces=9`, `modifiedEdgeOccurrences=0`), 그 compound에서는 위 실패 대역 6개 지점에서 `IntCurvesFace_ShapeIntersector::Perform`이 메시지 없는 `Standard_Failure`를 던진다. 제품의 `catch (Standard_Failure&)`가 이를 `GeometryFailure`로 변환한다. 따라서 계획 문서에 기록된 `GeometryFailure` 6개는 원본 모델 상태가 아니라 재봉 사본에서 생긴 커널 예외이며, **원본 모드에서는 같은 6점이 정상 Hit(face 57)**이다. 실패 uv 자체는 두 모드 모두 Miss로 동일하다.
- **2 mm 대조(측정):** 같은 인출점에서 외곽 간격만 2.0 mm로 실행하면 2D 1,088조각/544레인, 3D 확정 1,088조각 / `227149.59715059225` mm / 1회로 / 543턴, 평면 턴 예외 1건(인증 상계 `0.17371753663556924` mm)으로 성공하고 영역 B(472조각)는 보존된다. 0.2 mm는 위 실패로 A가 게시되지 않고 B만 보존된다 — **0.2 mm 전체가 해결되었다는 기록이 아니다.**
- **추가한 회귀:** `tests\WorkingSide.Regression\SurfaceQueryModeChecks.cs`(70검사). opt-in 값 11종의 true/false, 원본 모드 실패 uv의 선택/전역 Miss, u±0.001의 Hit 58/57, 밴드 6점의 원본 Hit와 재봉 모드 GeometryFailure, 대조 v=-32.952의 Hit 61, 두 모드의 같은 datum 3D점·지지면 동일, `IsWorkingPointVisible` 동일(같은 intersector 공유), **환경변수만 바꾼 상태에서 active·진단 스냅샷·조회 결과가 불변**이고 재로드에서만 전환됨을 확인한다.
- **검증(2026-11-15, 수정 후·새 DLL):** Core 테스트 269 통과, App 테스트 207 통과, `tests\WorkingSide.Regression` EXIT=0·그룹 실패 0 — sidebar 31, area highlight 30, direct exit 19, vertex edit 32, split boundary 28, remaining fixture 340, electrical 3, pattern state 48, geometry rollback 63, support-face 7, surface-query 70, project load 20, project load exclusion 51, working-side 189. 네이티브는 `/p:OutDir=<repo>\x64\Release\` 절대 경로로 빌드하고, 관리 빌드 뒤 새 `OccBridge.dll`을 테스트·회귀 실행 폴더로 다시 복사한 다음 실행했다. 네이티브 바이너리는 같은 소스 `/t:Rebuild`에서도 PE 타임스탬프 때문에 해시가 달라지므로, DLL 해시는 **해당 실행이 실제로 로드한 산출물 식별자**로만 쓴다.
- **부모 독립 재실행(2026-11-15, 제품·네이티브 빌드 없음):** 부모가 최종 `tests\WorkingSide.Regression\bin\Release\net8.0-windows\WorkingSide.Regression.exe`를 인자 `x64\WorkingSideRegression`으로, 새 ValidationGuard를 통해 직접 실행했다(OCCT·App.Tests 런타임 PATH와 `CSF_OCCTResourcePath` 명시, 재봉 opt-in 환경변수 미설정). 가드 상태는 `succeeded` / `reason=completed` / `rootExitCode=0` / `rootExited=true` / `reportWriteFailed=false`이고 대화상자는 없었다. 전체 EXIT 0, 그룹 실패 0, FAIL 행 0, 신규 surface-query 70건 모두 PASS.
  - 실행 파일·App·Core·native 등 5개 파일의 SHA-256이 실행 전/후 동일했고, 로드된 bridge는 저장소 `x64\Release\OccBridge.dll`과 같은 해시다. `COREHOST_TRACE`로 App/Core/native가 회귀 exe 옆 경로에서 해석됨을 확인했다(종료 후 모듈 열거 방식이 아니다). 해시: App `7AB6BE234EFE75505526A97E691861B23EF357FCE5152552B5959BD06F4CBA3E`(615,936 B), Core `0604D3E9606B7011AB861C4582948B3624E0A1233A1ADE089F7C8117CDB90910`(232,448 B), OccBridge `680E85996E4F42C878D73FC422AC362BF324051334F4E77BF3689C37466B5326`(4,618,752 B). 이 값들은 **실행 산출물 식별자**이며 소스 지문이 아니다.
  - **계수 차이(미해결·범위 밖):** 회귀 로그의 PASS 행은 911개인데 각 그룹이 선언한 검사 수의 합은 931이다. 931 PASS로 단정하지 않으며 두 수를 같은 총계로 취급하지 않는다. 원인은 확인하지 않았고 테스트 카운터도 수정하지 않았다. surface-query 70건은 별도로 직접 확인했다.
- **검증 구분:** Core 269 / App 207은 이 작업 세션이 실행하고 부모가 로그로 확인한 값이며, 위 부모 재실행은 WorkingSide 회귀에만 해당한다.
- **미실행·미검증:** 2단계 이후 구현 전체, 실제 마우스 조작·화면 육안 판정, 재봉 격리에 따른 성능 영향, 이 모델 이외의 새 모델에서의 동일 현상 유무, 재봉 모드 `Perform` 예외의 OCCT 내부 원인. 위 항목은 확인 전까지 해결된 것으로 기록하지 않는다.

## 발열경로 제한 근사 연결 구현 (사용자 승인·구현 2026-11-15)

> 이 항목은 이전 "계획 요청(미구현)" 기록을 대체한다. 계획 보고 후 사용자가 **구현을 승인**했고,
> 아래 내용은 **실제 구현·자동 검증** 결과다. 상세 계약은 `docs/CurrentStatus.md`(§2·§3·§6·§8)에
> 단일 현황으로 정리되어 있다(이전 `docs/BoundedSampleSkip.md`는 2026-11-15 문서 정리에서 삭제).

- **사용자 확정 정책(최신):** 정밀 표면 일치가 아니라 **대략적인 경로면 된다**. 표면을 찾지 못한
  짧은 내부 표본을 생략하고 앞뒤 실제 정상점으로 직선 연결한다. 한도는 **앞뒤 정상점 사이 원래
  2D 경로 진행 길이와 3D 직선거리 모두 ≤ 10.0 mm**, **누락 개수 제한 없음**, 누락 구간은
  **각각 독립 판정**한다. 인출점·원본 조각 끝점·원래 꺾임점은 절대 생략하지 않는다.
  큰 구간·깊이 점프·커널 오류·오래된 컨텍스트·비유한 값은 거부한다.
  이전의 "전체 표면 피복/공유 edge 증명/전면 투영 교체"는 이번 구현의 선행 조건이 **아니다**.
- **구현(Core):** `RouteApproximationPolicy`(`route-approx-v1`, `MaxBridgeAdvanceMm=10`,
  `MaxBridge3DGapMm=10`, `ProbeSpacingMm=1`)과 `SurfacePathApproximation`
  (`TryMapRoute`: strict fast path + 근사 / `TryMapRouteAfterStrictFailure`: strict를 이미 실행한
  호출자용)를 추가했다. typed `SurfaceQueryResult`(`Miss`/`Hit`/`Failure`)로만 분기하며
  문자열 부분 검사·공유 lastError를 쓰지 않는다. strict API
  (`SurfacePathMapper.TryMapRouteStrict`)의 실패 동작은 변경하지 않았다.
- **알고리즘:** strict와 같은 정제 격자(중점→사분점)와 같은 1 mm 보강 표본 격자를 실제로 다시
  조회한다(우연히 Miss를 못 본 것으로 통과시키지 않기 위함). Miss 구간은 경계를 1 mm 간격으로
  실제 조회해 마지막/첫 Hit를 찾고, 그 두 Hit로 10 mm 두 한도를 검사한 뒤 중간 3D 점을 만들지
  않는다. 승인 표본은 모두 실제 Hit(또는 정확 endpoint override)이며 점별 supportFace는 네이티브
  스테이징 검사를 그대로 통과한다. 일반 성공 구간의 chord 0.05 mm 검사는 유지된다.
- **적용 위치·순서:** 생성은 `MainWindow.Areas.cs`의 `TryPreparePieceSamples`가
  **strict → 평면 턴 외곽 예외 → 제한 근사** 순서로 판정한다. 기존에 성립하던 2점 턴이 근사
  때문에 여러 점/새 길이로 바뀌지 않게 하기 위해 근사를 턴 예외 뒤에 둔다. 수동 편집
  (`MainWindow.VertexEdit.cs`의 `TryBuildVertexCandidate`)도 같은 helper를 쓴다. 2D 드래그
  미리보기는 변경하지 않았다.
- **캐시:** 근사·턴 예외 표본은 strict 표본 사전에 넣지 않는다(`RouteSampleCachePolicy.CanCache`가
  `Strict`만 허용). 검증 키 접두부를 `strict-v4`로 올리고 `RouteApproximationPolicy` 값
  (버전/10 mm/조회 간격)을 포함했으며 retain 키도 같은 접두부를 쓰므로 이전 보증 표본과 섞이지
  않는다. 로드/저장 경로는 그대로다(schema 7, 저장된 실제 xyz+점별 supportFace 복원, 재투영 없음).
- **독립 리뷰 수정 6건(구현 완료, 같은 날):**
  - HIGH 성공 우선순위 보존: 후보 판정을 `SurfaceProbePolicy`(App, 순수 함수)로 분리했다.
    선택 면의 하드 상태가 전역 표면/정확 경계의 Hit를 막지 않고, **유효한 Hit가 나오면 그 점으로
    즉시 성공**한다(과거 nullable 규칙과 같은 순서·좌표). Hit가 전혀 없을 때만 하드 상태를
    Failure로 판정하고, 순수 Miss(선택 면/전역 표면 Miss + 경계 `NoIntersection`)만 근사 허용이다.
    결과 객체 null·Hit인데 3D 점 없음·Tangent인데 점 없음 같은 불명확한 비-Miss는 단순 Miss로
    취급하지 않는다.
  - HIGH strict 하드 오류 재시도 금지: `SurfaceProbeRecorder`(Core)가 strict 시도 중 관측한
    typed Miss/Failure를 값으로 기록하고, `AllowsApproximationRetry = !SawFailure && SawMiss`일
    때만 평면 턴 예외·근사를 시도한다. 커널/stale/비유한/입력·override/정제/예산·깊이 한도 실패는
    재조회로 지워지지 않는다(일시적 Failure→Hit delegate도 재시도되지 않는다).
    `TryMapRouteAfterStrictFailure`는 recorder를 인자로 받아 이 전제를 구조적으로 강제한다.
  - HIGH 같은 2D 좌표 반복 시 필수점 생략 금지: `TryValidateDuplicateRequiredPoints`(Core)가
    연속 동일 좌표에서 뒤쪽 필수점(인출점 override)의 유한성·지지 Face·앞 점과의 3D/지지 면
    동일성을 검사한다. 두 점이 같으면 정상 중복(strict와 같은 결과)으로 진행하고, 다르거나
    유효하지 않으면 명시적으로 거부한다. strict fast path 앞에서도 적용하며 Walker 안에서도 재검사한다.
  - MEDIUM 표본 격자 정수 오버플로: `ceil(length/gapSpacing)`을 int로 캐스팅하기 전에 유한성·
    int 범위·남은 표본 예산을 검사한다(근사 Walker와 strict `ProbeLeafGaps` 모두). 넘치는
    격자는 표본 검사를 생략한 성공이 아니라 명시적 실패다(정상 조회 순서·좌표는 불변).
  - fast path 동일성 테스트를 `BitConverter.DoubleToInt64Bits` 비교로 강화했다(±0 비트 차이 검출,
    override 표본 포함).
  - MEDIUM 적응 표본이 모두 Miss인 구간의 오탐 거부: 초기 적응 표본(25/50/75%)이 모두 Miss이면
    `HandleMisses`가 그 사이를 조회하지 않고 하나의 연속 누락으로 합쳐, 실제로는 2 mm 안팎인
    서로 떨어진 누락 3곳(예: 25/50/75 위치)을 약 52 mm 연결로 오판해 정상 경로를 거부했다.
    이제 그 경우 정책 1 mm 간격으로 구간 전체를 **실제 조회**해 Hit/Miss 구조를 파악하고,
    이미 관측한 적응 표본과 진행 파라미터 순서로 합쳐(`MergeProbes`, 같은 위치는 Miss 우선)
    **누락 구간마다 독립 판정**한다. 누락 개수 상한은 추가하지 않았고 표본 예산·10 mm 두 한도·
    필수점 규칙은 그대로다. 하나의 넓은 실제 누락은 계속 거부되고, 조회 중 Failure는 즉시
    전체 거부다. 가로/세로·정/역방향 4조합 회귀와 넓은 누락 거부 회귀를 추가했다.
  - MEDIUM malformed Hit 후보 판정 일관성: 공통 factory `SurfaceProbeOutcome.FromHit`가
    데이텀/3D 유한성 + `FaceId >= 0`(+ 요청점 overload는 데이텀 잔차 ≤ 0.1 mm)을 검사해
    malformed native Hit를 **Hard**로 분류한다. 그래서 선택 면이 malformed면 전역 표면,
    전역 표면이 malformed면 정확 경계의 **유효 Hit**로 계속 진행하고(기존 성공 우선순위·좌표는
    bitwise 그대로), 유효 후보가 하나도 없을 때만 Failure가 된다(단순 Miss/근사 허용으로
    낮추지 않는다). 이전에는 비유한 좌표·`FaceId<0`만 즉시 Hit로 반환되어 뒤의 유효 후보를
    막았고, `Point 없음`(Hard) 처리와 계약이 달랐다. 선택/전역/경계 세 단계가 같은 factory를
    쓰므로 검증 권위가 하나다. Core의 "final Failure는 재조회하지 않는다" 계약은 변경하지 않았다.
- **통계:** `RouteApproximationPublication`이 트랜잭션 시작 시 `Begin`, 네이티브 커밋 성공 직후
  `PublishOnCommit`, 준비 실패 시 `AbandonOnFailure`를 호출한다. 완료 상태에는
  `이번 준비 근사 연결 N곳 · 총 X mm · 최대 Y mm`만 표시하고, 근사가 없으면 아무것도 붙이지
  않는다. **이 수는 이번 준비에서 실제로 실행한 근사만 센다** — retain/캐시 적중 조각은 다시
  매핑하지 않으므로 포함되지 않으며, schema 7에 근사 메타데이터를 저장하지 않으므로 로드 직후의
  0을 "영역 전체 근사 없음"으로 해석하지 않는다. 새 설정 UI는 추가하지 않았다(10 mm 고정 상수).
- **길이/전기/라벨/DXF/STEP:** 모두 확정 native chain과 실측 길이를 그대로 쓰므로 변경 없음.
- **검증 A (2026-11-15, 4건 수정 시점의 공유 출력 — 이미 배포/검증 담당이 사용 중):** Core **311**,
  App **222**, `tests\WorkingSide.Regression` **EXIT 0, 911 PASS, FAIL 0**. 로드 DLL:
  App `3916E531…E9CA`(623,616 B), Core `D09A7E1A…1649`(255,488 B), OccBridge `680E8599…5326`.
  이 출력은 `tests\HeatingMold.App.Tests\bin\x64\Release\net8.0-windows\`, `x64\WorkingSideBuild\`,
  `tests\WorkingSide.Regression\bin\Release\net8.0-windows\`에 있으며 5차 수정에서 **덮어쓰지 않았다**.
- **검증 B (2026-11-15, 5차 수정 후, 완전 격리 OutDir — 최신 소스 상태):** Core **316 통과**
  (신규 적응-표본-전부-Miss 분할 4조합 + 넓은 누락 거부 1), App **222 통과**,
  `WorkingSide.Regression` **EXIT 0, 911 PASS, FAIL 0, 그룹 실패 0**. 공유 출력은 건드리지 않고
  `<Temp>\opencode\hm-approx\iso\{core,app,ws}`에만 빌드·실행했다. 로드 DLL:
  App `214193C6…ADFEC`(623,616 B), Core `5B6C6BF8…F8F0`(256,000 B, App 빌드 경로),
  Core `97A80F94…33BD`(256,512 B, Core.Tests AnyCPU 빌드 경로), OccBridge `680E8599…5326`.
  `tests\HeatingMold.Core.Tests\bin\Release\net8.0\HeatingMold.Core.dll`은 5차 수정 **이전**
  중간 빌드(`79D70784…6164`)이므로 그 기본 출력으로 Core suite를 돌리면 최신 수정이 아니다
  (격리 OutDir로 실행해야 한다).
- **검증 B의 포함 항목:** Core `SurfacePathApproximationTests` 38 — fast path `DoubleToInt64Bits`
  동일성(override 포함), 고립/연속 Miss, 다중 구간 독립 판정, 적응 표본 전부 Miss의 4조합 분할,
  넓은 누락 거부, 개수 무제한, 2D 초과 거부, 3D 점프 거부, 정확 10 mm 허용/초과 거부,
  꼭지점·인출점 override 보존, kernel Failure/비유한/지원 Face 없음/데이텀 잔차/표본 한도/
  override 범위 거부, 일시적 Failure→Hit·비유한 표본→Hit·깊이/예산 실패 재시도 금지,
  동일 좌표 필수점 identity(다른 깊이·무효 support 거부, 같은 깊이 보존), 거대 격자 사전
  거부(strict/근사), 조회 순서≠경로 순서, 같은 XY 재방문, strict API 거부 유지.
  App `RouteApproximationPublicationTests` 7 + `SurfaceProbePolicyTests` 8(선택 면 하드+전역 Hit
  성공 보존, Miss+하드+경계 Hit 성공, 세 단계 우선순위, 하드만 있으면 Failure, 불명확 비-Miss는
  Miss 아님, 경계 모호는 하드, 순수 Miss만 근사 허용, 커밋 전 미게시 등).
  `WorkingSide.Regression` 그룹: sidebar 31, area highlight 30, direct exit 19, vertex edit 32,
  split boundary 28, remaining fixture 340, electrical 3, pattern state 48, geometry rollback 63,
  support-face 7, surface-query 70, project load 20, project-load exclusion 51, working-side 189.
- **검증 C (2026-11-15, 부모가 격리 출력을 빌드 없이 직접 재실행):** 위 B와 같은 격리 산출물을
  `tools\validation\Invoke-GuardedProcess.ps1`로 가드 실행해 확인했다. 결과: Core
  `iso\core\HeatingMold.Core.Tests.dll` **TRX 316/316 통과**, App
  `iso\app\HeatingMold.App.Tests.dll` **TRX 222/222 통과**, `iso\ws\WorkingSide.Regression.exe`를
  인자 `<repo>\x64\WorkingSideRegression`로 실행해 **EXIT 0, PASS 행 911, FAIL 행 0,
  `CHECK-GROUP FAILED` 0**. 가드 보고 3건 모두 `status=succeeded`, `reason=completed`,
  `rootExitCode=0`, `rootExited=true`, `reportWriteFailed=false`, `dialog=null`,
  `cleanupStatus=not-required`. 실행 전/후 SHA-256 동일(Core 2개·App 4개·WS 5개):
  `iso\ws\` App `214193C6…ADFEC`(623,616 B), Core `5B6C6BF8…F8F0`(256,000 B), native
  `680E8599…5326`(4,618,752 B), `iso\core\` Core `97A80F94…33BD`(256,512 B).
  `COREHOST_TRACE`로 App/Core/OccBridge 세 어셈블리가 모두 `iso\ws\` 옆 DLL로 해석됨을 확인해,
  구 DLL이 아니라 새 코드가 실제로 로드됐음이 증명됐다. 증거 파일 이름:
  `core-final`, `app-final`, `ws-final` 폴더의 각 `core.log`/`app.log`/`regression.log`,
  `results\*.trx`, `validation-guard.json`, `before-sha256.json`/`after-sha256.json`,
  `ws-final\host-trace.log`.
  **선언된 그룹 검사 수 합 931과 PASS 행 911의 차이는 이 변경 이전부터 있던 미해결 사항이며,
  "931 PASS"로 표기하지 않는다.**
- **검증 D (2026-11-15, 리뷰 6차 수정 후, 새 격리 OutDir `iso6`):** Core **로직 미변경**이라
  Core 테스트는 재실행하지 않았다(Core DLL 해시 `5B6C6BF8…F8F0`이 B와 동일함으로 확인).
  이후 `RouteApproximationPolicy.ProbeSpacingMm`의 XML 주석 1곳만 정정했고 **재빌드하지 않았다**
  (상수·IL·동작 동일, 기존 Core 바이너리는 그대로). App 테스트
  **241 통과/0 실패**(222 + 이번 신규 factory/fallback 19), `WorkingSide.Regression`
  **EXIT 0, 911 PASS, FAIL 0, `CHECK-GROUP FAILED` 0**(그룹 구성은 B와 동일).
  로드 DLL(절대 경로는 보고 참조): `iso6\app|ws\HeatingMold.App.dll` `427E502D…6008`(624,128 B),
  Core `5B6C6BF8…F8F0`(256,000 B), native `680E8599…5326`(4,618,752 B).
  공유 출력(App bin·test bin·`x64\WorkingSideBuild`)과 이전 격리 `iso\{app,core,ws}`는
  SHA-256이 그대로임을 확인했다(App `3916E531…`, Core `D09A7E1A…` / `214193C6…`, `97A80F94…`).
  실사용 CAD 0.2 mm 종단 성공 여부는 여전히 **미측정**이다.
- **검증 E (2026-11-15, 부모가 `iso6` 최종 출력을 빌드 없이 가드 재실행):** 위 D와 같은 격리
  산출물을 `tools\validation\Invoke-GuardedProcess.ps1`로 다시 실행해 확인했다. App
  `iso6\app\HeatingMold.App.Tests.dll` **TRX 241/241 통과**(failed·error·timeout·notExecuted 0),
  `iso6\ws\WorkingSide.Regression.exe` **EXIT 0, PASS 행 911, FAIL 행 0, `CHECK-GROUP FAILED` 0**.
  가드 보고 2건 모두 `status=succeeded`, `reason=completed`, `rootExitCode=0`, `rootExited=true`,
  `reportWriteFailed=false`, `cleanupStatus=not-required`, `dialog=null`, `remainingProcessIds` 빈 배열.
  실행 전/후 SHA-256 동일(App.Tests·App·Core·native 4개, WS exe·harness dll·App·Core·native 5개).
  `COREHOST_TRACE`로 App/Core/OccBridge 세 어셈블리가 모두 `iso6\ws` exe 옆 경로에서 해석됨을 확인.
  로드 DLL: App `427E502D…6008`(624,128 B), Core `5B6C6BF8…F8F0`(256,000 B),
  native `680E8599…5326`(4,618,752 B). Core 로직 미변경이므로 Core 316 TRX는 기존 기록을 유지하고
  이번 재실행은 App·WS만 해당한다. 증거 폴더와 상대 파일: `app-six-20261115-024139`의 `app.log`,
  `results\app.trx`, `validation-guard.json`, `before-sha256.json`/`after-sha256.json`;
  `ws-six-20261115-024139`의 `regression.log`, `validation-guard.json`,
  `before-sha256.json`/`after-sha256.json`, `host-trace.log`. WS 종료 시 `wglMakeCurrent` 경고는
  기존 정리 메시지이며 검사 실패가 아니다.
  부모 확인: `git diff --check` EXIT 0, 사용자 Plan `92EB7308…63AB2`, STEP `8CB03405…FCED`,
  JSON `A7B814F7…C70C9`, native `680E8599…5326` 불변.
  **최종 App 공유 bin 배치(`3916E531…`)는 아직 하지 않았으며 격리 최종본(`427E502D…`)과 구분한다.**
  선언 그룹 합 931과 PASS 행 911의 차이는 기존 범위 밖 미해결 사항이며 "931 PASS"로 쓰지 않는다.
- **성능 주의(정적 확인, 배율 미측정):** 근사 연결은 **Miss 구간 허용 정책**이며 표본 밀도나
  전체 성능을 최적화한 것이 아니다. strict fast path의 원래 1 mm 보강 조회와 조각별 native
  점별 지지면 검증은 근사 도입 후에도 그대로 실행된다. 생성 시간에는 커밋 후 길이 라벨 재배치가
  `Generate_Click`의 동기 호출에 포함되어 있고, 라벨 엔진은 라벨마다 전체 route 구간에서 자기
  조각을 제외한 목록을 새로 만들어 배치된 라벨·전체 선분과의 겹침을 검색한다(정적 확인).
  실제 병목 귀속과 개선 배율은 **측정하지 않았다**. 조각 수·작업이 다른 두 실행(예: 저장
  472조각 복원/보존 vs 0.2 mm 5,000조각 신규 생성)을 생성 속도 비교로 해석하지 않는다.
- **함정 재확인:** `tests\WorkingSide.Regression`은 `x64\WorkingSideBuild\`의 DLL을 참조한다.
  App/Core를 새로 빌드한 뒤 그 폴더로 복사하지 않으면 **구 DLL로 실행되어 변경이 검증되지 않는다**
  (이번에 구 DLL로 한 번 통과한 뒤 새 DLL에서 `(SurfaceMappedSample[])` 캐스트 실패가 드러났다).
- **미검증:** 실제 마우스 조작·화면 육안 판정, 새 프로세스에서의 프로젝트 복원, 외부 CAD에서
  DXF/STEP 열기, 사용자 실제 CAD(0.2 mm·대형 모델) 종단 성능과 조각 수, 근사 연결 내부의 실제
  표면 오차, 1 mm보다 좁은 결함 검출. 근사 연결은 표면 피복을 증명하지 않는다.
