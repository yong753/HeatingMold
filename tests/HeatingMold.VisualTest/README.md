# HeatingMold 시각 테스트

실제 앱 창을 띄운 채 기존 기능을 코드로 순서대로 실행하고, 각 단계가 끝난 화면을
일정 시간 유지해 눈으로 확인하는 테스트입니다. OS 마우스/키보드 입력이나 UI Automation은
사용하지 않습니다.

> 저장소에는 대표 시나리오 세 개가 포함되어 있습니다.
> `scenarios/e2e-full.json`(한 영역 생성·편집·분할·저장/복원 전체 흐름),
> `scenarios/e2e-multi-area.json`(독립적인 두 영역의 상호 보존),
> `scenarios/e2e-margin-spacing-exit.json`(다른 마진·간격·인출점 조건의 보존·전력 재배분)입니다.
> 형식은 [docs/VisualTest.Scenarios.md](../../docs/VisualTest.Scenarios.md)를 참고하세요.

## 실행

앱 헤더의 **시각 테스트…** 버튼으로도 실행할 수 있습니다. 시나리오, 결과 폴더,
관찰 시간, 한 단계씩 실행 여부를 지정하면 현재 창은 그대로 두고 새 앱 창에서
테스트가 시작됩니다. 시나리오 경로가 비어 있으면 실행하지 않고 안내만 표시합니다.

```powershell
# 포함된 대표 시나리오 실행 (이름만 지정)
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-full
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-multi-area
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-margin-spacing-exit

# 시나리오 경로를 직접 지정 (필수)
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario C:\work\my-scenario.json

# 단계마다 '다음' 버튼을 누를 때까지 대기
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-full -Step

# 빌드 생략 (검증된 최신 바이너리로만 실행)
./tests/HeatingMold.VisualTest/Run.ps1 -Scenario e2e-full -NoBuild
```

`-HoldSeconds`(기본 3초, 화면 유지 시간)와 `-TimeoutSeconds`(기본 90초)는 시나리오
단계 수에 맞게 지정합니다. 대표 시나리오는 `-HoldSeconds 1 -TimeoutSeconds 300`으로
실행합니다.

> `-NoBuild`를 쓰면 방금 빌드한 `x64\Release\OccBridge.dll`을 앱 출력 폴더로 직접
> 복사한 뒤 실행하세요. `dotnet build`가 up-to-date로 판단하면 네이티브 DLL 복사가
> 생략되어 이전 DLL로 실행될 수 있습니다.

스크립트는 `-Scenario`가 없으면 빌드와 결과 폴더 생성 전에 오류로 중단합니다.
경로가 지정되면 네이티브 브리지와 앱을 빌드한 뒤 새 실행 디렉터리에서 앱을 시작합니다.
결과가 기록되면 요약을 출력하고, **앱은 육안 확인을 위해 열린 상태로 남깁니다.**
확인이 끝나면 창을 닫습니다. 잘못된 옵션이나 시나리오 경로면 앱 창을 띄우지 않고
종료 코드 2와 `result.json`(실패 사유)을 남깁니다.

## 화면 조작

테스트 중에는 사이드바와 3D 뷰포트 입력이 잠깁니다. 상단 진행 바의 버튼만 동작합니다.

- `일시정지/계속`: 현재 단계의 관찰 대기와 다음 단계 진행을 멈춥니다.
- `다음`: 단계 모드에서 다음 단계로 진행합니다.
- `중단`: 다음 작업을 시작하지 않고 현재 결과를 기록합니다.

완료·실패·중단 후에도 창과 마지막 결과 화면은 유지됩니다. 결과를 확인한 뒤 직접 닫습니다.

## 시나리오 스키마

- `schemaVersion: 2`는 `areas`(영역 정의)와 `steps`(행동 목록)를 사용합니다.
  실행 단계는 `loadModel`이 암묵적으로 먼저 실행되고, 이후 `steps` 순서대로 진행합니다.
- 다른 스키마 버전은 실행 전에 거부됩니다.
- 영역 기대값에는 발열선(`HeatingRun`)만 센 `heatingPieces`·`heatingLengthMm`를
  선택적으로 넣을 수 있습니다. 커넥터 포함 전체 길이가 같아도 제외영역이 실제 발열
  구간을 바꿨는지 확인할 때 씁니다.
- 전기 판정이 영역마다 다르면 `expectedWithinLimits`(모든 회로 동일 기대) 대신
  `circuitVerdicts`로 영역별 `OK`/`NG`를 지정합니다.
- 각 행동 전후로 모든 영역의 확정 경로 스냅샷을 비교합니다. 비대상 영역이나 읽기
  동작에서 경로가 달라지면 조각 좌표·길이·native ID·인출점 연결 수준에서 실패합니다.

사용 가능한 행동은 `docs/VisualTest.Scenarios.md`의 행동 목록을 참고하세요.
알 수 없는 행동, 중복 alias, 잘못된 참조는 실행 전에 거부됩니다.

## 결과 파일

`artifacts/visual-test/<실행ID>/`:

- `run.json`: 실행 시각, 시나리오, 모델 해시
- `steps.jsonl`: 단계별 시작/종료/소요/상태/데이터
- `result.json`: 최종 상태, 마지막 완료 단계, 오류, DXF 경로
- `result.dxf`: 실제 저장된 도면
- `occ_viewer_log.txt`: 네이티브 뷰어 로그

기존 실행 디렉터리에 `result.json`이나 `result.dxf`가 있으면 덮어쓰지 않고 시작을
거부합니다. `result.json`은 임시 파일을 거쳐 원자적으로 게시됩니다.

## 모델과 판정

시나리오의 `model`은 저장소 기준 상대 경로, SHA-256, 면 수, 솔리드 수를 선언합니다.
모델 해시, 면 수, 후보 지문이 맞지 않거나 후보가 모호하면 다른 모델이나 다른 후보로
자동 대체하지 않고 실패합니다.

자동 완료는 화면의 육안 합격을 의미하지 않습니다. 결과의 `automation`과
`visualReview`는 분리되어 있으며, 육안 검토는 기록하지 않습니다(`NotRecorded`).

## 전제와 한계

- 이 테스트는 기능의 전체 작업 순서와 상태 전이를 검증합니다. 실제 마우스 hit-test,
  드래그 감도, 저장 파일 대화상자 조작은 검증하지 않습니다.
- DXF는 확정 작업평면 기준 평면도 한 장(금형 윤곽·숨은선·치수), 검토 범위의
  발열경로, 전기 사양표를 포함합니다. 도면 내용의 육안 검토는 별도 CAD에서 합니다.
- 앱이 실행 중이면 빌드가 DLL 잠금으로 실패할 수 있습니다. 기존 프로세스를 강제
  종료하지 않고 빌드 오류로 알립니다.
