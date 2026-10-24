# Infrastructure.Persistence

`ProjectPackageStore`는 `.hmproj` version 1 ZIP 형식을 소유합니다. Core 설계 문서와 캡처한 원본 STEP 바이트를 저장하고, 필수 항목·payload 한도·SHA-256·지원 schema version을 검사한 뒤 프로젝트를 반환합니다.

쓰기는 대상 파일 옆에서 조립하고 flush한 뒤 이동합니다. 쓰기가 실패하거나 취소되면 기존 패키지를 유지합니다. 읽기는 프로젝트와 원본 바이트를 함께 반환해 호출자가 기하를 준비한 뒤 활성 session을 교체할 수 있게 합니다. 압축 해제는 선언된 길이만큼의 단일 버퍼를 사용하고, 읽기 중 취소를 확인하며 선언 길이와 실제 데이터 길이가 다르면 거부합니다.

`PackageJsonOptions`는 Core 값 객체와 곡선 정의의 JSON converter를 한곳에 둡니다. Runtime handle, native pointer, UI 상태, 절대 원본 경로는 패키지에 포함하지 않습니다. 파생 기하·면적 배분·전기 결과는 복원된 설계에서 다시 계산합니다.

`StepModelSourceStore`는 원본 파일 바이트를 캡처하고 주입된 import metadata로 identity를 기록합니다. OCCT 문서 수명은 소유하지 않으며 geometry adapter가 관리합니다.
