# HeatingMold.App

WPF 화면과 OCCT 뷰어 사이의 어댑터를 둔다. 화면 입력은 Application 유스케이스로 전달하고, 기하 질의와 변경은 `Geometry/Occt` 경계를 거쳐 활성 또는 후보 Native 세션에 요청한다.

## 코드 위치

- `AppCompositionRoot.cs`: Application 유스케이스와 포트를 조립한다.
- `MainWindow.xaml` 및 `MainWindow.*.cs`: 화면을 표시하고 기능별 이벤트를 연결한다. 부분 클래스 파일은 모델 로드, 프로젝트 파일, 경로 생성, 영역 편집, 결과, 출력처럼 사용자 동작별로 나뉜다.
- `ViewModels/`: 화면 입력과 명령을 사용자 동작별로 나눈다.
- `WireSpecificationInputsViewModel`과 `HeatingPathPlacementInputViewModel`: 공유 선재 제원과 경로 배치 입력을 검증한다.
- `ElectricalSpecificationViewModel`, `HeatingPathAnchorViewModel`, `HeatingPathRegionSelectionViewModel`: 전기 초안, 앵커 입력 대기, 경로 생성 영역 선택을 관리한다.
- `RegionSelectionViewModel`: 안정 Face 참조와 해당 선택의 topology·경계·투영 frame·진단을 관리한다.
- `Presentation/`: 작업 평면 영역 경계를 Canvas 화면 좌표로 투영하는 표시 계산을 둔다.
- `Geometry/Occt/`: Core 계약을 Native Bridge 호출과 활성·후보 문서 세션에 연결한다. `FaceReferenceIndex`는 현재 문서의 Native 면 번호와 영속 Face ID 대응을 한 번에 교체하고 양방향 맵을 읽기 전용으로 제공한다.
- `Services/`: 화면에서 사용하는 진단 표현과 확인 동작처럼 교체 가능한 UI 지원 기능을 둔다.
- `OccViewport.cs`: WPF 입력·화면 수명과 OCCT 뷰어를 연결한다.

화면에서 확정 프로젝트를 직접 수정하지 않는다. 사용자의 편집은 Application 초안 또는 후보를 거치며, 실패한 적용은 기존 확정 상태를 보존한다. 뷰포트는 확정·후보 세션의 식별자를 맞춰 요청해야 한다.

## 검증

`tests/HeatingMold.App.Tests`는 화면 지원 코드와 Bridge 대역을 함께 컴파일해 입력 전이와 UI 어댑터 계약을 검사한다. 관리형 전체 회귀는 저장소 루트에서 `scripts/Verify.ps1 -Group Managed`로 실행한다. WPF 실행, 실제 OCCT 질의와 설치 패키지는 C++/CLI `MSBuild.exe`, `OccBridge.dll`, OCCT 런타임이 준비된 Windows 환경에서 별도로 확인한다.
