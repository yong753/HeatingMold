# validation tools

검증 프로세스가 예상치 못한 모달 대화상자에서 멈추지 않도록 감시 실행하는 도구입니다.
자세한 규칙과 한계는 [docs/ValidationGuard.md](../../docs/ValidationGuard.md)를 참고하세요.

## 파일

- `GuardedProcess.ps1` — `Invoke-GuardedProcess` 함수. dot-source 해서 사용합니다.
- `Invoke-GuardedProcess.ps1` — 명령줄 진입점.
- `GuardedProcessHost.cs` — 실제 감시/판정/수집 구현. `Add-Type`으로 컴파일합니다.

## 빠른 사용

```powershell
# Run.ps1은 자동으로 이 가드를 사용한다.
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-full -NoBuild

# 임의 프로세스
powershell -ExecutionPolicy Bypass -File tools\validation\Invoke-GuardedProcess.ps1 `
    -ExePath dotnet.exe -Arguments "Harness.dll" `
    -RunDirectory C:\temp\harness-run -ResultPath C:\temp\harness-run\result.json `
    -TimeoutSeconds 600 -ExitWithCode
```

## 동작 요약

- 새로 만든 프로세스만 job object로 추적합니다(종료 범위는 job으로만 제한하며 프로세스
  이름으로 전체 종료하지 않습니다). 감시 중에는 kill-on-close가 켜져 있어 가드
  프로세스가 강제 종료되면 추적 트리도 함께 정리됩니다.
- 성공 시에는 성공 보고서(JSON/TXT)를 kill-on-close가 켜진 상태에서 먼저 게시하고,
  그 뒤 마지막 단계로 플래그를 해제·재조회한 다음 handle을 닫아 앱 창을 유지합니다.
  보고서 I/O 도중 host가 죽어도 고아 앱이 남지 않습니다.
- 정리 확인은 job query가 실제로 빈 목록을 반환했을 때만 인정합니다(`cleanupStatus`).
  `trackedProcessIds`는 초기 추적 증거, `remainingProcessIds`는 정리 후 남았거나
  확인하지 못한 PID입니다.
- 예상하지 않은 모달이면 그 트리만 종료하고 `validation-guard.json/txt`를 남깁니다.
  보고서를 쓰지 못하면 JSON을 failed로 보정하고 트리를 정리한 뒤 실패합니다.
- 대화상자 버튼은 절대 자동으로 누르지 않습니다.
- `-ExePath`에 경로 없는 이름(`dotnet`, `cmd`)을 주면 PATH에서 `.exe`를 찾습니다.
- 성공은 결과 파일 게시/정상 종료만 뜻하며, 시나리오 성공 판정은 호출자가 합니다.

## 참고

- `*.ps1`은 PowerShell 5.1이 비ASCII를 CP949로 읽지 않도록 **UTF-8 BOM**으로 저장합니다.
- 컴파일 캐시는 `%LOCALAPPDATA%\HeatingMold\ValidationGuard\`에 생성되며 저장소에
  산출물을 남기지 않습니다.
- 회귀: `tests\ValidationGuard\Run-GuardTests.ps1`
