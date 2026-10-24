# HeatingMold.Application

Application은 Core의 확정 설계와 Design·Electrical 서비스, 파일·기하·출력 포트 사이의 흐름을 조정합니다. `net8.0` 순수 관리형 프로젝트이며 WPF, 파일 시스템, OccBridge를 직접 참조하지 않습니다.

`ProjectSession`은 불변 `SessionSnapshot`을 공개하고 입력 draft, 저장 상태, 진행 중 작업을 따로 관리합니다. `CandidateCoordinator`는 기준 revision 확인, 참조 검증, Native staging, 파생값 계산, Publish, 세션 교체 순서를 한 곳에서 수행합니다. 준비 snapshot의 곡선 ID·수학 정의가 후보 설계와 일치하고 모든 곡선 표본 근사가 완료돼야 Publish합니다. Publish 전 실패는 기존 snapshot을 보존하며 Publish 뒤 알림 실패는 새 설계를 유지하고 `PresentationOutOfSync`를 표시합니다.

현재 유스케이스는 모델 열기, 작업축 변경, 영역 갱신, 경로 생성·편집, 회로 topology, 연결 경로, 전기 재계산, 프로젝트 저장·열기, STEP/DXF 출력 포트를 제공합니다. 프로젝트 복원은 원본 모델 바이트로 별도 문서를 준비한 다음 최종 곡선에서 회로 길이 원장과 전기 결과를 다시 계산합니다. 패키지에서 복원한 snapshot은 저장된 revision을 저장 기준으로 유지하고, 같은 프로젝트 ID를 새 세션 revision으로 바꿔도 clean 상태로 시작합니다. 전기 stamp와 일치하는 면적 cache만 재사용하고 새 전기 판정 면적은 다시 집계합니다. 저장은 캡처한 revision만 파일로 기록하며 저장 중 새 revision이 확정되면 현재 세션의 dirty 상태를 유지합니다. 출력은 draft, 잘못된 영속 참조, 최신이 아닌 결과, 누락·비활성 곡선이 있으면 exporter 호출 전에 차단합니다.

기하·파일·출력 구현은 각각 `IGeometryWorkspace`, `IProjectPackageStore`, `IModelSourceStore`, `IDesignExporter`로 주입합니다. 같은 프로젝트 안에서도 Native 문서의 준비/확정과 파일 원자 교체는 해당 어댑터가 소유합니다.

관리형 테스트는 다음 명령으로 실행합니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Group Application
```

현재 Application 테스트 64개는 후보 트랜잭션의 prepare/publish/알림 실패 보존, 준비된 geometry와 활성 발열·연결 곡선 ID·정의·표본 근사 완전성, 모델과 프로젝트 복원, 저장 실패와 revision 경합, 영역 갱신에 따른 종속 경로·회로·연결 정리, 각 편집 유스케이스의 오래된 요청·소유자 거부, draft/stale 출력 차단, 회로별 길이 원장 재구성, 프로젝트 면적 규칙 버전과 다른 캐시의 재계산을 확인합니다. 회로 topology는 변경하지 않은 연결 경로 보존, 새 연결 경로 완전성, 회전 구간 상태 변경과 회로 분할·Supply 상속을 함께 검증합니다. 경로 생성은 Supply·양 끝 연결부·ConnectionPath를 받아 경로와 함께 원자 확정하며, 관리형 first-hit fixture로 성공과 연결 입력 누락 차단을 확인합니다. `UpdateRegion.ApplyAdjustment`는 선택 영역의 포함·제외 루프를 갱신하고 영향을 받는 경로·회로·연결을 같은 후보 transaction에서 무효화하며, 다른 영역의 경로는 보존합니다. 출구 측을 제거하는 수정은 후보 확정 전에 거부합니다. 실제 Native staging과 WPF 흐름 연결, 절점 편집·연결 변경 성공 경로는 계속 검증해야 합니다.
