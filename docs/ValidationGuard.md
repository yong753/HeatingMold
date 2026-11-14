# 검증 실행 가드 (Validation Guard)

자동 검증(시각 테스트, 임시 오프스크린 하네스, `dotnet` 하네스)을 실행할 때
**예상하지 못한 모달 대화상자**가 뜨면, 지금까지는 사람이 그 창을 닫을 때까지
`Run.ps1`이나 서브에이전트가 timeout까지 기다렸습니다. 이 가드는 그 대기를 없애기
위한 실행 래퍼입니다.

- 실패는 **즉시** 보고합니다. 대화상자를 자동으로 닫거나 승인하지 않습니다.
- 실패 시 **가드가 새로 만든 프로세스 트리만** 종료합니다. 이미 떠 있던 다른 앱,
  다른 검증 프로세스, WER/시스템 오류 창은 건드리지 않습니다.
- 성공하면 검증 앱 창을 **닫지 않고 그대로 둡니다**(기존 육안 확인 계약 유지).
- 제품 코드(`src\HeatingMold.App`, `src\OccBridge`)는 이 기능으로 바뀌지 않습니다.

## 실제 사례 (참고)

한 번은 이미 화면에 떠 있던 `AppGate.exe`(다른 에이전트의 native gate)의 WPF 오류
창이 검증을 막았습니다. 부모 세션이 프로세스 경로·생성 시각·부모 실행 스크립트를
확인한 뒤, owner가 비활성화된 visible WPF 창임을 확인하고 UIA `ValuePattern`으로
readonly TextBox의 exception/stack 전체를 수집해 **그 프로세스 하나만** 종료했습니다.

증거 파일(다른 에이전트 소유, 수정 금지, 승인된 Temp 하위 상대 구조):

- `%TEMP%\opencode\fgate\supervisor-modal-<timestamp>.json`
- `%TEMP%\opencode\fgate\supervisor-modal-<timestamp>.txt`

이 가드는 같은 판정(소유 범위 → visible 여부 → owner 비활성 → UIA 본문)을 자동화한
것입니다. **다만 이미 실행 중인 프로세스에는 소급 적용되지 않습니다.** 가드는 자기가
새로 시작한 프로세스 트리만 감시하고 종료합니다. 이미 떠 있는 창은 위 사례처럼 별도로
소유 근거를 확인한 뒤 사람이/부모가 처리해야 합니다.

## 사용법

### 1. 시각 테스트 (`Run.ps1`) — 자동 적용

`tests\HeatingMold.VisualTest\Run.ps1`은 이제 실행/대기를 가드에 위임합니다. 명령과
성공 시 동작(앱 창 유지)은 이전과 같습니다.

```powershell
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-full -NoBuild
```

가드가 실패하면 `Run.ps1`은 다음을 출력하고 종료 코드 **3**(기존 "결과 없음"과 같은
비정상 코드)으로 끝납니다.

```
Validation guard stopped the run: unexpected-dialog — ...
Unexpected dialog: 오류 — HeatingMold [HwndWrapper[...]]
Last step: {"event":"start","index":24,...}
Run directory: ...\artifacts\visual-test\<id>
Guard report: ...\validation-guard.json
```

### 2. 명령줄

```powershell
powershell -ExecutionPolicy Bypass -File tools\validation\Invoke-GuardedProcess.ps1 `
    -ExePath .\Harness.exe `
    -Arguments "--model model.step" `
    -WorkingDirectory .\harness `
    -ResultPath .\run\result.json `
    -RunDirectory .\run `
    -TimeoutSeconds 600 `
    -ExitWithCode
```

- `-ResultPath`가 생기면 성공입니다. 생략하면 프로세스가 스스로 정상 종료(exit 0)할
  때만 성공합니다. 장시간 떠 있는 검증 앱에는 반드시 `-ResultPath`를 주세요.
- `-ResultPath`가 **실행 전에 이미 있으면** 이전 결과를 성공으로 오인하지 않도록
  시작 자체를 거부합니다(사유 `result-exists`).
- `-ExePath`에 경로 구분자가 없는 이름(`dotnet`, `cmd`)을 주면 PATH에서 `.exe`만
  찾아 해석합니다. PATH에서 찾은 항목이 `.exe`가 아니면 해석 단계에서 거부합니다.
- `-ExitWithCode`를 주면 성공 0 / 실패 1로 종료합니다.
- 여기서 성공은 **결과 파일 게시 완료 또는 프로세스의 정상 종료(exit 0)** 입니다.
  검증 시나리오 자체의 성공 판정은 `result.json`의 `Status`를 읽는 호출자(예:
  `Run.ps1`)가 합니다.

### 3. 함수로 사용

```powershell
. .\tools\validation\GuardedProcess.ps1
$r = Invoke-GuardedProcess -ExePath $exe -Arguments $args -RunDirectory $run `
    -ResultPath (Join-Path $run 'result.json') -TimeoutSeconds 90
if (-not $r.Success) { ... $r.Reason; $r.ReportPath ... }
```

