# HeatingMold.Electrical

회로 단위 전기 계산과 한도 판정을 담당합니다. `HeatingMold.Core`만 참조하며 OCCT, WPF, Clipper2, netDxf, 파일 입출력에 의존하지 않습니다.

`CircuitElectricalCalculator`는 `LengthBreakdown.ResistanceLengthMm`만 저항 길이로 사용합니다. 본 발열 경로, 같은 발열사의 연장부, 발열 여유는 포함하고 별도 연결선과 해당 여유는 제외합니다. 길이는 mm에서 m로 한 번 변환한 뒤 `R = r × L`, `I = V / R`, `P = V² / R`을 반올림 전에 계산합니다. 도체 지름은 사양 정보로 보존하지만 단위 저항을 자동 변경하지 않습니다.

입력 오류는 `ElectricalResult.InputIssues`의 안정 코드로, 빠진 필수 입력은 `Missing:<field>` 코드로 반환합니다. 결과 상태는 `InvalidInput`, `LimitExceeded`, `Incomplete`, `Pass` 중 하나입니다. 실제 전압·최대 전압·최대 전류가 필수이며 최대 전력은 선택 입력입니다. 여러 회로의 결과는 서로 독립적으로 판정합니다.

`WirePresetCatalog`는 SUS 316L Case 1/2 초기값을 제공합니다. 호출자는 프리셋에서 사양 값을 가져와 프로젝트의 사양으로 복사·편집할 수 있습니다.

`ElectricallyCompliantAreaAggregator`는 `AppliedAreaAllocation`과 동일한 `DependencyStamp`를 가진 회로 결과만 합산합니다. Pass 회로 면적, 비합격 회로 면적, 누락·불완전 결과 면적을 분리하며 stamp가 다르면 파생 면적을 비워 `StaleDerivedResult`로 돌려줍니다. 분할의 실제 면적·근사·잔차는 Core allocation에서 그대로 보존합니다.

## 검증

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Group Electrical
```

현재 단위 테스트는 두 사례 수치, 길이 역할, 독립 회로, 한도 경계, 입력 누락·무효, 반올림 전 비교, 산술 범위, 지름 변경, 프리셋과 적용면적 집계를 확인합니다.
