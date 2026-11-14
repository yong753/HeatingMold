#requires -Version 5.1
<#
.SYNOPSIS
    Validation guard 회귀용 fixture exe를 컴파일하고 경로를 돌려준다.

.DESCRIPTION
    %LOCALAPPDATA%\HeatingMold\ValidationGuard\fixtures\<source hash>\FixtureHost.exe 에 캐시한다.
    소스가 바뀌면 새 폴더에 다시 컴파일한다.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'FixtureHost.cs'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "FixtureHost.cs not found: $sourcePath"
}

$hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$outDir = Join-Path $env:LOCALAPPDATA ("HeatingMold\ValidationGuard\fixtures\" + $hash)
$exePath = Join-Path $outDir 'FixtureHost.exe'

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
    Add-Type -AssemblyName PresentationCore -ErrorAction Stop
    Add-Type -AssemblyName WindowsBase -ErrorAction Stop
    Add-Type -AssemblyName System.Xaml -ErrorAction Stop

    $references = @(
        [System.Windows.Window].Assembly.Location,
        [System.Windows.Media.Brush].Assembly.Location,
        [System.Windows.DependencyObject].Assembly.Location,
        [System.Xaml.XamlReader].Assembly.Location,
        'System.Windows.Forms.dll',
        'System.Drawing.dll'
    )
    $source = [IO.File]::ReadAllText($sourcePath, [Text.Encoding]::UTF8)
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references `
        -OutputAssembly $exePath -OutputType WindowsApplication -ErrorAction Stop
}

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Fixture compilation did not produce $exePath"
}

$exePath
