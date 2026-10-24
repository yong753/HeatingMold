# GEO-00: 실제 Mold 기하 조사

이 기록은 `resource/Mold.stp`에 대한 재현 가능한 기하 관측을 모으는 실험 절차다. 테스트 fixture의 통과를 실제 금형 검증으로 대신하지 않는다.

## 입력 고정

현재 저장소의 기준 모델은 다음 파일이다.

| 항목 | 값 |
|---|---|
| 파일 | `resource/Mold.stp` |
| 크기 | 2,500,626 bytes |
| SHA-256 | `B9E93702868F775EEE0478C776E8A98071AABBB4080465E4C6B4B444A6157379` |
| STEP 제품명 | `DT24-020 FRONT INTERIOR PANEL_LSE24_240202.stp` |
| 확인 상태 | Native experiment 미실행 |

파일이 바뀌면 결과 JSON의 해시가 달라진다. 서로 다른 모델 결과를 같은 몰드의 재실험으로 합치지 않는다.

## 실행

Visual Studio C++/CLI `MSBuild.exe`와 프로젝트가 요구하는 OCCT 7.9.3 배포 파일이 있는 x64 개발 환경에서 저장소 루트에서 실행한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1 `
    -Configuration Release -Platform x64 -Group WorkingSide `
    -Geo00MoldPath resource/Mold.stp
```

정상 실행은 기존 WorkingSide fixture 회귀 뒤 실제 Mold 질의와 11개 통제 fixture 질의를 수행한다. 실제 Mold 결과는 `x64/Verification/<run-id>/fixtures/WorkingSide/GEO-00/report.json`에 저장한다. 평판, 30°/60° 경사면, B-spline, 반원통, 구, 가림 적층, 고리 홀, trimmed 홀, 접선 후면과 3면 경계 fixture 결과는 `control-fixtures/<이름>.json`에 각각 저장한다. 실행 manifest에는 입력 모델과 두 보고서 경로가 기록된다. Native preflight가 환경 문제로 중단되면 실험 결과로 기록하지 않는다.

## 수집 범위

`Geo00Experiment`는 STEP의 SHA-256과 파일 크기, Native Solid/Shell/Face/Plane 수, OCCT 표면 종류별 면 개수·면적, 면적이 큰 Face 목록을 저장한다. 실제 Mold는 `+X/-X`, `+Y/-Y`, `+Z/-Z` 각 방향에서 작업 context를 만들고 전체 메시 투영 범위를 17×17 격자로 조회한다. 통제 fixture는 `+Z` 방향을 사용한다. 격자별 first-hit·miss·tangent·오류 수와 가시 Face 표본 수, 메시 편차와 실행 시간을 기록한다.

한 Face에서 격자 표본을 두 개 이상 얻으면, 제한된 후보 중간점을 다시 질의해 같은 Face의 first-hit으로 이어지는 경로를 찾는다. 실제 Mold에서는 최장 경로 하나를, 통제 fixture에서는 최장·작업 U방향·작업 V방향 후보를 각각 검사해 접선 방향 변화에 따른 결과를 비교한다. 경로별 작업 좌표 끝점·길이·방향을 기록하고, 선경 0.15/0.30 mm와 요구 깊이 0/0.15/1.0 mm 조합을 Native 깊이 보정에 전달한다. 성공한 보정만 독립 envelope 질의로 다시 측정한다. 같은 Face 경로가 없으면 `NotFound`와 진단을 남겨 빈 깊이 결과의 이유를 구분한다. 입력 끝점·Face, 보정 상태, 잔차·오차 상한, probe 수, 깊이 최소/최대와 실행 시간을 원시 값으로 남긴다.

이 표본 조사는 유한 격자와 제한된 경로 후보에 대한 실험이다. 표본 사이의 가림이나 미세 구멍이 없다는 증명, 실제 사용자가 선택한 유효 영역의 공통 출구 측면 증명, 임의 경로의 깊이 정확도 보증으로 해석하지 않는다. 같은 Face 경로가 발견되지 않거나 Native 상태가 `Unresolved`이면 그 상태를 결과로 남기고 임의 성공값을 만들지 않는다.

## 미완료 측정

현재 실행 환경에는 C++/CLI 지원 `MSBuild.exe`와 빌드된 `OccBridge.dll`이 없어 이 실험은 아직 실행되지 않았다. 다음 증거를 확보하기 전에는 GEO-00을 완료로 표시하지 않는다.

- 실제 Mold에 대한 축·Face·앵커 선택을 사람이 검토하고, 선택 이유와 모델 해시를 함께 기록한다.
- 평판/경사 fixture에서 30°·60°와 접선 방향 변화에 따른 깊이 오차를 해석 기준값과 비교한다.
- 원통 및 대표 B-spline 곡면의 깊이·간격에서 수렴, 지원 범위와 미확정 이유를 기록한다.
- 같은 영역의 양 끝점이 동일 출구 측면을 만족하는 성공 사례와 실제 실패 사례를 기록한다.
- 좁은 가림/구멍과 trimmed 경계에서 first-hit 및 실제 면적 오차·세분화 수렴을 확인한다.
- 각 결과에 최대 관측 오차, 지원 범위, 실패 조건, 총 실행 시간과 probe 수를 포함한다.

보고서만 생성되거나 합성 STEP fixture만 통과한 상태는 실제 Mold 기하의 완료 증거가 아니다.
