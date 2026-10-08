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

### 1.2 Mold.stp 실물 확인

- NX 12.0에서 ST-Developer v16.7로 내보낸 파일
- 스키마: `AUTOMOTIVE_DESIGN`(AP214 계열) — **모문서의 "AP242" 기재와 불일치** (기능 영향은 낮으나 문서 정정 필요)
- `CONSTRUCTIVE_GEOMETRY_REPRESENTATION`, `GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION` 엔티티 포함 → **솔리드 B-rep 포함 여부는 로드 시 확인 필요** (S1에서 조기 판별)

### 1.3 외부 기술 검증 (공식 자료 확인)

| 항목 | 검증 결과 |
|---|---|
| C++/CLI + .NET 8 (R1) | MS 공식 문서에서 `<CLRSupport>NetCore</CLRSupport>` + `<TargetFramework>net8.0</TargetFramework>` 정식 지원 확인. 제약: **DLL만 빌드 가능**(WPF 앱이 exe이므로 문제 없음), vcxproj 비-SDK 형식, .NET 7+는 기본 AssemblyLoadContext 로드(이중 로드 문제 없음) → **실컴파일 검증만 남음** |
| OCCT 릴리스 | 최신 8.0.1(2026-07-30, C++17 기준·빌드시스템 대개편·API 대규모 변경) / 7.9.3(7.9 계열 최종) → **7.9.3 고정 권장** (샘플·문서 호환성) |
| OCCT 바이너리 | prebuilt vc14-64 + 3rdparty(zip) 제공. vc14 = VS2015 ABI로 v143과 호환 → 소스 빌드 전 prebuilt 우선 |

### 1.4 사전 판정

7개 핵심 체인 모두 표준 기술 조합으로 **구현 가능성 높음**. 유일한 진입 장벽은 C++ 도구체인 미설치(반나절 작업)이며, 나머지 위험은 S1 스파이크에서 조기 판정한다.

---

## 2. MVP 정의

### 2.1 검증 목표 (GO/NO-GO 판정 대상)

| # | 검증 체인 | 실패 시 프로젝트 영향 |
|---|---|---|
| V1 | Mold.stp B-rep 로드·3D 표시 | 치명 |
| V2 | 면 선택(픽킹 또는 인덱스) → faceId 식별 | 치명 |
| V3 | UV 경계(외곽+홀) 폴리곤 추출 → C# 전달 | 치명 |
| V4 | Clipper2 마진/제외영역 차집합 + 사행 레인 생성 | 치명 |
| V5 | UV→3D 매핑 + 3D 와이어/리본 표시 | 치명 |
| V6 | HLR 투영 → 2D 선분 | 심각 (대체 수단 있음) |
| V7 | DXF 출력 → AutoCAD 열람 | 치명 |

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
6. C++/CLI net8.0 DLL이 WPF 앱에서 로드·호출 (R1 판정)

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

### S4 — 판정 문서화 (0.5일)

V1~V7 각각 GO/NO-GO + 근거, 로드·HLR 소요시간, 곡면 관찰 결과, 잔여 위험 목록을 정리 → **본 개발(M0~M5) 착수 여부 결정 자료**로 제출.

---

## 4. 위험 관리

| # | 위험 | 완화 |
|---|---|---|
| E1 | C++ 워크로드 설치 실패(권한/디스크) | 사전 관리자 승인 확보. 실패 시 MVP 자체 불가 → 조기 보고 |
| E2 | `/clr:netcore` + net8.0 실컴파일 문제 | 공식 문서상 지원 확인됨. 실패 시 net6.0 하향 또는 C ABI 전환(1~2일) |
| E3 | prebuilt OCCT(vc14)와 v143 링크 호환 | MSVC ABI는 VS2015 이후 호환. 문제 시 소스 빌드 |
| E4 | Mold.stp에 솔리드 B-rep 없음(와이어프레임만) | `GetShapeInfo`로 S1 초기에 판별 — 조기 확인 필수 |
| E5 | 자유곡면 pcurve 품질(UV 경계 추출) | MVP는 평면 면 주경로. 곡면은 관찰만. Seam(주기면) 처리는 본 개발 항목으로 기록 |
| E6 | HLR 성능 (수백 면) | S1 스모크에서 시간 측정. 느리면 HLR 대상 축소(발열선+윤곽만) |
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
