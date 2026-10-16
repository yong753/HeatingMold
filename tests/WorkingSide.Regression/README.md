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
It covers screen picking, visibility at the candidate point, zoom/pan, perspective,
revision ownership, preview/confirmation separation, and the intentional route-generation gate.

An OCCT `wglMakeCurrent()` diagnostic may be emitted at process teardown when the offscreen
HWND is destroyed before global viewer handles. The checks pass before teardown; this is not
a manual visual-quality test. Multi-monitor/high-DPI behavior still needs manual verification.

The `tests/PowerLead.Regression` harness retains its native legacy projection checks. Its UI
regressions now verify the deliberate gate before/after working-side confirmation. Restored pending
state is seeded explicitly to keep cleanup/late-hover tests non-vacuous; it does not enable the old
UI route. Its README identifies automatic power entry/queueing as future migration work.

Preview regressions inject both false-return and exception failures after candidate geometry and
camera preparation, from empty, confirmed, and existing-preview states. They compare native
boundary bounds/marker, screen projection, logical references and pick mode before/after rollback.
Cancel/replacement/confirmation and rejected checkbox entry are covered, as are IN/ON visibility
through a holed trimmed face and outside its supporting-surface extension.
