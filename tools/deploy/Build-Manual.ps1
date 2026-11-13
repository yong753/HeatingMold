#Requires -Version 5.1
<#
.SYNOPSIS
    촬영한 실제 배포본 화면으로 사용 설명서(HTML + PDF)를 만든다.

.DESCRIPTION
    artefacts\manual-capture\<시각>\captures.json 이 가리키는 원본 PNG 를 읽어
    tools\deploy\assets\Manual\screenshots.json 의 정의대로 개인 환경 노출 영역을 가리고
    images\*.png 로 저장한 뒤, index.template.html 로 오프라인 HTML 문서를 만든다.
    PDF 는 Microsoft Edge 의 headless 인쇄를 사용하며, 실패하면 성공으로 보고하지 않고 경고만 남긴다.

.PARAMETER CaptureDirectory
    캡처 폴더. 생략하면 artefacts\manual-capture 아래에서 captures.json 이 있는 가장 최근 폴더를 쓴다.

.PARAMETER OutDir
    결과 폴더. 생략하면 artefacts\manual\<yyyyMMdd-HHmmss>.

.PARAMETER NoPdf
    PDF 생성을 건너뛴다.
#>
param(
    [string]$CaptureDirectory,
    [string]$OutDir,
    [string]$PackageVersion,
    [switch]$NoPdf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Common.ps1')

$repo = Get-RepoRoot
$deployDir = Get-DeployDir
$assetsDir = Join-Path $deployDir 'assets\Manual'

function Find-CaptureDirectory {
    param([string]$Requested)

    if ($Requested) {
        $path = [IO.Path]::GetFullPath($Requested)
        if (-not (Test-Path (Join-Path $path 'captures.json'))) {
            throw "캡처 폴더에 captures.json 이 없습니다: $path"
        }
        return $path
    }

    $root = Join-Path $repo 'artifacts\manual-capture'
    if (-not (Test-Path $root)) {
        throw "캡처 폴더가 없습니다: $root (Capture-Manual.ps1 을 먼저 실행하세요)"
    }

    $candidates = @(Get-ChildItem -Path $root -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'captures.json') } |
        Sort-Object Name -Descending)
    if ($candidates.Count -eq 0) {
        throw "captures.json 이 있는 캡처 폴더를 찾지 못했습니다: $root"
    }
    return $candidates[0].FullName
}

function Get-PackageVersionFromCapture {
    param($Capture)

    if ($PackageVersion) { return $PackageVersion }

    $packageRoot = [string]$Capture.packageRoot
    if ($packageRoot) {
        $info = Join-Path $packageRoot 'build-info.json'
        if (Test-Path $info) {
            $build = Read-JsonFile -Path $info
            $names = $build.PSObject.Properties.Name
            if (($names -contains 'version') -and $build.version) {
                return [string]$build.version
            }
            if (($names -contains 'packageVersion') -and $build.packageVersion) {
                return [string]$build.packageVersion
            }
        }
    }
    return 'unknown'
}

