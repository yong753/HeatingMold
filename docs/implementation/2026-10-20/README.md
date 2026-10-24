# 모듈별 상세 구현계획

> 용어 기준: [TERMINOLOGY.md](TERMINOLOGY.md).


작성일: 2026-10-20  
기준: [상위 구현계획](../../Implementation.Plan.2026-10-20.md)  
상태: 전체 목표와 인계 기준을 설명하는 설계 문서. 일부 프로젝트·계약·테스트 이전을 시작했으며, 현재 진행 상태와 검증 결과는 [실행 계획](08.Refactoring.Execution.Plan.md)에 기록한다.

## 1. 문서 사용 순서

담당자는 상위 계획과 [공통 계약](00.Shared.Contracts.md)을 먼저 읽고 자신의 모듈 문서를 따른다. 공통 계약은 아래 모듈들이 서로 다른 단위·ID·깊이·revision을 구현하지 않도록 정한 구현 기준이다. 상위 계획의 사용자 합의가 우선하며, 수치 미정 사항을 납품 기준으로 임의 확정하지 않는다.

| 문서 | 담당 범위 | 먼저 확보할 산출물 |
|---|---|---|
| [01. Core](01.HeatingMold.Core.md) | 영속 설계 모델, 식별자, 단위, 기하 질의 계약, 오류 | CORE-01~04 |
| [02. Application](02.HeatingMold.Application.md) | 세션, 유스케이스, 후보 확정, 변경 영향, 저장·출력 조합 | Core 계약, APPFLOW-01 |
| [03. Design](03.HeatingMold.Design.md) | 영역, 제약 생성, 편집, 회로, 연결, 간격·면적 분할 | Core 계약, GEO-00 실험 결과 |
| [04. Electrical](04.HeatingMold.Electrical.md) | 저항·전류·전력, 회로별 판정, 전기 조건 만족 적용면적 집계 | Core의 길이·면적·전원 계약 |
| [05. Infrastructure](05.HeatingMold.Infrastructure.md) | OCCT 어댑터, ZIP/JSON, STEP/DXF, 사양 데이터 | Core/Application 포트, OccBridge API |
| [06. App](06.HeatingMold.App.md) | WPF, 입력 모드, 뷰어, 조립, 빌드·배포 | Application 유스케이스, Viewer API |
| [07. OccBridge](07.OccBridge.md) | 문서 소유 네이티브 상태, 기하·삽입 깊이, AIS, STEP writer | Core와 일치하는 브리지 DTO 의미 |

2026-10-24 현재 소스와 실행 환경을 대조한 구현 진행과 검증 결과는 [레거시 기반 리팩토링 실행 계획](08.Refactoring.Execution.Plan.md)에 정리했다. 선택 Face 영역 확정, 프로젝트 저장·출력, 회로 topology·연결 경로·제어점 편집, 회전 다중선 내부 곡선 편집이 Application 후보 확정과 App preview에 연결됐다. 영역 투영, 회로 길이 표 집계, topology 연결 입력·여유·회전 목록·survivor 선택 상태, 무변경 판정의 Application 이전, Face 선택 이벤트와 입력 취소·주요 편집 상태도 전용 모듈로 분리했다. 작업 좌표계 세션·축 선택·안내 문구와 인덱스↔법선 변환은 `WorkingCoordinateSystemViewModel`로, 세션 변경 갱신 보류와 복구 진단은 `MainWindowSessionPresentationViewModel`로 옮겼다. XAML 양방향 바인딩·이벤트 처리기 연결을 검사하는 회귀도 추가했다. 직접 작성한 `src`·`tests` 코드의 XML `<summary>`는 한글로 유지하고, `scripts/Verify.ps1`이 매 검증 전에 자동 검사한다. 최신 App 199개와 Managed Release/x64 전체 631개(Core 41, Design 108, Electrical 22, Application 75, Infrastructure 186, App 199)는 한글 summary 3,995개 검사와 함께 `x64/Verification/20261024-134422-360fef23/run.json`에서 통과했다. Native clearance는 비주기 1차 spline과 비공선 단일 구간 2차 spline의 exact 거리 코드 및 회귀 입력까지 추가했지만 Native 실행은 C++/CLI용 `MSBuild.exe`가 없어 아직 검증되지 않았다(`x64/Verification/20261024-111252-6d2c8611/run.json`). 실제 `Mold.stp` GEO-00 계측, G4/G5, Native·실제 WPF 검증, 연속 B-rep 깊이 극값과 실제 Mold 면적 수렴은 미완료다. 잔여 범위와 실행 증거는 실행 계획에 기록한다.