### 4. 임시 오프스크린 하네스 (서브에이전트)

`MainWindow`를 직접 만드는 임시 하네스는 다음 중 하나를 **반드시** 지켜야 합니다.

1. 이 가드로 실행한다.

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\validation\Invoke-GuardedProcess.ps1 `
       -ExePath dotnet.exe -Arguments "MyHarness.dll" -RunDirectory C:\temp\harness-run `
       -TimeoutSeconds 600 -ExitWithCode
   ```

2. 또는 하네스 코드에서 **`MainWindow` 생성 전에** `ErrorReporter.SuppressDialogs = true`를
   설정하고, 제품 함수가 반환한 오류/경고를 직접 검사해 실패로 처리한다.
   (`ErrorReporter`는 `MainWindow`가 뜬 뒤에야 시각 테스트가 `SuppressDialogs`를 켭니다.
   그 전에 난 오류는 모달이 될 수 있습니다.)

두 방법 모두 "오류를 자동 승인"하는 것이 아니라, **오류를 기록하고 실패로 끝내는**
방법입니다.

## 감지 규칙

가드가 새로 시작한 프로세스 트리(job object)에 속한 프로세스의 **보이는 top-level
창**만 검사합니다. 200~300ms(기본 250ms) 주기입니다.

다음 중 하나면 "예상하지 않은 대화상자"입니다.

| 조건 | 예 |
| --- | --- |
| 표준 대화상자 클래스 `#32770` | Win32 `MessageBox`, 파일 열기/저장 대화상자 |
| owner가 비활성화된 visible owned 창 | WPF `Window.ShowDialog()`, WinForms `Form.ShowDialog()` |
| 제목에 `HeatingMold` + `오류`/`경고`/`시작 실패` | `오류 — HeatingMold`, `경고 — HeatingMold`, `HeatingMold 오류`, `HeatingMold 시작 실패` |

오탐을 막기 위해 다음은 제외합니다.

- 추적 트리에 속하지 않는 프로세스의 창(다른 앱, WER, 셸)
- 정상 앱 주 창(owner 없음, `#32770` 아님, 오류 제목 아님)
- owner가 **활성** 상태인 owned 도구 창(모달이 아님)
- IME/툴팁/메뉴/그림자 등 유틸리티 클래스

감지해도 **버튼을 누르지 않습니다.** 파일 대화상자나 확인 대화상자도 noninteractive
검증에서는 실패로 처리합니다.

## 실패 사유

| Reason | 의미 |
| --- | --- |
| `unexpected-dialog` | 예상하지 않은 모달 창 |
| `timeout` | 전체 대기 시간 초과. 남은 트리 정리 후 실패 |
| `root-nonzero-exit` | 결과 파일 없이 root가 0이 아닌 코드로 종료 |
| `early-exit` | 결과 파일 없이 root가 정상 종료 |
| `result-exists` | 이전 결과 파일이 이미 있음(시작 거부) |
| `job-create` / `job-assign` / `job-limit` | 소유 범위(job)를 만들/붙이거나 kill-on-close를 설정하지 못함 → 프로세스 이름으로 전체 종료하지 않고 실패 |
| `job-query` | 추적 트리 목록을 읽지 못함 → 중단 |
| `job-detach` | 성공 후 kill-on-close를 안전하게 해제하지 못함 → 앱을 남기지 않고 실패 |
| `launch` / `launch-resume` | 프로세스 시작/재개 실패 |
| `invalid-*` | 옵션/경로 오류 |
| `report-write-failed` | 감시 보고서를 쓰지 못함 → JSON을 failed로 보정하고 검증 트리를 정리한 뒤 실패 |

## 산출물

실행 폴더에 UTF-8(JSON은 BOM 없음)로 남깁니다.

- `validation-guard.json`
- `validation-guard.txt`

주요 필드:

