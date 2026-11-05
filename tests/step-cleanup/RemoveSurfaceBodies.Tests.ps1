# Self-contained regression tests for tools/step-cleanup/remove-surface-bodies.ps1.
# No Pester dependency: the script is invoked in a child PowerShell process so its
# explicit exit codes cannot terminate this test runner.
$ErrorActionPreference = 'Stop'
$script = (Resolve-Path (Join-Path $PSScriptRoot '../../tools/step-cleanup/remove-surface-bodies.ps1')).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('step-remove-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

$count = 0
function Check([bool]$value, [string]$label) {
    if (-not $value) { throw "FAIL: $label" }
    Write-Host "PASS $label"
    $script:count++
}
function Write-Step([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text)
}
function Invoke-Cleanup([string]$inputPath, [string]$outputPath) {
    $exe = (Get-Command powershell -ErrorAction Stop).Source
    & $exe -NoProfile -ExecutionPolicy Bypass -File $script -In $inputPath -Out $outputPath | Out-Null
    return $LASTEXITCODE
}

$surfaceOne = @"
ISO-10303-21;
HEADER;
ENDSEC;
DATA;
#1=MANIFOLD_SURFACE_SHAPE_REPRESENTATION('surface one',(#2),#3);
#2=CARTESIAN_POINT('',(0.,0.,0.));
#3=AXIS2_PLACEMENT_3D('',#2,#4,#5);
#4=DIRECTION('',(0.,0.,1.));
#5=DIRECTION('',(1.,0.,0.));
#10=SHAPE_REPRESENTATION_RELATIONSHIP('rel one long name','',#20,#1);
#11=SHAPE_REPRESENTATION_RELATIONSHIP('rel two','',#20,#1);
#12=SHAPE_REPRESENTATION_RELATIONSHIP('keep unrelated','',#20,#30);
#20=SHAPE_REPRESENTATION('product',(#2),#3);
#30=MANIFOLD_SOLID_BREP('solid',#31);
#50=CARTESIAN_POINT('supplemental',(1.,2.,3.));
#51=SHAPE_REPRESENTATION('supplemental geometry',(#50),#3);
ENDSEC;
END-ISO-10303-21;
"@

try {
    # One surface referenced by two relationships: both must disappear, unrelated
    # entities and the supplemental geometry must survive.
    $inA = Join-Path $temp 'one-surface.stp'
    $outA = Join-Path $temp 'one-surface-out.stp'
    Write-Step $inA $surfaceOne
    Check ((Invoke-Cleanup $inA $outA) -eq 0) 'multiple relationships on one surface exits 0'
    $resultA = [IO.File]::ReadAllText($outA)
    Check (-not $resultA.Contains('rel one long name') -and -not $resultA.Contains('rel two')) `
        'all relationships referencing the target surface are removed'
    Check ($resultA.Contains('keep unrelated') -and $resultA.Contains('supplemental geometry')) `
        'unrelated relationship and supplemental geometry are preserved'
    Check ($resultA.Contains('#30=MANIFOLD_SOLID_BREP')) 'solid entities are preserved'

    # Two different surface representations must both be cleaned in the same run.
    $surfaceTwo = $surfaceOne.Replace(
        '#10=SHAPE_REPRESENTATION_RELATIONSHIP(''rel one long name'','''',#20,#1);',
        '#10=SHAPE_REPRESENTATION_RELATIONSHIP(''rel one long name'','''',#20,#1);' + "`r`n" +
        '#6=MANIFOLD_SURFACE_SHAPE_REPRESENTATION(''surface two'',(#2),#3);' + "`r`n" +
        '#13=SHAPE_REPRESENTATION_RELATIONSHIP(''rel three'','''',#20,#6);')
    $inB = Join-Path $temp 'two-surfaces.stp'
    $outB = Join-Path $temp 'two-surfaces-out.stp'
    Write-Step $inB $surfaceTwo
    Check ((Invoke-Cleanup $inB $outB) -eq 0) 'two surface representations exit 0'
    $resultB = [IO.File]::ReadAllText($outB)
    Check (-not $resultB.Contains('rel one long name') -and -not $resultB.Contains('rel two') -and
           -not $resultB.Contains('rel three')) 'relationships for every surface representation are removed'
    Check ($resultB.Contains('keep unrelated')) 'unrelated relationship survives multi-surface cleanup'

    # No surface representation: copy input unchanged and exit 0.
    $noSurfaceText = @"
ISO-10303-21;
DATA;
#30=MANIFOLD_SOLID_BREP('solid',#31);
ENDSEC;
END-ISO-10303-21;
"@
    $inC = Join-Path $temp 'no-surface.stp'
    $outC = Join-Path $temp 'no-surface-out.stp'
    Write-Step $inC $noSurfaceText
    Check ((Invoke-Cleanup $inC $outC) -eq 0) 'input without a surface representation exits 0'
    Check ([IO.File]::ReadAllText($outC) -eq [IO.File]::ReadAllText($inC)) `
        'input without a surface representation is copied unchanged'

    # Surface exists but has no relationship: exit 1 and do not overwrite an existing output.
    $noRelationText = @"
ISO-10303-21;
DATA;
#1=MANIFOLD_SURFACE_SHAPE_REPRESENTATION('surface',(#2),#3);
#10=SHAPE_REPRESENTATION_RELATIONSHIP('keep','',#20,#30);
ENDSEC;
END-ISO-10303-21;
"@
    $inD = Join-Path $temp 'no-relation.stp'
    $outD = Join-Path $temp 'no-relation-out.stp'
    Write-Step $inD $noRelationText
    Write-Step $outD 'sentinel'
    Check ((Invoke-Cleanup $inD $outD) -eq 1) 'surface without a matching relationship exits 1'
    Check ([IO.File]::ReadAllText($outD) -eq 'sentinel') `
        'failed cleanup does not overwrite an existing output file'

    # Paths containing spaces must be passed through without breaking native invocation.
    $spaceDir = Join-Path $temp 'dir with spaces'
    New-Item -ItemType Directory -Path $spaceDir | Out-Null
    $inE = Join-Path $spaceDir 'input copy.stp'
    $outE = Join-Path $spaceDir 'output copy.stp'
    Write-Step $inE $surfaceOne
    Check ((Invoke-Cleanup $inE $outE) -eq 0) 'paths containing spaces are handled'
    Check ([IO.File]::ReadAllText($outE).Contains('keep unrelated')) `
        'spaced-path output contains the preserved unrelated entity'

    Write-Host "$count STEP cleanup checks passed."
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