`src`의 제품 프로젝트는 **6개 C# + OccBridge = 7개**로 맞춘다. 테스트·실험 실행 프로젝트는 제품 프로젝트 수에 포함하지 않는다. Regions/Routing/Editing 등은 별도 제품 프로젝트를 추가하지 않고 해당 프로젝트 내부 폴더로 둔다. Infrastructure의 Geometry/Persistence/Export도 같은 원칙이다.

## 2. 착수 전 상태

아래 표는 실행 계획을 작성한 기준점 `c4b760b`의 상태다. 이후 진행 현황과 미완료 검증은 [실행 계획](08.Refactoring.Execution.Plan.md)에서 갱신한다.

| 현재 파일 | 이전·확장 책임 |
|---|---|
| `src/HeatingMold.Core/Geometry/Vec2.cs`, `Vec3.cs`, `ProjectionFrame.cs` | Core 유지, 값 검증 보강 |
| `Core/Geometry/WorkingCoordinateSystem.cs` | 값/ID는 Core, 변경 세션은 Application, `ProjectedModelBoundary` 연산은 Design |
| `Core/Geometry/RegionOps.cs`, `HeatingPathGenerator.cs`, `SurfacePathMapper.cs` | Design으로 이동; 기존 테스트도 담당 기능에 맞춰 이동 |
| `Core/Model/PatternParameters.cs` | MVP `CenterPitchMm`/`BoundaryMarginMm` 이름을 유지하고 신규 `PlacementConstraints`로 확장 |
| `App/MainWindow.xaml.cs`, `MainWindow.WorkingCoordinateSystem.cs` | 세션·흐름은 Application, 계산은 Design/Electrical, 화면은 App |
| `App/OccViewport.cs`, `OcctEnv.cs` | App/Viewer 및 시작 시 런타임 구성 |
| `Export/DxfExporter.cs` | HLR 입력 계약을 Application 포트로 옮기고 DXF writer를 Infrastructure로 이전. 기존 Export 제품 프로젝트 제거 완료 |
| `OccBridge/OccBridgeApi.cpp`, `WorkingCoordinateSystemNative.inc` | OccBridge 내부 책임별 분리 |

회귀 프로젝트 경로 `WorkingSide.Regression`과 `PowerLead.Regression`은 기존 실행 harness 이름이라 유지한다. 이 이름은 제품 도메인 용어가 아니며 신규 코드에서는 `WorkingCoordinateSystem`/`ConnectionPath`를 사용한다. 상위 계획 9절의 72개 회귀 검사는 과거 기록이며 새 실행 결과로 기록하지 않는다.

## 3. 의존성 및 파일 소유권

| 프로젝트 | 직접 참조 | 외부 의존 |
|---|---|---|
| Core | 없음 | .NET 기본 라이브러리 |
| Design | Core | 기존 Clipper2 2.0.0 |
| Electrical | Core | .NET 기본 라이브러리 |
| Application | Core, Design, Electrical | .NET 기본 라이브러리 |
| Infrastructure | Core, Application, OccBridge | 기존 netDxf 2023.11.10, ZIP/JSON |
| App | Application, Infrastructure, Core; 조립 위치에서 Design/Electrical | WPF, Viewer에서만 OccBridge |
| OccBridge | C# 프로젝트 참조 없음 | C++/CLI net8.0, 기존 OCCT 7.9.3 |

Core/Design/Electrical/Application은 `net8.0`, App/Infrastructure는 Windows x64 경계에 맞게 `net8.0-windows`, OccBridge는 기존 `/clr:netcore`를 유지한다. 프레임워크·패키지 버전 업그레이드는 이 작업에 섞지 않는다.