- `status`, `reason`, `message`, `detail`
- `rootProcessId`, `rootExitCode`, `rootExited`, `commandLine`, `startTimeUtc`
- `dialog.title/className/processId/processPath/ownerTitle/ownerDisabled`
- `dialog.body/bodyLength/bodySource/bodyError`
- `lastStep` — `steps.jsonl`의 마지막 줄(최대 1000자)
- `trackedProcessIds` — 실패/성공 판정 시점에 추적하던 PID(초기 증거)
- `remainingProcessIds` — 정리 후에도 job에 남았거나 확인하지 못한 PID. 정리가
  확인되면 빈 배열, 성공 시에는 유지 중인 창의 PID입니다.
- `cleanupStatus` — `not-required`(정리 불필요/성공 유지), `confirmed`(job query가
  실제로 비었음), `unconfirmed`(종료/빈 job 미확인)
- `log`

각 보고서 파일은 임시 파일을 만든 뒤 기존 대상이 있으면 `File.Replace`, 없으면
`File.Move`로 교체합니다. **JSON과 TXT 두 파일은 하나의 transaction이 아닙니다.**
본문 수집은 UIA `ValuePattern`/`TextPattern`과 Win32 자식 컨트롤 텍스트 폴백을
**하나의 STA worker**에서 수행하고, 감시 스레드는 전체 수집 시간을
**최대 1초**(`-TextCaptureLimitMs`)만 기다립니다. Win32 폴백은 자식마다 남은 시간만큼만
`SendMessageTimeout`을 쓰고 자식 수/텍스트 수에도 상한이 있으며, `GetWindowTextLength`
같은 동기 메시지로 무제한 대기하지 않습니다. 상한을 넘으면 UIA/Win32 수집을 중단하고
**창 제목만** 남깁니다(본문 없음은 성공이 아님). 보고서 본문은 8000자에서 잘리고
`bodyLength`에 원래 길이가 남습니다.

## 종료/정리 정책

