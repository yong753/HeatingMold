# Power lead regression checks

Run from the repository root in PowerShell:

```powershell
& tests/PowerLead.Regression/Run.ps1
```

Requires the installed VS 2022 Community x64 C++ tools, .NET 8, and the repository's OCCT distribution. The runner refreshes the native bridge in `x64/Release` (the app's fixed reference path), then builds the solution using the installed VS MSBuild in Release/x64, with an isolated app output directory (`x64/PowerLeadBuild`) so it does not overwrite a running application's DLLs. Generated STEP fixtures and the native fixture generator go under `x64/PowerLeadRegression`. No production STEP file is modified.

The console checks exact native projection on sloped and cylindrical sheets, adjacent sewn faces, gaps/holes, source orientation, insertion translation, actual curve lengths, invalid input and destination rejection. Hidden-window WPF checks exercise native-length circuit tables, disabled-turn splitting, deliberate safe gating before and after working-side confirmation, and cancellation/topology/regeneration cleanup of explicitly seeded restored pending state. No test enables or bypasses the UI generation gate. AIS display/overlay removal and screen picking still need an interactive viewer check; headless geometry tests do not create a GL context.

Preview checks compare exact preview/commit lengths, reject invalid preview destinations, exercise idempotent preview cleanup and verify hover does not consume endpoints or update lengths. Deterministic timer-tick checks exercise 100 ms latest-cursor coalescing, duplicate-pixel suppression and cancellation on leave, wheel navigation, capture loss, mode exit, reset and commit (including stale ticks). Wheel checks cover inverted positive/negative, multi-notch and fractional-notch input, preserving baseline quantization including zero delta.

Current checkpoint interactive checks: power lead setup must remain blocked, including after working-side confirmation. Pattern generation at depth zero remains enabled. Confirm wheel direction is reversed while toolbar zoom buttons retain their previous directions. Native legacy preview/commit geometry is still tested directly, but is not an authorized UI route.

Future route-migration checks (not available/enabled now): automatic actual-3D circuit endpoint queuing, mutually exclusive power input modes, outside-destination hover/commit, restart, and committed-vs-transient overlay lifecycle must be revalidated against the new whole-model first-hit/boundary contract. The old selected-face destination/external-continuation rules below must not be silently reused for the new boundary route. The seeded restored-state tests cover cleanup, not future automatic queue creation.

## Retained legacy native behavior and limits (UI gated)

- Setup automatically uses the retained, source-oriented projected 3D start/end of each enabled circuit. Users click only each outside destination, in circuit/start/end order shown in the status bar.
- A straight line in the fixed model projection plane defines the lead's plan-view direction. OCCT projects and clips it to the selected trimmed heating faces. Its exact surface edges are retained, followed by one straight external segment from the heating-face boundary to the clicked point on the insertion datum plane.
- Existing insertion depth semantics are preserved: project onto heating faces first, then translate the surface portion along fixed model -Z. This is not a surface-normal offset.
- Destinations whose projection-axis line intersects any mold face are rejected. Gaps, re-entry, multiple projected wires/layers, missing endpoint matches, invalid geometry, or failure to join are rejected without a floating-line fallback. It does not search for detours or choose among ambiguous surface layers.
- The entire external segment is additionally projected against every mold face independently, including unselected faces, and checked for direct material intersections. Crossing adjacent material or re-entering across a concavity is rejected even when the destination is outside. Independent face checks prevent a tolerant imported neighbor from merging away a narrow obstruction. Verified selected-face boundary endpoints may have zero surface length when directed immediately outside; inward directions still require the normal surface projection. Boundary matching uses 3D trim-edge distance with the maximum OCCT face/edge/incident-vertex tolerance (at least `Precision::Confusion()`), capped at the existing 0.01 mm projection/sewing budget; datum alignment uses that same matched tolerance. External crossing/contact checks remain at 1e-7 mm, not the larger boundary tolerance. Noisy STEP fixtures exercise this distinction and nearby obstruction rejection.
- Heating and lead totals use OCCT 3D curve lengths. Leads have their own ID-keyed native overlays; setup restart, cancellation, topology changes, regeneration and file changes clear them without deleting heating-route overlays.
- Sewing, endpoint and continuity acceptance tolerance is 0.01 mm. Curve sampling at 0.01 mm is used only for monotonicity validation, never to replace projected surface curves or measure their length.
