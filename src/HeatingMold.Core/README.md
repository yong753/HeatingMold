# HeatingMold.Core

Core는 다른 제품 모듈이 공유하는 설계 용어, 식별자, 단위와 기하 값을 소유합니다. 기준 용어는 [용어 안내](../../docs/implementation/2026-10-20/TERMINOLOGY.md), 공유 규칙은 [공통 계약](../../docs/implementation/2026-10-20/00.Shared.Contracts.md), 전체 순서는 [리팩토링 실행 계획](../../docs/implementation/2026-10-20/08.Refactoring.Execution.Plan.md)을 따릅니다.

`Primitives`의 Guid 식별자는 영속 ID와 런타임 ID를 타입으로 구분합니다. 기본값은 `Guid.Empty`일 수 있으므로 프로젝트·엔터티를 구성할 때는 `IsEmpty`를 검사하고 새 값은 해당 ID의 `New()`로 생성합니다. 경로 조각 순서나 네이티브 Face 번호는 영속 ID로 사용하지 않습니다.

`Units`의 이름 있는 값은 내부 숫자를 표시합니다. 이 값은 입력의 유한성만 보장하며, 양수·음수나 0의 허용 여부는 제약·계산 계약을 검사하는 모듈이 결정합니다. 전기 계산에서는 `LengthMm.ToMeters()`를 사용해 저항 대상 길이를 한 번만 변환합니다. 면적의 cm²/m² 변환은 표시 계층에 둡니다.

| 개념 | 코드 이름 | 용도 |
|---|---|---|
| 프로젝트와 설계 요소의 안정 ID | `ProjectId`, `RegionId`, `HeatingPathSegmentId`, `CircuitId` 등 | 저장·재열기에 유지 |
| 네이티브 문서와 변경 후보 ID | `GeometryDocumentId`, `CandidateId` | 열린 세션에서만 사용 |
| 확정 설계 변경 revision | `DesignRevision` | 성공한 설계 변경마다 증가 |
| 네이티브 모델 기하 revision | `ModelRevision` | 열린 문서에만 속하며 프로젝트 JSON에는 저장하지 않음 |
| 위치와 기하 길이 | `Vec2`, `Vec3`, `LengthMm`, `AreaMm2` | mm 및 mm² 기준 |
| 전기량 | `ResistanceOhmsPerMeter`, `ResistanceOhms`, `VoltageVrms`, `CurrentAmps`, `PowerWatts` | Ω/m, Ω, V RMS, A, W 기준 |
| 모델 고정 작업 좌표계 | `WorkingCoordinateSystem`, `ProjectionFrame` | 카메라 상태와 독립적인 원점과 X/Y/N축 |
| 영속 설계 스냅샷 | `DesignProject`, `HeatingRegion`, `HeatingPath`, `Circuit` | 안정 ID와 참조 무결성 검사 |
| 원본 곡선·측정 결과 | `CurveDefinition`, `GeometrySnapshot`, `CurveGeometrySnapshot` | 재생성 정의와 Native 최종 길이·샘플 분리 |
| 영역 경계·배치 제약 | `BoundaryLoop`, `ExitSideReference`, `PlacementConstraints` | g·d·D·방향·고정 앵커 보존 |
| 파생 면적·검증 결과 | `AppliedAreaAllocation`, `ValidationReport`, `OperationResult<T>` | 수렴 근거와 실패 원인 보존 |

현재 값 계약은 좌표 벡터의 NaN·무한대를 거부하고 영벡터 정규화에 실패합니다. `ProjectionFrame`은 영법선과 영벡터·법선에 평행한 X축 후보를 거부하며 오른손 직교 기저를 만듭니다. 좌표 연산 결과가 표현 범위를 넘으면 유한한 값인 것처럼 반환하지 않습니다. 저장 좌표계는 세션 식별자와 분리된 `WorkingCoordinateSystemDefinition`으로 보관합니다.

영속 모델은 읽기 전용 컬렉션과 타입별 ID를 사용합니다. `DesignProject.ValidateReferences()`는 Face·영역·경로·절점·회로·연결부 참조와 활성 조각의 회로 소속을 검사하며, 거리·깊이·표면 교차 계산은 수행하지 않습니다. `CurveDefinition`은 Line/Polyline/Arc/BSpline 원형 정의이고 `GeometrySnapshot`은 열린 Native 문서가 계산한 최종 곡선 길이·표본·근사 증거를 담습니다. 두 표현을 서로 대체하지 않습니다.

`IGeometryQueries`는 전체 모델 first-hit, 선택 topology, 실제 3D 간격·깊이, trimmed 표면 분할과 연결 경로 충돌의 프레임워크 독립 포트입니다. `AppliedAreaAllocation`은 회로별 면적, 미배정 면적, 실면적 잔차·근사 설정·수렴 여부를 보존합니다. 구현 어댑터와 모든 기하 판단은 Infrastructure/Design에서 제공합니다.

`CurveGeometrySnapshot`의 첫·마지막 표본은 `Start`·`End` 측정값과 같아야 합니다. 그래야 끝점 연속성 검사와 표본 기반 간격·면적 계산이 같은 기하를 참조합니다. `CurveDefinitionComparer.AreEquivalent`는 직렬화나 브리지 변환 뒤 새로 생성된 곡선 정의도 배열 참조가 아닌 좌표·방향·가중치·knot·trim 값으로 비교합니다. 설계 모델과 Native 준비 snapshot의 곡선 정의가 같은지 검증할 때 사용합니다.

레거시 2차원 `PatternParameters`, `HeatingPathGenerator`, `RegionOps`, `SurfacePathMapper`, `CircuitPathPartitioner`, 투영 경계 계산과 Clipper2 의존은 `HeatingMold.Design`으로 이전했습니다. 이 API는 `Design.Legacy`, `Design.Regions`, `Design.GeometryMapping` 아래에 격리했고 새 영속 설계 모델로 취급하지 않습니다. 작업 좌표계 변경 미리보기 세션은 `HeatingMold.Application/Sessions/WorkingCoordinateSystemSession.cs`로 이전했습니다.

Core에서 맡는 범위는 값과 계약입니다. 경로 생성·영역 Boolean·저장 직렬화·OCCT 호출·전기 계산·화면 상태는 각각 Design, Infrastructure, Electrical, App/Application에 둡니다. Core를 순수 `net8.0` 라이브러리로 유지하고 플랫폼·파일 형식 의존을 추가하지 않습니다.
