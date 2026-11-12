# Working-side checkpoint regression

Run from the repository root in PowerShell:

```powershell
& tests/WorkingSide.Regression/Run.ps1
dotnet test tests/HeatingMold.Core.Tests/HeatingMold.Core.Tests.csproj -c Release --no-restore
```

The script builds the native bridge into `x64/Release` first, because the app explicitly
references that DLL. It then builds the solution into `x64/WorkingSideBuild`, avoiding
DLL locks from a running viewer. It does not terminate any process.

Fixtures cover layered occlusion, a through-hole formed by adjoining sheets, and a sphere
whose projected silhouette is not its trim-wire boundary. The harness initializes an
offscreen native viewer and exercises WPF handlers without showing the application window.
It covers visibility at the candidate point, revision ownership, immediate axis application
(including default +Z), same-axis no-op, and precommit rollback. It does not test pan/zoom
behavior or the route-generation gate.

Vertex editing is exercised through the real `CommitVertex` commit path, including
spacing-locked drags, candidate identity preservation, and rollback when native rebuild fails.
Undo/Redo history is not provided; the harness checks that the removed history members stay absent.
`GeometryRollbackChecks` covers exclusion-path stage cleanup and `AreaRouteSnapshot` ownership;
`ElectricalRefreshChecks` covers table invalidation when exclusions invalidate a pattern.

An OCCT `wglMakeCurrent()` diagnostic may be emitted at process teardown when the offscreen
HWND is destroyed before global viewer handles. The checks pass before teardown; this is not
a manual visual-quality test. Multi-monitor/high-DPI behavior still needs manual verification.

The former `tests/PowerLead.Regression` harness is no longer part of the repository. Power-lead UI
remains behind the deliberate `WorkingRouteMigrationComplete` gate, and this harness does not claim
separate power-lead coverage.

Candidate-application regressions inject camera-preparation and post-preparation/precommit failures.
They verify that the old context and dependent selection state survive rollback, and that successful
axis changes invalidate dependent state. No preview/confirmation or diagnostic-picking UI state is
tested or claimed. IN/ON visibility through a holed trimmed face and outside its supporting-surface
extension remains covered.

The STEP cleanup helper has a separate dependency-free check from the repository root:

```powershell
& tests/step-cleanup/RemoveSurfaceBodies.Tests.ps1
```

It runs the helper in child PowerShell processes and verifies multi-relationship removal, preservation
of unrelated relations and supplemental geometry, no-surface/no-relation exit behavior, output-file
preservation on failure, and paths containing spaces.
