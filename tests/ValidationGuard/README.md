# Validation Guard 회귀

`tools\validation\GuardedProcess.ps1`(검증 실행 가드)의 독립 회귀입니다.
제품 App/네이티브를 빌드하지 않고, fixture 프로세스만 사용합니다.

## 실행

```powershell
powershell -ExecutionPolicy Bypass -File tests\ValidationGuard\Run-GuardTests.ps1
```

- `-WorkRoot <폴더>`: 산출물 위치(기본 `%TEMP%\hm-guard-tests\<timestamp>`)
- `-PollIntervalMs`(기본 150) / `-TimeoutSeconds`(기본 25): 시험 속도 조절용
- 종료 코드 0 = 전부 통과, 1 = 실패

## fixture

`Fixtures\FixtureHost.cs`를 `Fixtures\Build-Fixtures.ps1`이
`%LOCALAPPDATA%\HeatingMold\ValidationGuard\fixtures\<hash>\FixtureHost.exe`로 컴파일합니다.
소스가 바뀌면 새 hash 폴더에 다시 컴파일합니다.

| mode | 동작 |
| --- | --- |
| `messagebox` | Win32 `MessageBox`(`#32770`) |
| `reporter [--empty-body\|--long-body\|--no-owner]` | 제품 `ErrorReporter`와 같은 제목/owner/readonly TextBox의 WPF 창 |
| `launcher <childExe> [args]` | 자식을 띄우고 즉시 exit 0 |
| `result <resultPath> [--tool-window]` | 결과 JSON 작성 후 창 유지, 선택적으로 owned 도구 창 추가 |
| `result-dialog <resultPath>` | 결과 JSON 작성 후 200ms 뒤 모달 표시(모달 우선 확인) |
| `earlyexit` / `earlyexit-zero` | 결과 없이 exit 3 / 0 |
| `idle [title]` | 결과 없이 창 유지(timeout/무관 프로세스 보존 확인) |
| `slow-body` | 300개 Edit 자식 + 메시지 루프 없음: UIA/Win32 수집 상한 확인 |
| `visual-result` / `visual-modal` | `Run.ps1`이 넘기는 `--visual-run-dir`를 해석하는 stub |

`HM_GUARD_FIXTURE_MODE` 환경 변수는 `Run.ps1` 경유 시험에서만 stub 모드를 고릅니다.

## 확인 항목

1. 표준 MessageBox, WPF reporter(owner disabled, UIA 본문), owner 없는 WPF 오류 창
2. launcher 정상 종료 후 자식 모달 감지, `trackedProcessIds`/`cleanupStatus=confirmed`
3. 결과 작성 후 앱 창 유지, 성공 보고서가 kill-on-close 해제보다 먼저 게시되는 순서
4. timeout이 추적 트리만 정리하고 무관한 프로세스는 보존, job query 빈 목록으로 확인
5. owned 도구 창 오탐 없음
6. 결과 없는 비정상/정상 종료, 이전 결과 파일 재사용 거부
7. 결과+모달 동시 발생 시 모달 우선
8. 긴 본문/빈 본문/수집 상한(UIA hang 방지) 경로. 다수·무응답 자식 컨트롤에서도
   설정한 수집 상한 적용 후 감시가 진행되어 10초 이내 실패·정리되는지 확인
9. 보고서 쓰기 실패(`validation-guard.txt`가 디렉터리) 시 JSON failed 보정·트리 정리,
   detach 로그가 없음
10. PATH 이름(`cmd`) 실행 해석
11. 가드 host를 별도 프로세스로 띄운 뒤 그 프로세스만 강제 종료: 추적 child는
    kill-on-close로 종료, 무관 프로세스는 유지
12. `Run.ps1` 연결: 성공 요약, 모달 실패 시 nonzero와 가드 보고서

버튼 자동 클릭/승인은 어느 경로에도 없습니다. 실제 앱 시각테스트는
`tests\HeatingMold.VisualTest\Run.ps1`이 같은 가드를 사용합니다.