- 감시 중에는 job에 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`가 켜져 있습니다. 가드
  프로세스가 외부 timeout/Ctrl+C/강제 종료로 죽으면 OS가 job handle을 닫으면서
  **추적 중인 검증 트리도 함께 종료**됩니다. 무관한 프로세스는 job 밖이므로 유지됩니다.
- 실패: `TerminateJobObject`로 추적 트리만 종료하고, **job query가 실제로 빈 목록을
  반환했을 때만** 정리 완료로 확인합니다. PID 종료 검사는 `OpenProcess` 실패(access
  denied/unknown)를 종료로 오인할 수 있어 확인 근거로 쓰지 않습니다. 확인되지 않으면
  "정리 완료"라고 단정하지 않고 `cleanupStatus=unconfirmed`와 남은 PID를 기록합니다.
- 성공: 결과(또는 정상 종료)가 확정되어도 **곧바로 detach하지 않습니다.** 성공 보고서
  JSON/TXT 게시를 kill-on-close가 켜진 상태에서 먼저 끝내고, 그 뒤 플래그가 여전히
  켜져 있는지 다시 조회한 다음 **마지막 단계로** 플래그를 해제·재조회합니다. 따라서
  보고서 I/O 도중 host가 죽어도 추적 트리가 함께 정리되어 고아 앱이 남지 않습니다.
  detach 이후에는 보고서 I/O를 하지 않습니다. 해제가 실패하면 `job-detach`로 실패
  처리하고, kill-on-close 재설정을 시도한 뒤 트리를 정리합니다.
- 감시 보고서 쓰기에 실패하면(예: `validation-guard.txt` 경로가 디렉터리) 성공으로
  게시하지 않습니다. 이미 쓴 JSON은 `status=failed`, `reason=report-write-failed`로
  다시 쓰고, 검증 트리를 정리한 뒤 실패로 반환합니다.
- root가 정상 종료한 launcher(`HeatingMold.Start.exe` 등)여도 자식 앱은 같은 job에
  있으므로 결과가 나올 때까지 계속 감시합니다.
- 결과 파일과 모달이 같은 시점에 감지되면 **모달 오류를 우선**합니다.

## 한계

- **소급 적용 없음.** 가드 시작 전에 이미 떠 있던 창은 감지/종료하지 않습니다.
- 결과 파일이 게시된 **뒤에** 늦게 뜨는 모달은 감지하지 못할 수 있습니다(감지 주기).
  결과 직후 한 번 더 스캔하지만, 그 이후는 보장하지 않습니다.
- UIA provider가 응답하지 않으면 본문이 비고 제목/사유만 남을 수 있습니다.
- 감시 프로세스가 강제 종료되면 추적 트리도 함께 종료됩니다(의도된 kill-on-close).
  검증 창을 남기려면 가드가 정상 성공 경로로 끝나야 합니다.
- 파일/승인 대화상자는 감지 시 실패로 처리하며 자동 조작하지 않습니다.
- 자식 프로세스가 스스로 job을 깨고 나가는 경우(`CREATE_BREAKAWAY_FROM_JOB` 허용)는
  추적하지 못합니다. 제품 앱/시작 도구는 그렇게 하지 않습니다.
- 실제 마우스/키보드 조작, 화면 육안 판정, 외부 CAD 출력은 여전히 별도 확인입니다.

## 회귀

```powershell
powershell -ExecutionPolicy Bypass -File tests\ValidationGuard\Run-GuardTests.ps1
```

fixture는 `tests\ValidationGuard\Fixtures\FixtureHost.cs`이며, 컴파일 결과는
`%LOCALAPPDATA%\HeatingMold\ValidationGuard\fixtures\<hash>\`에만 생성됩니다.
제품 앱/네이티브를 빌드하지 않습니다. 회귀는 112개 체크(모달/launcher 자식/성공 창
유지/timeout 정리/무관 프로세스 보존/이전 결과 거부/모달 우선/본문 상한/보고서 쓰기
실패/보고서 게시 후 detach 순서/PATH exe/가드 강제 종료 시 kill-on-close/Run.ps1
연결)를 실행합니다. 300개 무응답 자식 테스트는 설정한 수집 상한 이후 감시가 진행되어
**전체 실행이 10초 이내에 실패·정리**되는지를 확인하며, 300ms 안에 모든 단계가 끝난다는
의미가 아닙니다.

## 검증 상태 (2026-11-15 부모 재검증)

증거는 승인된 Temp 하위의 상대 구조로만 둡니다(사용자 계정 경로/PID는 기록하지 않음).

- **standalone 가드 회귀**: `opencode\guard-parent-review\standalone-<timestamp>\` 새 실행
  폴더에서 `Run-GuardTests.ps1`을 다시 실행해 **EXIT 0, 112 PASS / 0 FAIL**, 로그 전체를
  부모가 직접 확인했습니다. synthetic fixture만 사용하며 제품/네이티브를 빌드하지 않습니다.
- **실제 App 7단계 스모크**: 제품/네이티브 빌드 없이 기존 App 출력의 exe/App.dll/Core.dll/
  OccBridge.dll을 `opencode\guard-parent-review\actual-app-<timestamp>\app\`에 복사했고,
  복사 전/후/staged의 SHA-256이 모두 같음을 확인했습니다(제품 코드 무수정). 원본
  `e2e-multi-area`의 첫 6개 actions와 기대값을 그대로 임시 복사한 시나리오(암묵
  `loadModel` 포함 7 steps)를 `Run.ps1 -NoBuild -AppExe <staged exe> -ModelPath
  <repo>\resource\Mold_Cover_Trim.step -HoldSeconds 0.1 -TimeoutSeconds 180`으로 실행했습니다.
  OCCT/App.Tests 런타임 PATH와 `CSF_OCCTResourcePath`를 명시하고 sewn 환경 변수는 해제했습니다.
  결과: **EXIT 0**, `Status=Completed`, `Automation=Completed`, **7/7 OK**,
  `VisualReview=NotRecorded`. 가드 보고서는 **succeeded / result-written /
  cleanupStatus=not-required / reportWriteFailed=false**였고, 결과 게시 후에도 실제 앱
  main 창이 살아 있었으며 로드된 3개 모듈이 모두 staged 경로였습니다. staged SHA-256:
  App `D0F8E301B4AC0C69BF2F86E1A5F1F92E23F2FB4DBDB75A7BC6C92C9E7F6AF5E8`,
  Core `7439E7210A904F7112474FBD2E0BA75CCC0CD020F749B36A2CE83AAA0049F656`,
  OccBridge `1EB93388F79D1EF2F3CE505F2D041B07812762A76CDDD396FBDF6BF488D9EE28`.
  7단계 기준 결과는 A 60조각/29턴/1회로/18,440.993 mm입니다. 테스트 후에는 정확한 경로·
  생성 시각·process handle 소유를 확인한 **그 스모크 프로세스만** Kill/Wait로 정리했고
  사용자 앱은 건드리지 않았습니다.
- 여전히 미검증: 실제 제품 오류 모달이 발생하는 경로, 전체 54단계 시나리오/5개 시나리오
  전체 실행, 실제 마우스/키보드/화면 육안 판정, 보고서 게시 도중 host 강제 종료의 직접
  재현, `job-detach`/`job-query` 강제 실패, 실제 서드파티 UIA provider 완전 정지, 실제
  파일 열기/저장 대화상자. synthetic 112개 체크와 실제 App 7단계 스모크는 구분해서
  해석해야 합니다.