공용 솔루션·빌드 속성·프로젝트 참조 수정은 App 담당의 UI-01에서 모아 반영한다. Core 담당은 공통 타입을, Application 담당은 저장·출력·후보 수명 포트를, OccBridge 담당은 관리형 브리지 DTO를 소유한다. 소비 모듈에서 같은 의미의 타입을 재정의하지 않는다. 계약 변경 시 먼저 공통 문서와 제공 모듈의 테스트를 바꾸고 소비 코드를 함께 수정한다.

## 4. 구현 순서와 인계 지점

| 순서 | 작업 | 다음 단계 진입 증거 |
|---|---|---|
| G0 | OCC-01/UI-01로 실행 환경·격리 빌드 확보; 기존 3개 검증군 실행 | 빌드 경로, 실행 명령, 성공/실패 로그, 실패가 환경 문제인지 기능 문제인지 기록 |
| GEO-00 | OCC-02/04와 DES-02/03 담당이 작은 기하 실험 수행 | 실제 `resource/Mold.stp`의 선택 Face/작업축/앵커 재현 정보, 평판·경사·곡면 삽입 깊이, 같은 측면 양 끝점 가능/실패 사례 |
| G1 | CORE-01~04 → APPFLOW-01/02 → UI-01, DES-01, INF-01, OCC-03 | 계약 컴파일, 새 프로젝트 의존성 검사, 기존 동작 회귀 통과 |
| G2 | DES-02/03 + OCC-04/05 + INF-02 | 통합 영역·가림·실제 깊이 후보와 정확한 3D 길이 검증 |
| G3 | DES-04/05 + APPFLOW-03 + UI-03 | 지정 방향·양 끝점·단일 경로 또는 구체적 생성 실패 |
| G4 | DES-06/07/08 + APPFLOW-04 + UI-04 | 편집·절단·통합·연결 실패 시 전체 이전 상태 보존 |
| G5 | DES-09/10 + ELEC-01~04 + UI-05 | 최소 간격 거부, 공백 수치, 실제 회로별 적용면적, 회로별 전기 판정 |
| G6 | INF-03~07 + OCC-07 + APPFLOW-05/06 + UI-06 | ZIP 이동·재열기, STEP/DXF 재읽기, CAD 편집 확인 |
| G7 | UI-07 + 각 모듈 README·검증 기록 | 개발 도구 없는 Windows에서 설치·실행·최종 시연 |

Electrical은 Core 계약 확정 후 기하 구현과 독립적으로 숫자 fixture로 개발할 수 있다. Persistence는 초기 모델부터 직렬화 샘플을 작성하되, 수동 편집·곡선 복원을 포함한 통합 완료는 G6에서 판정한다. UI 세부 구현 전에 GEO-00 결과를 확보한다. 고정 납기 추정은 이 실험 이후에 한다.

표의 선행은 필요한 계약/산출물의 인계를 뜻하며 상대 모듈의 전체 구현 완료를 기다리라는 뜻이 아니다. UI-01은 프로젝트 skeleton 연결(01a)과 구현 빌드/격리 회귀(01b)를 분리한다. Application은 포트/fake를 먼저, Infrastructure는 그 포트 구현을 나중에 제공하고 저장/출력 유스케이스 통합은 같은 검증 지점에서 마친다. 임시 stub은 실제 기능 성공으로 반환하지 않고 미구현 상태를 명시한다.

상위 계획과의 대응은 다음과 같다.

| 상위 계획 | 주 구현 작업 | 통합 작업 |
|---|---|---|
| 6.1 적용면/영역 | DES-02, OCC-03 | INF-02, APPFLOW-03, UI-03 |
| 6.2 시작/종료·방향·생성 | DES-04/05, OCC-02/04 | APPFLOW-03, UI-03 |
| 6.3 3D 매핑/삽입 깊이 | OCC-04/05, DES-03 | INF-02, GEO-00 |
| 6.4 꼭지점·최소간격·공백 | DES-06/09, OCC-05 | APPFLOW-04, UI-04/05 |
| 6.5 회로·연결·전원 | DES-07/08, OCC-06 | CORE-03, APPFLOW-04, UI-04 |
| 6.6 전기/회로별 적용면적 | ELEC-01~04, DES-10, OCC-05 | APPFLOW-04, UI-05 |
| 6.7 프로젝트 저장/재설계 | INF-03/04, CORE-02/03 | APPFLOW-05, UI-06 |
| 6.8 STEP/DXF/사양표 | INF-05~07, OCC-07 | APPFLOW-06, UI-06 |
| 4·5·7절 모듈/데이터/변경 영향 | CORE-01~06, APPFLOW-01/02 | UI-01/02, INF-01, DES-01 |
| 8·9절 검증/납품 | 각 모듈 테스트, OCC-01/02, UI-01/07 | 전체 완료 시나리오 |

