<#
.SYNOPSIS
    STEP 파일에 앱이 로드 시 적용하는 축 정렬(회전)을 미리 구워 넣는다.

.DESCRIPTION
    앱(OccBridge)은 STEP 을 읽은 뒤 "supplemental geometry" 표현의 AXIS2_PLACEMENT_3D 로
    모델 좌표계를 잡고, 그 축을 월드 X/Y/Z 에 맞추는 회전을 형상에 적용한다(원점 고정, 이동 없음).
    이 스크립트는 같은 회전을 파일 자체에 적용해, CAD 에서 열었을 때 앱과 같은 좌표로 보이게 한다.

    - CARTESIAN_POINT / DIRECTION 의 좌표 삼중값만 회전시킨다. 반지름/각도/파라미터/단위는 그대로 둔다.
    - supplemental geometry 자체도 함께 회전하므로, 이후 앱이 읽으면 적용 회전이 항등이 된다.
      (즉 CAD 좌표 == 앱 좌표)
    - 원본 파일은 건드리지 않는다.

.EXAMPLE
    .\bake-axes.ps1 -In ..\..\resource\Mold_Solids.stp -Out ..\..\artifacts\aligned\Mold_Solids_aligned.stp
#>
param(
    [Parameter(Mandatory = $true)][string]$In,
    [string]$Out = ""
)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

function Get-EntityText([string]$text, [int]$id) {
    $m = [regex]::Match($text, "(?m)^\s*#$id\s*=.*?;", 'Singleline')
    if (-not $m.Success) { throw "entity #$id not found" }
    return $m.Value
}

function Get-Triple([string]$text, [int]$id) {
    $e = Get-EntityText $text $id
    $m = [regex]::Match($e, "\(\s*'[^']*'\s*,\s*\(([^)]*)\)")
    if (-not $m.Success) { throw "coordinate tuple not found in #$id" }
    $parts = $m.Groups[1].Value.Split(',')
    if ($parts.Count -ne 3) { throw "entity #$id is not a 3D tuple" }
    $v = @(0.0, 0.0, 0.0)
    for ($i = 0; $i -lt 3; $i++) {
        $v[$i] = [double]::Parse($parts[$i].Trim(), [Globalization.NumberStyles]::Float, $inv)
    }
    return $v
}

function MaxAbs([double[]]$v) {
    $m = 0.0
    foreach ($c in $v) { if ([math]::Abs($c) -gt $m) { $m = [math]::Abs($c) } }
    return $m
}

function Normalize([double[]]$v) {
    $n = [math]::Sqrt($v[0] * $v[0] + $v[1] * $v[1] + $v[2] * $v[2])
    if ($n -le 1e-30) { throw "zero-length vector" }
    # PowerShell 은 배열 리터럴 안에서 쉼표가 산술 연산자보다 우선하므로 요소마다 괄호가 필요하다.
    return @(($v[0] / $n), ($v[1] / $n), ($v[2] / $n))
}

# 앱과 동일한 규칙으로 supplemental geometry 의 축 기저를 만든다.
function Get-AxesBasis([string]$text) {
    $rep = [regex]::Match($text, "=\s*[A-Z_]*REPRESENTATION\s*\(\s*'supplemental geometry'\s*,\s*\(\s*#(\d+)")
    if (-not $rep.Success) { throw "'supplemental geometry' representation not found" }
    $placementId = [int]$rep.Groups[1].Value

    $pl = Get-EntityText $text $placementId
    $pm = [regex]::Match($pl, "AXIS2_PLACEMENT_3D\s*\(\s*'[^']*'\s*,\s*#(\d+)\s*,\s*#(\d+)\s*,\s*#(\d+)\s*\)")
    if (-not $pm.Success) { throw "AXIS2_PLACEMENT_3D #$placementId is not in the expected 3-reference form" }

    $zRaw = Get-Triple $text ([int]$pm.Groups[2].Value)
    $xRaw = Get-Triple $text ([int]$pm.Groups[3].Value)

    $z = Normalize ($zRaw | ForEach-Object { $_ / (MaxAbs $zRaw) })
    $x = Normalize ($xRaw | ForEach-Object { $_ / (MaxAbs $xRaw) })
    $dot = $x[0] * $z[0] + $x[1] * $z[1] + $x[2] * $z[2]
    $x = Normalize @(($x[0] - $dot * $z[0]), ($x[1] - $dot * $z[1]), ($x[2] - $dot * $z[2]))
    $y = @(($z[1] * $x[2] - $z[2] * $x[1]), ($z[2] * $x[0] - $z[0] * $x[2]), ($z[0] * $x[1] - $z[1] * $x[0]))
    $y = Normalize $y

    return @{
        Placement = $placementId
        X         = $x
        Y         = $y
        Z         = $z
    }
}

