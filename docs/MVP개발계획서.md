# MVP 개발계획서 — 구현 가능성 검증

| 항목 | 내용 |
|---|---|
| 문서 버전 | v1.0 (2026-10-08) |
| 모문서 | `docs/개발계획서.md` v1.0 |
| 목적 | 전체 파이프라인(STEP 로드 → 사행 패턴 생성 → 3D 표시 → 2D DXF)의 **구현 가능성만** 최소 비용으로 검증 |
| 기간 | 총 4~7일 (S1·S2 병행 시 4~5일) |
| 산출물 | MVP 솔루션(본 개발 구조와 동일) · xUnit 테스트 · 샘플 DXF · 판정 보고서 |

> 이 문서는 본 개발(M0~M5) 착수 **전** 단계로, 기능 완성이 아니라 GO/NO-GO 판정이 목표다.
> MVP 코드는 폐기물이 아니라 본 개발 M1~M4의 초기 코드로 그대로 이관한다.

---

## 1. 사전 조사 결과 (계획 수립 근거)

### 1.1 개발 환경 점검 (직접 확인)

| 항목 | 확인 결과 | 판정 |
|---|---|---|
| VS2022 | Community 설치됨 | ⚠️ C++ 워크로드 **미설치** |
| MSVC 도구체인 | `VC\Tools\MSVC`, `vcvars64.bat` 없음 | ❌ 설치 필요 |
| CMake | 독립 설치·VS 번들 모두 없음 | ❌ C++ 워크로드로 해결 |
| Windows SDK | `Windows Kits\10` 있음 | ✅ |
| .NET SDK | 6.0.428 / **8.0.127** | ✅ (.NET 8 LTS 사용 가능) |
| VS Installer | 있음 → 워크로드 추가 가능 | ✅ (관리자 권한 필요) |
| OCCT 소스/바이너리 | 전 디스크 미발견 | 다운로드 필요 |
| 작업공간 | 문서 1건 + `resource\Mold.stp`만 존재, Git 아님 | MVP 시작 시 저장소 초기화 권장 |

> **S0 실행 결과 (2026-10-08 재점검):** C++ 워크로드 설치 완료 — MSVC 14.44.35207 · VS 번들 CMake 3.31.6 · Windows SDK 10.0.26100.0, `cl.exe` 컴파일·링크·실행 스모크 통과. OCCT 7.9.3 prebuilt(vc14-64)+3rdparty를 `third_party/`에 배치(저장소 제외, `env.bat vc143 64` 경로 검증 완료). 저장소 초기 커밋 `60dca06`.
> - 정정 1: Windows SDK는 "폴더 존재 ✅"가 아니라 Include/Lib 부재 상태였음 → 워크로드 설치로 해결.
> - 정정 2: STEP 모듈명은 7.9부터 `TKDESTEP`(구 TKSTEP 아님) — S1 브리지 링크 시 주의.
> - OCCT 경로: inc `third_party/opencascade-7.9.3-vc14-64/inc` · lib `…/win64/vc14/lib` · bin `…/win64/vc14/bin` · 3rdparty `third_party/3rdparty-vc14-64`.
> - 위 표는 최초 점검 기록으로 유지.

### 1.2 Mold.stp 실물 확인

- NX 12.0에서 ST-Developer v16.7로 내보낸 파일 — 헤더 검증 완료(`SIEMENS PLM Software NX 12.0` · `ST-DEVELOPER v16.7`, 2024-03-14 생성, 원본명 `DT24-020 FRONT INTERIOR PANEL_LSE24_240202.stp`)
- 스키마: `AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 1 }`(AP214 계열) — **모문서의 "AP242" 기재와 불일치** (기능 영향은 낮으나 문서 정정 필요)
- 단위 mm(`SI_UNIT(.MILLI.,.METRE.)`) · 단일 PRODUCT, 어셈블리 구조 없음(관계 엔티티 0건)

