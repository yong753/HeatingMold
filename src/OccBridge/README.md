# HeatingMold.OccBridge

OccBridge는 WPF 화면과 OCCT 사이의 C++/CLI 경계입니다. OCCT 모델·topology·Native 곡선과 AIS 표시를 소유하고, 관리형 호출자에게는 평탄한 DTO와 상태형 결과를 반환합니다. Core 도메인 타입을 C++에서 참조하지 않습니다.

## 책임과 소유권

`NativeDocumentSession`은 활성 STEP 모델, Face 지도와 인접 캐시, 모델 revision, 작업 좌표계 질의 context, 경로 staging·확정 wire를 소유합니다. `NativeDocumentPresentation`은 해당 문서의 모델·경로·연결부·제외 영역·미리보기 AIS 객체를 소유합니다. Bridge의 정적 API는 현재 단일 활성 문서에 위임합니다. 이 구조는 다중 문서나 병렬 OCCT 작업을 의미하지 않습니다.

모델 교체는 `PrepareStepCandidate`에서 후보를 만들고 `PublishStepCandidate`로 확정하거나 `DiscardStepCandidate`로 폐기합니다. 준비 후보 질의는 후보 ID와 기준 revision을 사용하고, 활성 문서 질의는 작업 context ID와 model revision을 사용합니다. 오래된 context·revision, 잘못된 입력, 예산 초과, 수치 판정 미확정은 성공 결과로 바꾸지 않습니다.

## API 영역

- **모델과 topology:** STEP 후보 준비·확정·폐기, Face 특징/경계 표본, 공유 Edge 인접, 선택 영역 topology.
- **작업 좌표계:** first-hit, 전체 모델 가시성, 표면 요소와 실제 면적 측정. Face 순번은 현재 문서 안에서만 쓰며 저장 ID로 사용하지 않습니다.
- **경로 geometry:** 활성·후보 곡선 측정, 깊이 envelope와 삽입 깊이 보정, 최소 간격, 연결 경로의 금형 교차 검사.
- **staging과 표시:** 곡선 wire를 후보에 staging하고 측정 결과를 확인한 뒤 commit/publish합니다. 미리보기와 AIS 수명은 확정 설계 상태와 분리합니다.
- **출력과 Viewer:** STEP 설계 형상 쓰기, HLR 투영, Face 선택·호버, 회로 강조와 뷰포트 조작.

각 질의 결과의 상태와 진단은 `OccBridgeApi.h`의 DTO 계약을 따릅니다. 배열 좌표는 XYZ 순서의 mm 값이고, Native Face 번호와 staging 번호는 영속 식별자가 아닙니다. 관리형 Infrastructure 어댑터가 이를 Core ID·단위·오류 계약으로 변환합니다.

## Native 소스 구성

`WorkingCoordinateSystemNative.inc`는 아래 구현 파일을 원래 선언 순서대로 포함하는 집계 파일입니다.

- `NativeWorkingSurface.inc`: 작업면 context, first-hit과 깊이 측정
- `NativeRegionTopology.inc`: Face 인접·선택 topology와 표면 요소
- `NativeHeatingPathGeometry.inc`: 곡선 생성과 측정 입력
- `NativeConnectionPathGeometry.inc`: 연결 경로의 금형 충돌 질의
- `NativeClearanceQueries.inc`: Native 곡선 최소 간격
- `NativeHeatingPathPreparation.inc`: 곡선 표본, 후보 경로 준비와 확정

이 파일들은 현재 `OccBridgeApi.cpp`의 같은 translation unit에 포함되므로 전역 상태와 내부 helper의 연결 방식, DLL ABI는 유지됩니다. 독립 translation unit으로 분리하는 작업은 실제 C++/CLI 빌드와 회귀를 확인할 수 있는 환경에서 이어갑니다.

## Managed API 소스 구성

`OccBridgeApi.cpp`는 managed Bridge 구현을 아래 파일 순서로 포함합니다.

- `OccBridgeManagedSupport.inc`: DTO 변환과 공용 내부 helper
- `OccBridgeWorkingSurfaceApi.inc`: 작업면 context와 표면 질의 API
- `OccBridgeCurveAnalysisApi.inc`: 곡선 간격·깊이·충돌 분석 API
- `OccBridgeTopologyAreaApi.inc`: Face topology와 적용면적 API
- `OccBridgePathStagingApi.inc`: 경로 후보 staging·확정 API
- `OccBridgeViewerAndModelApi.inc`: 모델 표시, 선택, 투영과 Viewer API

이 파일들도 기존 선언·정의 순서대로 같은 translation unit에 들어갑니다. 이번 분리는 읽기와 책임 추적을 쉽게 하기 위한 것이며, 독립 컴파일 단위나 ABI 변경을 뜻하지 않습니다.

## 빌드와 검증

프로젝트는 Visual Studio v143 C++/CLI, `net8.0`, `Release|x64`, OCCT 7.9.3 사전 빌드를 사용합니다. `scripts/Verify.ps1`이 Native 빌드와 `WorkingSide.Regression`을 같은 격리 실행 디렉터리에서 수행합니다.

```powershell
& ./scripts/Verify.ps1 -Configuration Release -Platform x64 -Group Native
```

Visual Studio 경로나 OCCT 배포 위치가 자동 검색되지 않는 환경에서는 `-MSBuildPath`, `-VcVars64Path`, `-OcctRoot`, `-ThirdPartyRoot`를 지정합니다. 결과와 로그는 `x64/Verification/<run-id>/`에 남습니다. Managed/App 대역 테스트는 C++/CLI 구현이나 실제 OCCT 실행을 대신하지 않습니다.

현재 작업 환경에서는 C++/CLI를 지원하는 `MSBuild.exe`와 OCCT 배포 디렉터리를 찾지 못했습니다. 따라서 OccBridge 변경 소스와 `WorkingSide.Regression`은 아직 컴파일·실행되지 않았고, Native 완료 판정에는 위 명령의 실제 성공과 Mold fixture 검증이 필요합니다.

세부 구현 및 완료 조건은 [OccBridge 구현 계획](../../docs/implementation/2026-10-20/07.OccBridge.md)을 따릅니다.