function Format-Real([double]$d) {
    if ($d -eq 0.0) { return '0.' }
    $s = $d.ToString('R', $inv)
    if ($s.IndexOf('.') -lt 0 -and $s.IndexOf('e') -lt 0 -and $s.IndexOf('E') -lt 0) { $s += '.' }
    return $s
}

if (-not $Out) {
    $dir = Split-Path -Parent (Resolve-Path $In).Path
    $name = [IO.Path]::GetFileNameWithoutExtension($In)
    $Out = Join-Path $dir "$name`_aligned.stp"
}

$inPath = (Resolve-Path $In).Path
$text = [IO.File]::ReadAllText($inPath)
$basis = Get-AxesBasis $text

$R = @($basis.X, $basis.Y, $basis.Z)
Write-Output ("### source            : " + $inPath)
Write-Output ("### supplemental rep  : #" + $basis.Placement)
Write-Output ("### baked rotation rows (aligned = R * source):")
foreach ($row in $R) {
    Write-Output ("    " + (($row | ForEach-Object { $_.ToString('0.000000000000', $inv) }) -join ', '))
}

$state = @{ Rewritten = 0; Skipped = 0 }
$evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
    param($m)
    $parts = $m.Groups[3].Value.Split(',')
    if ($parts.Count -ne 3) { $state.Skipped++; return $m.Value }
    $v = @(0.0, 0.0, 0.0)
    for ($i = 0; $i -lt 3; $i++) {
        $v[$i] = [double]::Parse($parts[$i].Trim(), [Globalization.NumberStyles]::Float, $inv)
    }
    $r = @(
        ($R[0][0] * $v[0] + $R[0][1] * $v[1] + $R[0][2] * $v[2]),
        ($R[1][0] * $v[0] + $R[1][1] * $v[1] + $R[1][2] * $v[2]),
        ($R[2][0] * $v[0] + $R[2][1] * $v[1] + $R[2][2] * $v[2])
    )
    $state.Rewritten++
    $s = (($r | ForEach-Object { Format-Real $_ }) -join ',')
    return $m.Groups[1].Value + $m.Groups[2].Value + $s + $m.Groups[4].Value
}

# 좌표 삼중값을 가진 두 엔티티만 손댄다. 이름 문자열/괄호는 그대로 보존한다.
$newText = [regex]::Replace(
    $text,
    "(CARTESIAN_POINT|DIRECTION)(\s*\(\s*'[^']*'\s*,\s*\()([^)]*)(\)\s*\))",
    $evaluator)

Write-Output ("### rewritten tuples  : " + $state.Rewritten + " (skipped " + $state.Skipped + ")")

$outPath = [IO.Path]::GetFullPath($Out)
$outDir = Split-Path -Parent $outPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
[IO.File]::WriteAllText($outPath, $newText, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("### written           : " + $outPath + " (" + (Get-Item $outPath).Length + " B)")

# 검증: 출력 파일의 supplemental 축이 항등 기저가 되어야 앱이 추가 회전을 하지 않는다.
$verifyBasis = Get-AxesBasis ([IO.File]::ReadAllText($outPath))
$dev = 0.0
for ($i = 0; $i -lt 3; $i++) {
    for ($j = 0; $j -lt 3; $j++) {
        $expected = if ($i -eq $j) { 1.0 } else { 0.0 }
        $actual = @($verifyBasis.X, $verifyBasis.Y, $verifyBasis.Z)[$i][$j]
        $dev = [math]::Max($dev, [math]::Abs($actual - $expected))
    }
}
Write-Output ("### residual rotation : max |R' - I| = " + $dev.ToString('0.######E+00', $inv))