> **실물 계량 분석 (2026-10-08, 37,472 엔티티):** E4(솔리드 부재) 위험은 파일 수준에서 해소 — `MANIFOLD_SOLID_BREP` 30개(CLOSED_SHELL 30개, 574면) 존재.
> - 전체 `ADVANCED_FACE` 664개 = 솔리드 574 + 보조 서피스(OPEN_SHELL 2개) 90. 면 타입: **평면 317** · 원통 208 · 원뿔 54 · B스플라인 59 · 원환 8 · 구 7 · 압출 11 → MVP 주경로(평면)에 충분
> - 보조 표현 동반: `GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION`(와이어), `MANIFOLD_SURFACE_SHAPE_REPRESENTATION`(서피스), `CONSTRUCTIVE_GEOMETRY_REPRESENTATION`(보조 지오메트리)
> - **S1 확인 항목:** 메인 셰이프(`SHAPE_REPRESENTATION` #36)는 AXIS2_PLACEMENT뿐인 사실상 빈 표현이고, 솔리드 30개는 `SHAPE_REPRESENTATION_RELATIONSHIP`으로 연결된 `ADVANCED_BREP_SHAPE_REPRESENTATION`(#25)에 있음 → V1에서 OCCT가 관계 표현까지 전개해 로드하는지 확인 필수 — **2.1 사전 스모크에서 확인 완료**(STEPControl_Reader가 전개·로드, 솔리드 30개)

### 1.3 외부 기술 검증 (공식 자료 확인 + 실측)

| 항목 | 검증 결과 |
|---|---|
| C++/CLI + .NET 8 (R1) | MS 공식 문서에서 `<CLRSupport>NetCore</CLRSupport>` + `<TargetFramework>net8.0</TargetFramework>` 정식 지원 확인. 제약: **DLL만 빌드 가능**(WPF 앱이 exe이므로 문제 없음), Windows 전용, vcxproj 비-SDK 형식, `-clr:netcore`만 지원, .NET 7+는 기본 AssemblyLoadContext 로드(이중 로드 문제 없음) → **실컴파일·로드 검증 완료** |
| OCCT 릴리스 | 최신 8.0.1(2026-07-30, C++17/ABI 기준은 8.0.0에서 확립 — 7.9.0 대비 500+ 변경) / 7.9.3(2025-12-06, 7.9 계열 최종 — 7.9.4 없음) → **7.9.3 고정 권장** (샘플·문서 호환성) |
| OCCT 바이너리 | prebuilt vc14-64 + 3rdparty(zip) 제공. vc14 = VS2015 ABI로 v143과 호환 → **TKernel 링크·실행 실측 통과** |

> **1.3 검증 실행 결과 (2026-10-08, GitHub Releases API·MS Learn·로컬 스모크):**
> - C++/CLI net8.0: MSVC v143(14.44)으로 `/clr:netcore` DLL 빌드 성공 → C# net8 콘솔에서 로드·호출 성공(`clr-net8-load-ok`). **앱 프로젝트에 `<UseIJWHost>true</UseIJWHost>` 필수** — 미설정 시 `ijwhost.dll`이 출력에 복사되지 않아 `FileNotFoundException`("지정된 모듈을 찾을 수 없음") 발생, 설정 시 자동 복사 확인.
> - OCCT prebuilt: v143 툴셋으로 `TKernel.lib` 링크 + 실행 성공(`occt-link-ok 7.9.3`) → E3 실측 해소 방향.
> - 참고: vswhere/설치 상태에는 별도 "C++/CLI 지원" 구성 요소가 표시되지 않으나 빌드 성공(17.14 기본 포함으로 판단).

### 1.4 사전 판정

7개 핵심 체인 모두 표준 기술 조합으로 **구현 가능성 높음**. S0(도구체인·OCCT 배치)와 1.3 실측(C++/CLI 빌드·로드, prebuilt 링크)으로 초기 진입 장벽은 해소 — 남은 판정 대상은 S1 통합 스파이크(관계 표현 로드, HwndHost 뷰어, HLR)뿐이다.

---

## 2. MVP 정의

### 2.1 검증 목표 (GO/NO-GO 판정 대상)

| # | 검증 체인 | 실패 시 프로젝트 영향 | 판정 단계 (근거) |
|---|---|---|---|
| V1 | Mold.stp B-rep 로드·3D 표시 | 치명 | S1 검증 1·2 |
| V2 | 면 선택(픽킹 또는 인덱스) → faceId 식별 | 치명 | S1 검증 2·3 |
| V3 | UV 경계(외곽+홀) 폴리곤 추출 → C# 전달 | 치명 | S1 검증 3 |
| V4 | Clipper2 마진/제외영역 차집합 + 사행 레인 생성 | 치명 | S2 T1~T5 |
| V5 | UV→3D 매핑 + 3D 와이어/리본 표시 | 치명 | S1 검증 4 + S3 |
| V6 | HLR 투영 → 2D 선분 | 심각 (대체 수단 있음) | S1 검증 5 + S3 |
| V7 | DXF 출력 → AutoCAD 열람 | 치명 | S3 GO (AutoCAD 검수) |

> **2.1 사전 스모크 (2026-10-08, 네이티브 C++·temp):** `STEPControl_Reader`로 Mold.stp 로드 — 솔리드 30 · 셸 32 · 면 664 · 평면 317 (1.2 계량과 일치, **관계 표현 전개 확인**). 첫 평면에서 `BRepTools::UVBounds` + 와이어/엣지 순회·샘플링 동작 → **V1·V3의 네이티브 메커니즘 사전 확인** (S1은 C++/CLI·WPF 경유만 검증하면 됨).
> - 런타임 교훈: `env.bat`의 3rdparty PATH는 상대 경로(`..\3rdparty-vc14-64\...`)라 CASROOT 밖 CWD에서 DLL 로드 실패(0xC0000135) → S1 앱/테스트는 DLL 경로를 절대 경로로 설정해야 함.

### 2.2 의도적 제외 (타당성 판정에 불필요 — 본 개발로 이관)

- 제외영역 **스케치 UI** → 실제 면의 **홀 폴리곤**을 제외영역으로 사용 (V3·V4 검증 경로 동일, UI 비용 절감)
- JSON 저장/불러오기(FR-10), 설치 패키지(Inno Setup/MSIX), LGPL 고지문 패키징
- 전기계산(FR-04 원칙), 홀 자동 회피(v2), 곡면 전개도, UV 왜곡 품질 최적화(곡면 1면에서 **관찰만**)
- AIS 픽킹은 우선순위 낮춤 → MVP 주경로는 **평면 면 목록 콤보 선택**. 픽킹 실패가 프로젝트를 죽이지 않음(V2의 본질은 면 식별)

### 2.3 원칙

1. **구조 동일성**: 모문서의 솔루션 구조를 그대로 사용 (MVP 코드 = 본 개발 초기 코드)
2. **전환 대비**: 브리지 native API는 처음부터 `extern "C"` 스타일로 작성 → C++/CLI 실패 시 C ABI + P/Invoke 전환 비용 최소화
3. **조기 판정**: 치명 위험(R1, E4)은 S1에서 최우선 확인

---

## 3. 단계별 실행 계획

```
S0 환경준비(0.5일) → S1 브리지·뷰어 스파이크(2일) → S2 순수C# 파이프라인(2일)
                   → S3 통합 데모+DXF(1.5일) → S4 판정 문서화(0.5일)
S1과 S2는 상호 독립 → 병행 가능 (총 4~5일)
```

### S0 — 환경 준비 (0.5일)

| 작업 | 내용 |
|---|---|
| 워크로드 설치 | VS Installer → **"C++를 사용한 데스크톱 개발"** (MSVC v143 + Windows 10/11 SDK + CMake Windows 도구). 관리자 권한·디스크 약 10GB |
| OCCT 확보 | GitHub `Open-Cascade-SAS/OCCT` V7_9_3 릴리스에서 prebuilt vc14-64 바이너리 + 3rdparty(zip) 다운로드 |
| NuGet | Clipper2, netDxf, CommunityToolkit.Mvvm, xUnit |
| GO 기준 | `cl.exe` 컴파일 동작, VS 번들 `cmake --version` 동작, OCCT 헤더/lib 경로 정리 |

### S1 — 브리지·뷰어 스파이크 (2일, 최대 위험 구간)

**산출물 (솔루션 구조 — 본 개발과 동일)**

```
HeatingMold.Mvp.sln
├─ src/OccBridge/           C++/CLI DLL (/clr:netcore, net8.0) — native/ + managed/
├─ src/HeatingMold.Core/    (S2에서 구현)
├─ src/HeatingMold.App/     WPF net8.0-windows — HwndHost 뷰어
├─ src/HeatingMold.Export/  (S3에서 구현)
└─ tests/HeatingMold.Core.Tests/
```

**브리지 최소 API (7개)**

| 함수 | 용도 |
|---|---|
| `LoadStep(path)` → handle | STEP 로드 |
| `GetShapeInfo(handle)` | 솔리드 수·면 수·면 타입(평면/곡면) — AP214 혼합 표현 조기 확인 |
| `GetFaceUvBoundary(faceId)` | 외곽+홀 폴리곤 (double[] 벌크) |
| `SampleUvTo3D(faceId, u, v)` | UV→3D 매핑 |
| `BuildWire3D(faceId, uvPath[])` | 표시용 3D 와이어/리본 |
| `InitViewer(hwnd)` / `FitView` / `SetColor` | AIS 뷰어 (HwndHost 임베드) |
| `HlrProject(projection, wireHandles)` | HLR → 2D 폴리라인 배열 (스모크) |

**검증 (GO 기준 — 전부 통과 시 GO)**

1. WPF 앱 실행, Mold.stp 로드·줌·회전 (HwndHost 임베드)
2. `GetShapeInfo`로 실제 솔리드/면 구성 파악 — 평면 면 최소 1개 확인
3. 평면 면 UV 경계 폴리곤이 기하학적 예상과 일치 (치수 대조)
4. `SampleUvTo3D` 반환점이 실제 면 위에 위치 (편차 확인)
5. 단순 형상으로 `HlrProject` 스모크 — 윤곽선 분리 확인
6. C++/CLI net8.0 DLL이 WPF 앱에서 로드·호출 (R1 판정) — 앱 csproj에 `<UseIJWHost>true</UseIJWHost>` 필요(1.3 확인)

> **S1 진행 기록 (2026-10-08):** OccBridge C++/CLI(/clr:netcore net8.0) 생성 — `HeatingMold.Mvp.sln` + `src/OccBridge`. 최소 API 구현: `LoadStep`·`GetShapeInfo`·`GetFaceUvBoundary`·`SampleUvTo3D`(+`GetFaceType` 보조). C# 스모크(브리지 경유) 결과: **솔리드 30 · 셸 32 · 면 664 · 평면 317**(2.1 사전 스모크와 일치) · 첫 평면 UV 경계 26점 `(-3,0)×(-20,20)` · `SampleUvTo3D` 평면 이탈 편차 0 → **검증 항목 2·3·4 통과(브리지 경로)**.
> - 빌드 교훈: ① OCCT 타입을 ref 클래스 메서드에서 직접 사용하면 C2711(C3821/C3645 연쇄) → OCCT 호출을 native 헬퍼(`#pragma managed(off)`)로 격리, managed 메서드는 마샬링만. ② 헤더 전역 `using namespace System;`은 Windows SDK COM(`IServiceProvider`)과 충돌(C3699). ③ C++/CLI 배열은 `System::array<T>`가 아니라 키워드 `array<T>^`.
> - **뷰어·HLR 확장 (같은 날):** 브리지에 뷰어 API(`InitViewer`·`ResizeView`·`FitView`·`RotateStart/Move/End`·`ZoomView`·`SetShapeColor`·`GetViewState`) + `HlrProject` 추가, WPF 앱 `src/HeatingMold.App`(HwndHost 임베드) 생성. 실측: 앱에서 Mold.stp 표시(로드 690ms) → **드래그 회전·휠 줌·색상 전환·HLR 버튼 모두 동작 — 검증 항목 1·5·6 통과 → S1 6항목 전부 통과**. HLR(30솔리드 전체): 가시 520 · 은선 1650 · **1.9~2.1초**(E6 양호). GL 컨텍스트 진단: `ctxValid=true, gl>=2.1=true, vendor=nvidia corporation`.
> - 뷰어 교훈: HwndHost 자식 창을 `"static"` 클래스로 만들면 **CS_PARENTDC** 때문에 GL DC가 부모 DC와 공유되어 렌더링이 화면에 표시되지 않음 → 배경 브러시 없는 **커스텀 창 클래스**(`HeatingMoldOccViewport`)로 교체 + WM_PAINT/WM_ERASEBKGND 무효화 처리로 해결. 앱은 `HEATINGMOLD_OCC_LOG` 환경변수로 OCCT 메시지·GL 정보를 `x64\Release\occ_viewer_log.txt`에 수집.

**NO-GO 시 대비**

| 상황 | 대응 |
|---|---|
| `/clr:netcore` 실패 | TFM net6.0 하향 또는 C ABI + P/Invoke 전환 (native API는 `extern "C"` 유지로 즉시 가능) |
| prebuilt 링크 실패 | OCCT 7.9.3 소스 CMake 빌드 (필요 모듈만: TKernel~TKSTEP, TKV3d, TKHLR) |

### S2 — 순수 C# 파이프라인 (2일, 브리지 없이 독립 검증)

- `HeatingMold.Core`: `Geometry/Serpentine.cs`(세그먼트+연결규칙 분리), `Geometry/RegionOps.cs`(Clipper2), `Model/`(PatternParams·FacePlacement — 모문서 5장 데이터 모델 그대로)
- 가짜 `IUvMapper` 인터페이스로 브리지 없이 xUnit 테스트

**필수 테스트 케이스**

| # | 케이스 | 검증 내용 |
|---|---|---|
| T1 | 직사각형 + 마진 | 레인 수·길이·간격 일치 |
| T2 | 홀 포함 폴리곤 | 클리핑으로 레인 분절 (MVP 허용 사양) |
| T3 | 홀수 레인 U턴 반전 | 단일 연속 와이어 연결 |
| T4 | 방향축 회전 | UV 장축 자동 산출 |
| T5 | 총 길이 sanity | 예상 범위 내 |

**GO**: 전체 테스트 통과. OCCT와 무관하므로 실패 위험 낮음 — 일정 버퍼로 활용.

> **S2 진행 기록 (2026-10-09):** `src/HeatingMold.Core`(net8.0, Clipper2 2.0.0) 구현 — `Geometry/Vec2.cs`·`RegionOps.cs`(마진·홀 차집합)·`Serpentine.cs`(스캔라인 레인 분절 + U턴 호 연결 + 장축 자동), `Model/PatternParams`·`FacePlacement`, `IUvMapper`. 테스트 `tests/HeatingMold.Core.Tests`(xUnit): **T1~T5 전부 통과 (5/5, 268ms)** — T1 레인 5·길이 90·간격 10 / T2 홀 분절(6세그먼트) / T3 홀수 레인 방향 교대·단일 연속 / T4 장축 자동(0°/90°)·회전 불변 / T5 총 길이 = 레인합 + U턴(4×π×5) ±2%. 빌드: dotnet SDK 9.0.315(net8.0 타깃).
> - 구현 노트: 마진 = Clipper2 Miter 오프셋(외곽 −margin, 홀 +margin), 레인 분절 = even-odd 스캔라인(반개구간 규칙), U턴 = 반원 12분할 근사, 홀 분절 구간 사이는 직선 연결(2.2 제외 항목 '홀 자동회피'에 따름).

### S3 — 통합 데모 (1.5일)

```
픽킹/콤보 면 선택 → GetFaceUvBoundary → (마진·홀 차집합) → 사행 생성(UV)
→ UV→3D 샘플링 → BuildWire3D(리본 두께) → AIS 오버레이 표시
→ 평면 뷰: 직교 투영(단순 선형 변환, HLR 불요) / 측면 뷰: HLR
→ netDxf: MOLD_OUTLINE / HEAT_WIRE / DIMENSION 3레이어 + 치수 → DXF
```

- 간격·마진·선폭 입력용 최소 파라미터 입력란 (텍스트박스 3개 수준)
- 곡면 면 1개 시도 → UV 왜곡 **관찰 기록** (개선은 본 개발)
- **GO**: 평면 면에서 전 체인 성공 + 산출 DXF를 AutoCAD에서 열어 레이어·치수·윤곽 검수 통과

> **S3 진행 기록 (2026-10-09):** 전 체인 성공 — 앱에서 최대 평면 **#626(500×981mm)** → 경계 추출 → 마진·홀 차집합 → 사행(**48레인·30,421.5mm·84ms**) → 리본 스트로크 → `BuildWire3D` 오버레이(적색)·정면 뷰 표시. DXF `output/heat_pattern_face626.dxf` 생성 — **netDxf 재로드 검증 통과**(MOLD_OUTLINE/HEAT_WIRE/DIMENSION 3레이어 + 치수 2개). AutoCAD 검수는 사용자 확인 대기.
> - 곡면 관찰: 원통면 #4 — **u=0.058rad(각도)·v=127.7mm, 단위 불일치로 UV 사행 불가**(NO SEGMENTS). 평면 #626 UV↔3D 길이 비율 1.0000(등방) → UV 패턴 성립. 곡면 스케일 보정은 본 개발 이관(E5).
> - 구현 노트: 브리지 +`BuildWire3D`/`ClearOverlay`/`SetViewDirection`/`GetFaceNormal`; Core +`RegionOps.StrokePath`(open-path offset, EndType.Butt); `HeatingMold.Export` 신설 — netDxf **2023.11.10**(2.4.1 미배포, 3.x API `Polyline2D`); 면 콤보 = UV 크기순 정렬(큰 면 우선).

### S4 — 판정 문서화 (0.5일)

V1~V7 각각 GO/NO-GO + 근거, 로드·HLR 소요시간, 곡면 관찰 결과, 잔여 위험 목록을 정리 → **본 개발(M0~M5) 착수 여부 결정 자료**로 제출.

---

## 4. 위험 관리

| # | 위험 | 완화 |
|---|---|---|
| E1 | C++ 워크로드 설치 실패(권한/디스크) | 사전 관리자 승인 확보. 실패 시 MVP 자체 불가 → 조기 보고 |
| E2 | `/clr:netcore` + net8.0 실컴파일 문제 | **해소** — 1.3 실컴파일·로드 + **S1 WPF 앱 경유 로드·렌더·호출 확인**(`UseIJWHost` 필요) |
| E3 | prebuilt OCCT(vc14)와 v143 링크 호환 | **해소** — 1.3 TKernel + **S1 뷰어(TKV3d·TKOpenGl·TKService)·HLR(TKHLR) 링크·렌더 실측 통과** |
| E4 | Mold.stp에 솔리드 B-rep 없음(와이어프레임만) | **1.2 계량(솔리드 30개) + 2.1 사전 스모크(관계 표현 전개·30솔리드 로드 실측)로 해소** — S1 V1은 C++/CLI 브리지 경유 로드만 확인 |
| E5 | 자유곡면 pcurve 품질(UV 경계 추출) | MVP는 평면 면 주경로. 곡면은 관찰만. **S3 관찰 완료: 원통면 u=각도(rad)·v=mm — UV 스케일 보정 없이는 패턴 불가 확인.** Seam(주기면) 처리는 본 개발 항목으로 기록 |
| E6 | HLR 성능 (수백 면) | **S1 실측: 면 664(솔리드 30) 전체 1.9~2.1초 — 양호.** 필요 시 HLR 대상 축소(발열선+윤곽만) |
| E7 | VS Community 상용 라이선스 | 상용 배포 시 사용 조건(5명 미만·비엔터프라이즈) 충족 여부 확인 — 위반 시 Professional 구매. **개발 시작 전 확인 권장** |

---

## 5. 산출물 및 일정

| 구분 | 내용 |
|---|---|
| 일정 | 총 4~7일 (S1/S2 병행 시 4~5일) |
| 산출물 | MVP 솔루션 · xUnit 테스트 · 샘플 DXF · 판정 보고서 |
| 재사용 | OccBridge·Core·Export는 본 개발 M1~M4의 기반으로 그대로 이관 |

---

## 6. 판정 종료 조건 (Exit Criteria)

MVP는 다음 문서가 나오는 순간 종료한다.

1. **V1~V7 판정표** — 각 GO/NO-GO + 근거
2. **잔여 위험 목록** — 본 개발 이관 항목 (Seam 처리, 곡면 품질, 성능 등)
3. **착수 권고/중단 의견** — 본 개발 M0~M5 착수 여부

판정표에서 "전체 과정 구현 가능"이 확정되면 모문서 M0~M5를 그대로 착수한다. NO-GO 발생 시 해당 체인의 대체 기술(P/Invoke 전환, HLR 대체, 타 커널 검토 등)을 먼저 결정한다.

---

## 7. 본 개발 이관 시 유의사항

| 항목 | 내용 |
|---|---|
| 문서 정정 | 모문서 "AP242" → 실제 파일은 AP214 계열 (`AUTOMOTIVE_DESIGN`) |
| OCCT 버전 | MVP는 7.9.3 prebuilt. 본 개발 M0에서 소스 빌드 전환 여부 결정 (LGPL 동적 링크 유지) |
| 미구현 이관 | 제외영역 스케치 UI, JSON 저장/불러오기, 픽킹 완성, 홀 자동 회피, 곡면 품질 |
| 라이선스 | VS Community 상용 사용 조건 확인 결과 반영 |