## 5. 검증 운영

기존 명령은 저장소 루트 PowerShell에서 실행한다.

```powershell
dotnet test tests/HeatingMold.Core.Tests/HeatingMold.Core.Tests.csproj -c Release
& ./tests/WorkingSide.Regression/Run.ps1
& ./tests/PowerLead.Regression/Run.ps1 # 기존 harness 경로명은 유지
```

현재 네이티브 회귀 러너는 먼저 `x64/Release`를 갱신하고 App을 별도 출력에 빌드한다. 실행 중 DLL 잠금으로 실패한 전력이 있다. UI-01/OCC-01에서 `scripts/Verify.ps1`을 추가하여 `x64/Verification/<run-id>/` 아래 네이티브·앱·회귀 실행물을 일관되게 배치한다. 기존 앱 프로세스를 종료하지 않는다. 새 러너 완성 전에는 기존 러너의 한계를 실행 기록에 남긴다.

새 검증군은 `HeatingMold.Design.Tests`, `HeatingMold.Electrical.Tests`, `HeatingMold.Application.Tests`, `HeatingMold.Infrastructure.Tests`, WPF 소스를 직접 컴파일하는 `HeatingMold.App.Tests`, 그리고 기존 네이티브 회귀군으로 나눈다. 순수 모듈 테스트는 OCCT가 없는 환경에서 실행 가능해야 한다. App 테스트는 `MainWindow` partial의 트랜잭션 회귀를 테스트 대역으로 확인하며 Native 실행을 대신하지 않는다. 파일 이동 시 기존 테스트를 삭제해 회귀 기준을 잃지 않는다. 자동검사 결과와 실제 Viewer/DPI·CAD/설치 수동검사를 구분한다. `scripts/Verify.ps1 -Group Managed`는 6개 managed/App 테스트 프로젝트를 순서대로 실행한다.

모든 작업 인계에는 변경 파일, 구현한 작업 ID, 계약 버전, 재현 입력, 실행 명령/결과, 남은 제한을 기록한다. 미구현 테스트 명령·미실행 검사를 통과로 보고하지 않는다.

## 6. 전체 완료 시나리오

1. STEP를 열고 작업축을 확정한다. 인접 A→B→C Face를 선택하여 내부 공유 경계 없는 발열영역을 만든다.
2. Case 1/2, 최소 외곽 간격, 양의 삽입 깊이, 공통 측면·양 끝점·가로/세로를 지정한다. 카메라를 회전해도 생성 조건은 유지된다.
3. 단일 연속 경로를 생성하고 꼭지점을 이동한다. 최소 간격 미달·구멍/제외 관통·가림 후보는 이전 경로·연결·계산을 보존하며 거부한다.
4. 회로를 절단·통합하고 회로별 두 끝점의 동일 출구 측면을 검사한다. 발열사 연장부와 별도 연결선을 구분하고 `연결부 위치`까지 `연결 경로`를 설정한다.
5. 회로별 길이·전기값·회로별 적용면적·간격 편차를 확인한다. 회로별 16A를 합산 한도로 오해하지 않는다.
6. 수동 편집이 있는 `.hmproj`를 저장하고 원본 외부 경로가 없는 다른 폴더로 한 파일만 이동해 재열기한다. ID·좌표·회로·길이·편집 결과를 비교한다.
7. 같은 확정 revision으로 STEP/DXF를 출력한다. STEP 재읽기 길이·좌표, DXF 경로·연결부 위치·사양표 TEXT, 일반 CAD 편집을 확인한다.
8. 개발 도구 없는 Windows에서 동일 과정을 수행한다. Undo/Redo·자동 장애물 우회·열해석·전개도·자동 회로 최적화가 제품에 추가되지 않았음을 확인한다.
