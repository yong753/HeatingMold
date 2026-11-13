# 배포 패키지 만들기

HeatingMold 테스트 배포본(ZIP)을 다시 만드는 절차입니다.
코드가 바뀌었을 때 이 문서의 순서만 따라가면 됩니다.

- 대상: Windows 10/11 x64, 오프라인 PC
- 형태: 자체 포함(self-contained) 다중 파일 ZIP. 사용자 PC에 .NET·VC++ 설치 불필요
- 진입점: ZIP 안의 `HeatingMold.Start.exe`

---

## 0. 준비 (처음 한 번만)

| 항목 | 확인 내용 |
| --- | --- |
| Visual Studio 2022 | C++ 워크로드 + v143 도구셋. 네이티브 브리지 빌드에 필요 |
| .NET SDK 8 | `dotnet publish` 용. 사용자 PC에는 필요 없음 |
| OCCT | `third_party\opencascade-7.9.3-vc14-64` 가 저장소에 있어야 함 |
| PowerShell | 5.1 기준. 스크립트는 **UTF-8 BOM** 으로 저장되어 있어야 함 |

스크립트 위치는 모두 `tools\deploy\` 입니다.
저장소 루트에서 실행하세요.

---

## 1. 일반 코드 변경 (화면은 그대로)

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1
powershell -ExecutionPolicy Bypass -File tools\deploy\Test-Package.ps1 -RunLauncher
```

`Build-Package.ps1` 이 순서대로 처리합니다.

1. 네이티브 브리지 빌드 (`x64\Release\OccBridge.dll`)
2. 앱 자체 포함 게시 (`win-x64`, 단일 파일 아님)
3. 시작 도구 빌드 후 패키지 루트로 복사
4. 필요한 DLL 자동 수집 (OCCT + 전이 의존 + VC 런타임)
5. OCCT 리소스 폴더와 라이선스, 안내 문서 복사
6. `deployment.json`, `build-info.json`, `manifest.sha256` 생성
7. ZIP 생성 + ZIP 해시 기록 + 추출 검증

`Test-Package.ps1` 은 ZIP을 다시 풀어 필수 파일, 해시 일치, DLL 누락, 개발 경로 노출을 검사하고
시작 도구를 `--check-only` 로 실행해 봅니다. 문제가 있으면 0이 아닌 값으로 끝납니다.

매뉴얼은 자동으로 들어가지 않습니다. 이전 매뉴얼을 그대로 쓰려면:

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 `
  -ManualPath artifacts\manual\<이전폴더>\manual
```

---

## 2. 화면·수치·버튼이 바뀐 경우

매뉴얼 그림은 마지막 캡처 시점 화면이라 자동 갱신되지 않습니다.
화면이 달라졌으면 아래 순서로 다시 만듭니다.

```powershell
# 1) 매뉴얼 없이 먼저 패키지를 만든다 (캡처 대상이 필요)
powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 -NoZip

# 2) 그 패키지 화면을 단계별로 캡처한다
powershell -ExecutionPolicy Bypass -File tools\deploy\Capture-Manual.ps1 -HoldSeconds 2.5

# 3) 캡처로 매뉴얼(HTML + PDF)을 만든다
powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Manual.ps1

# 4) 같은 버전으로 매뉴얼을 넣어 최종 ZIP을 만든다
powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 `
  -SkipNativeBuild -Version <1단계 버전> `
  -ManualPath artifacts\manual\<3단계 폴더>\manual

# 5) 검증
powershell -ExecutionPolicy Bypass -File tools\deploy\Test-Package.ps1 -RunLauncher
```

주의할 점:

