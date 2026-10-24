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

An OCCT `wglMakeCurrent()` diagnostic may be emitted at process teardown when the offscreen
HWND is destroyed before global viewer handles. The checks pass before teardown; this is not
a manual visual-quality test. Multi-monitor/high-DPI behavior still needs manual verification.

The `tests/PowerLead.Regression` harness retains its native legacy projection checks. Its UI
regressions now verify the deliberate gate before/after working-side confirmation. Restored pending
state is seeded explicitly to keep cleanup/late-hover tests non-vacuous; it does not enable the old
UI route. Its README identifies automatic power entry/queueing as future migration work.

Candidate-application regressions inject camera-preparation and post-preparation/precommit failures.
They verify that the old context and dependent selection state survive rollback, and that successful
axis changes invalidate dependent state. No preview/confirmation or diagnostic-picking UI state is
tested or claimed. IN/ON visibility through a holed trimmed face and outside its supporting-surface
extension remains covered.

## GEO-00 actual-mold probe

When the Visual Studio C++/CLI and OCCT build environment is available, the verifier can append a
bounded actual-model probe to the normal WorkingSide regression:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1 `
    -Configuration Release -Platform x64 -Group WorkingSide `
    -Geo00MoldPath resource/Mold.stp
```

The actual-mold report is written to
`x64/Verification/<run-id>/fixtures/WorkingSide/GEO-00/report.json`. It records the STEP SHA-256,
shape and surface inventory, a 17-by-17 first-hit grid for each signed world axis, and bounded
depth-adjustment/envelope probes for same-Face paths found in that grid. Eleven control reports
are written under `GEO-00/control-fixtures/` for the flat plane, 30°/60° slopes, B-spline, half
cylinder, sphere, layered occlusion, ring and trimmed holes, tangent-back surface, and three-panel
surface boundary. These scenarios use +Z and record up to three paths spanning the longest, working
U, and working V directions; each report retains its input STEP hash. The reports are
exploratory evidence: the harness requires all three directions in the flat and 30°/60° slope
reports, but the grid does not prove visibility between samples, and a same-Face pair does not by
itself establish an approved production region or common exit side. See
`docs/verification/GEO-00.md` for the parameter set and remaining checks.