function New-MaskedImage {
    param(
        [string]$SourcePath,
        [string]$TargetPath,
        [object[]]$Masks
    )

    Add-Type -AssemblyName System.Drawing
    $source = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($source.Width, $source.Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.DrawImage($source, 0, 0, $source.Width, $source.Height)
                foreach ($mask in $Masks) {
                    $color = [System.Drawing.ColorTranslator]::FromHtml([string]$mask.color)
                    $brush = New-Object System.Drawing.SolidBrush($color)
                    try {
                        $graphics.FillRectangle($brush, [int]$mask.x, [int]$mask.y, [int]$mask.width, [int]$mask.height)
                    }
                    finally {
                        $brush.Dispose()
                    }
                }
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($TargetPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Invoke-EdgePdf {
    param(
        [string]$HtmlPath,
        [string]$PdfPath
    )

    $edgeCandidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
    ) | Where-Object { $_ -and (Test-Path $_) })

    if ($edgeCandidates.Count -eq 0) {
        Write-Warn 'Microsoft Edge 를 찾지 못해 PDF 를 만들지 않았습니다. HTML 은 생성되었습니다.'
        return $null
    }

    $edge = $edgeCandidates[0]
    if (Test-Path $PdfPath) { Remove-Item -LiteralPath $PdfPath -Force }

    $uri = ([Uri]$HtmlPath).AbsoluteUri
    $arguments = @(
        '--headless=new',
        '--disable-gpu',
        '--no-pdf-header-footer',
        '--virtual-time-budget=15000',
        "--print-to-pdf=$PdfPath",
        $uri
    )

    Write-Detail "Edge headless 인쇄: $edge"
    $process = Start-Process -FilePath $edge -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        Write-Warn "Edge 인쇄가 종료 코드 $($process.ExitCode) 로 끝났습니다."
    }
    if (-not (Test-Path $PdfPath)) {
        Write-Warn 'PDF 파일이 만들어지지 않았습니다. HTML 만 배포하거나 PDF 변환 도구를 따로 쓰십시오.'
        return $null
    }

    $length = (Get-Item -LiteralPath $PdfPath).Length
    $header = [System.IO.File]::ReadAllBytes($PdfPath)
    $isPdf = $header.Length -gt 5 -and
        $header[0] -eq 0x25 -and $header[1] -eq 0x50 -and $header[2] -eq 0x44 -and $header[3] -eq 0x46
    if (-not $isPdf) {
        Write-Warn '만들어진 파일이 PDF 형식이 아닙니다.'
        return $null
    }
    if ($length -lt 100000) {
        Write-Warn ("PDF 크기가 {0} 로 작습니다. 그림이 빠졌는지 직접 열어 확인하십시오." -f (Format-Size -Bytes $length))
    }

    return [pscustomobject]@{ Path = $PdfPath; Size = $length }
}

# ---------------------------------------------------------------- 본문

Write-Step '사용 설명서 생성'

$captureDir = Find-CaptureDirectory -Requested $CaptureDirectory
Write-Detail "캡처 폴더: $captureDir"

$capture = Read-JsonFile -Path (Join-Path $captureDir 'captures.json')
$definitions = Read-JsonFile -Path (Join-Path $assetsDir 'screenshots.json')
$templatePath = Join-Path $assetsDir 'index.template.html'
$cssPath = Join-Path $assetsDir 'manual.css'

foreach ($required in @($templatePath, $cssPath)) {
    if (-not (Test-Path $required)) { Stop-Deploy "매뉴얼 자산이 없습니다: $required" }
}

$version = Get-PackageVersionFromCapture -Capture $capture
Write-Detail "패키지 버전: $version"