- 1단계에서 버전이 `날짜-시각-커밋해시` 로 자동 생성됩니다. 콘솔에 출력되므로 4단계에 그대로 넘기세요.
- 커밋하지 않은 변경이 있으면 버전에 `-dirty` 가 붙습니다. 배포용으로 남기려면 먼저 커밋합니다.
- 4단계는 같은 버전 폴더를 지우고 다시 만듭니다. 1단계 ZIP과 그 옆의 캡처용 압축 해제본은 사라지지만,
  캡처 이미지(`artifacts\manual-capture\`)와 매뉴얼 산출물(`artifacts\manual\`)은 다른 위치라 남습니다.
- 캡처는 앱을 직접 조작하지 않습니다. 시나리오를 자동 실행하면서 단계마다 창을 찍습니다.

---

## 3. 산출물

| 경로 | 내용 |
| --- | --- |
| `artifacts\deploy\<버전>\<패키지명>.zip` | 배포용 ZIP |
| `artifacts\deploy\<버전>\<패키지명>.zip.sha256.txt` | ZIP 해시 |
| `artifacts\deploy\<버전>\<패키지명>\` | 압축 전 폴더(검토용) |
| `artifacts\deploy\<버전>\build-report.txt` | 빌드 요약 |
| `artifacts\manual-capture\<시각>\` | 캡처 원본 PNG + `captures.json` |
| `artifacts\manual\<시각>\manual\` | `index.html`, `manual.css`, `images`, PDF |

배포할 때는 ZIP과 `.sha256.txt` 두 개만 전달하면 됩니다.

---

## 4. 패키지 구성

```
HeatingMold.Start.exe          시작 도구 (VC/.NET 무의존)
처음_읽어주세요.txt
ReleaseNotes.txt
THIRD-PARTY-NOTICES.txt
build-info.json
manifest.sha256
Manual\                        매뉴얼 HTML + PDF + 이미지
App\
  HeatingMold.App.exe
  OccBridge.dll                C++/CLI 브리지
  ijwhost.dll
  (게시된 .NET/WPF 파일 전체)
  (OCCT 및 전이 DLL, VC 런타임 DLL)
  deployment.json              배포 모드 표시
  resource\Mold_Cover_Trim.step
  occt-resources\              OCCT 런타임 리소스
Licenses\
```

시작 도구가 하는 일:

1. 64비트 여부와 Windows 버전 확인
2. 패키지 필수 파일 존재 확인
3. `manifest.sha256` 으로 모든 파일 무결성 검사
4. 앱 실행 (작업 폴더는 `App\`)
5. 15초 뒤 정상 종료

앱이 파일을 못 찾거나 시작 직후 비정상 종료하면 원인별 코드로 끝납니다.
`--check-only` 는 앱을 띄우지 않고 점검만, `--diagnostics` 는 로그 폴더를 엽니다.

---

## 5. 로그

- 앱: `%LOCALAPPDATA%\HeatingMold\Logs\<실행시각>-<PID>\` 의 `app.log`, `environment.json`, `occ_viewer_log.txt`
- 시작 도구: `%LOCALAPPDATA%\HeatingMold\Logs\launcher.log`

`environment.json` 에는 배포 모드 여부, 실제 앱 폴더, OCCT 리소스 경로, OS/런타임 버전이 남습니다.
사용자 문의를 받으면 이 폴더를 요청하세요.

---

## 6. 자주 나는 문제

| 증상 | 원인 / 조치 |
| --- | --- |
| 빌드가 파일 잠금으로 실패 | 실행 중인 HeatingMold 앱과 시작 도구를 닫고 다시 실행 |
| 버전에 `-dirty` 가 붙음 | 커밋하지 않은 변경이 있음. 의도한 것이면 그대로 사용 |
| 매뉴얼이 ZIP에 없음 | `-ManualPath` 를 주지 않았거나 `index.html` 이 없는 폴더를 지정 |
| 매뉴얼 그림이 예전 화면 | 캡처를 다시 하고 3~4단계 재실행 |
| `Test-Package.ps1` 이 실패 | 출력된 문제 목록 확인. 대부분 파일 누락이거나 해시 불일치 |
| 패키지 앱이 시작 직후 종료 | `%LOCALAPPDATA%\HeatingMold\Logs` 의 `app.log` 확인 |

---

## 7. 배포 전 확인 항목

자동 검사로 확인되지 않는 것들입니다.

- 깨끗한 Windows 10 x64 PC(별도 장비 또는 VM)에서 실행
- 관리자 권한이 없는 표준 사용자 계정
- 화면 배율 100% / 125% / 150%
- 한글·공백이 포함된 사용자 폴더 경로
- 앱을 완전히 종료한 뒤 새 프로세스에서 프로젝트 복원
- 외부 CAD 프로그램에서 DXF / STEP 열기
- 제3자 라이선스 원문과 CAD 모델 외부 전달 권한

기능 회귀까지 보려면 배포본을 푼 상태로 시나리오를 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File tests\HeatingMold.VisualTest\Run.ps1 `
  -Scenario e2e-full `
  -AppExe <풀어둔패키지>\HeatingMold.Start.exe `
  -ModelPath <풀어둔패키지>\App\resource\Mold_Cover_Trim.step `
  -RunDir artifacts\visual-test\<새폴더> `
  -HoldSeconds 1 -TimeoutSeconds 600 -ExpectPackaged
```

시나리오는 `e2e-full`, `e2e-multi-area`, `e2e-margin-spacing-exit`, `e2e-split-safety`, `e2e-split-four` 다섯 개입니다.

---

## 8. 스크립트를 고칠 때

- `.ps1` 은 반드시 **UTF-8 BOM** 으로 저장합니다. BOM이 없으면 Windows PowerShell 5.1이 CP949로 읽어 한글 문자열에서 구문 오류가 납니다.
- JSON, HTML, CSS는 BOM 없이 저장합니다.
- PowerShell 함수 파라미터에 `[byte[]]` 형을 붙이면 큰 배열을 넘길 때마다 전체 복사가 일어납니다. DLL 검사 코드는 형 선언 없이 슬라이스로 처리합니다.
- StrictMode에서 `@(...) | Where-Object` 는 결과가 1개면 배열이 아니어서 `.Count` 가 실패합니다. 파이프라인 전체를 `@()` 로 감쌉니다.
- 설정 값(경로, 리소스 폴더 목록, 제외 패턴)은 `ReleaseConfig.json` 에 있습니다. OCCT나 도구셋을 바꿀 때만 수정하면 됩니다.
