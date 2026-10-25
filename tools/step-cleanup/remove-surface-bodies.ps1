<#
.SYNOPSIS
    STEP 파일에서 MANIFOLD_SURFACE_SHAPE_REPRESENTATION(자유 곡면 바디)을 제품 구조에서 분리한다.

.DESCRIPTION
    Mold.stp 에는 MANIFOLD_SOLID_BREP 30개 외에 SHELL_BASED_SURFACE_MODEL 2개
    (SURFACE.135 = 86면, SURFACE.142 = 4면)가 함께 들어 있다. 이 스크립트는
    표면 표현을 참조하는 SHAPE_REPRESENTATION_RELATIONSHIP 엔티티 하나만 제거해서
    솔리드 30개만 로드되게 만든다.

    OCCT 로 다시 읽고 쓰는 방식(재출력)과 달리, 나머지 엔티티는 바이트 단위로
    그대로 유지된다. 특히 'supplemental geometry' 표현(#10/#11)이 보존되므로
    HeatingMold 의 ComputeAutoAxes() 가 이전과 동일한 모델 좌표계를 얻는다.

.PARAMETER In
    원본 STEP 경로.

.PARAMETER Out
    결과 STEP 경로.

.EXAMPLE
    .\remove-surface-bodies.ps1 -In ..\..\resource\Mold.stp -Out ..\..\resource\Mold_Solids.stp
#>
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'

$inPath = (Resolve-Path -LiteralPath $In).Path
$text = [IO.File]::ReadAllText($inPath)

# 1) 표면 표현 엔티티 id 수집
$surfIds = [System.Collections.Generic.List[string]]::new()
foreach ($m in [regex]::Matches($text, '#(\d+)\s*=\s*MANIFOLD_SURFACE_SHAPE_REPRESENTATION\s*\(')) {
    $surfIds.Add($m.Groups[1].Value)
}
if ($surfIds.Count -eq 0) {
    Write-Host "표면 표현 엔티티가 없습니다. 원본을 그대로 복사합니다."
    [IO.File]::WriteAllText($Out, $text)
    exit 0
}
Write-Host ("표면 표현 {0}개 발견: {1}" -f $surfIds.Count, (($surfIds | ForEach-Object { "#$_" }) -join ", "))

# 2) 해당 표현을 참조하는 SHAPE_REPRESENTATION_RELATIONSHIP 제거
$removed = 0
foreach ($sid in $surfIds) {
    $pattern = "(?ms)^#\d+\s*=\s*SHAPE_REPRESENTATION_RELATIONSHIP\s*\([^;]*?,\s*#$sid\s*\);\s*\r?\n"
    $matches = [regex]::Matches($text, $pattern)
    foreach ($m in $matches) {
        Write-Host ("관계 엔티티 제거: {0} bytes" -f $m.Length)
        $text = $text.Remove($m.Index, $m.Length)
        $removed++
    }
}

if ($removed -eq 0) {
    Write-Warning "참조하는 SHAPE_REPRESENTATION_RELATIONSHIP 를 찾지 못했습니다. 파일을 수정하지 않습니다."
    exit 1
}

[IO.File]::WriteAllText($Out, $text)
Write-Host ("완료: {0} (관계 엔티티 {1}개 제거)" -f (Resolve-Path -LiteralPath $Out).Path, $removed)