if (-not $OutDir) {
    $OutDir = Join-Path $repo ('artifacts\manual\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
$manualDir = Join-Path $OutDir 'manual'
$imagesDir = Join-Path $manualDir 'images'

New-CleanDirectory -Path $manualDir
New-Item -ItemType Directory -Force -Path $imagesDir | Out-Null

$defaultMasks = @()
if ($definitions.PSObject.Properties.Name -contains 'defaultMasks') {
    $defaultMasks = @($definitions.defaultMasks)
}

$figureRecords = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList

foreach ($figure in $definitions.figures) {
    $sourcePath = Join-Path $captureDir ('raw\' + [string]$figure.source)
    if (-not (Test-Path $sourcePath)) {
        [void]$missing.Add([string]$figure.source)
        Write-Warn "원본 화면이 없습니다: $($figure.source)"
        continue
    }

    $masks = @($defaultMasks)
    if ($figure.PSObject.Properties.Name -contains 'masks' -and $figure.masks) {
        $masks = $masks + @($figure.masks)
    }

    $targetPath = Join-Path $imagesDir ([string]$figure.output)
    New-MaskedImage -SourcePath $sourcePath -TargetPath $targetPath -Masks $masks

    [void]$figureRecords.Add([pscustomobject]@{
        output  = [string]$figure.output
        source  = [string]$figure.source
        caption = [string]$figure.caption
        masks   = $masks.Count
        size    = (Get-Item -LiteralPath $targetPath).Length
    })
}

if ($missing.Count -gt 0) {
    Stop-Deploy ("매뉴얼에 필요한 화면 {0}개를 찾지 못했습니다: {1}" -f $missing.Count, ($missing -join ', '))
}

Write-Ok ("화면 {0}개를 가공했습니다." -f $figureRecords.Count)

Copy-Item -LiteralPath $cssPath -Destination (Join-Path $manualDir 'manual.css') -Force

$built = (Get-Date).ToString('yyyy-MM-dd HH:mm')
$html = [System.IO.File]::ReadAllText($templatePath, [System.Text.Encoding]::UTF8)
$title = 'HeatingMold 테스트 배포판 사용 설명서'
$html = $html.Replace('{{TITLE}}', $title)
$html = $html.Replace('{{VERSION}}', $version)
$html = $html.Replace('{{BUILT}}', $built)
Write-Utf8File -Path (Join-Path $manualDir 'index.html') -Content $html

$captureInfo = [ordered]@{
    schemaVersion      = 1
    generatedUtc       = (Get-Date).ToUniversalTime().ToString('o')
    title              = $title
    packageVersion     = $version
    captureDirectory   = $captureDir
    captureRunDir      = [string]$capture.runDirectory
    capturePackageRoot = [string]$capture.packageRoot
    capturedAtUtc      = [string]$capture.capturedAtUtc
    scenario           = [string]$capture.scenario
    holdSeconds        = $capture.holdSeconds
    images             = @($figureRecords)
    notes              = @(
        '그림은 실제 배포본 실행 화면이며, 파일 경로와 PC 알림 문구만 가렸다.',
        '화면의 수치는 예제 모델 기준이다.',
        'PDF 는 Microsoft Edge headless 인쇄로 만들며, 실패하면 HTML 만 남는다.'
    )
}
Write-JsonFile -Path (Join-Path $OutDir 'capture-info.json') -Value $captureInfo -Depth 6

Write-Step 'PDF 생성'
$pdf = $null
if ($NoPdf) {
    Write-Detail '-NoPdf 로 건너뜁니다.'
}
else {
    $pdf = Invoke-EdgePdf -HtmlPath (Join-Path $manualDir 'index.html') -PdfPath (Join-Path $manualDir 'HeatingMold_사용설명서.pdf')
    if ($pdf) {
        Write-Ok ("PDF: {0}" -f (Format-Size -Bytes $pdf.Size))
    }
}

$report = [ordered]@{
    generatedUtc   = (Get-Date).ToUniversalTime().ToString('o')
    captureDir     = $captureDir
    manualDir      = $manualDir
    packageVersion = $version
    imageCount     = $figureRecords.Count
    pdfPath        = if ($pdf) { $pdf.Path } else { $null }
    pdfSize        = if ($pdf) { $pdf.Size } else { 0 }
}

Write-Step '결과'
Write-Detail "HTML: $(Join-Path $manualDir 'index.html')"
Write-Detail "이미지: $imagesDir ($($figureRecords.Count)개)"
if ($pdf) {
    Write-Detail "PDF: $($pdf.Path)"
}
else {
    Write-Warn 'PDF 없음 — HTML 만 사용 가능합니다.'
}
Write-Detail "기록: $(Join-Path $OutDir 'capture-info.json')"

[pscustomobject]@{
    OutDir     = $OutDir
    ManualDir  = $manualDir
    ImageCount = $figureRecords.Count
    PdfPath    = if ($pdf) { $pdf.Path } else { $null }
    Version    = $version
}
