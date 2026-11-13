#requires -Version 5.1
<#
    VC++ 재배포 런타임(app-local) 파일 해석.

    배포 방식
      vc_redist.x64.exe 를 실행해 시스템에 설치하는 대신, Microsoft 가 재배포를
      허용한 DLL 을 응용 프로그램 폴더에 함께 넣는다. 그러면 관리자 권한과 UAC 가
      필요 없고, 사용자 PC 에 VC 런타임이 이미 설치되어 있는지도 확인할 필요가 없다.

    원본 위치
      <Visual Studio>\VC\Redist\MSVC\<버전>\x64\Microsoft.VC143.CRT
      <Visual Studio>\VC\Redist\MSVC\<버전>\x64\Microsoft.VC143.OpenMP

    이 파일은 파일을 읽기만 한다. 어떤 DLL 이 필요한지는 호출자가 PE 의존 폐포에서
    결정해 넘긴다(여기서 "전부 복사"하지 않는다).
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# CRT/OpenMP 폴더가 실제로 존재하는 가장 최신 재배포 버전을 고른다.
# 폴더 이름만 최신이고 내용이 없는 경우(부분 설치)를 걸러내기 위해 존재 검사를 먼저 한다.
function Find-VcRuntimeSource {
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)][string]$VsInstallPath
    )

    $spec = $Config.vcRuntime
    $redistRoot = Join-Path $VsInstallPath ($spec.redistSubdir -replace '/', '\')
    if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) {
        throw "VC 재배포 폴더가 없습니다: $redistRoot`n  Visual Studio 에 'C++ 를 사용한 데스크톱 개발' 워크로드가 필요합니다."
    }

    $versionDirs = Get-ChildItem -LiteralPath $redistRoot -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0.0.0' } } -Descending

    foreach ($dir in $versionDirs) {
        $crt = Join-Path $dir.FullName ($spec.crtRelative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $crt -PathType Container)) { continue }

        $openMp = Join-Path $dir.FullName ($spec.openMpRelative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $openMp -PathType Container)) { $openMp = $null }

        return [pscustomobject]@{
            Version   = $dir.Name
            Root      = $dir.FullName
            CrtDir    = $crt
            OpenMpDir = $openMp
            Pattern   = [string]$spec.releaseFileNamePattern
        }
    }

    throw "VC 재배포 DLL 폴더를 찾지 못했습니다: $redistRoot\$($spec.crtRelative)"
}

# 요청된 DLL 이름만 재배포 폴더에서 찾아 해시·크기와 함께 돌려준다.
# 이름 규칙(릴리스 이름)에 맞지 않거나 파일이 없으면 조용히 건너뛰지 않고 실패한다.
function Resolve-VcRuntimeFiles {
    param(
        [Parameter(Mandatory)] $Source,
        [Parameter(Mandatory)][string[]]$RequiredNames
    )

    # [regex] 기본값은 대소문자를 구분한다. DLL 이름은 대소문자가 섞여 오므로 무시한다.
    $regex = [regex]::new($Source.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $available = @{}

    foreach ($dir in @($Source.CrtDir, $Source.OpenMpDir)) {
        if (-not $dir) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $dir -File -Filter '*.dll') {
            $key = $file.Name.ToLowerInvariant()
            if ($available.ContainsKey($key)) { continue }
            $available[$key] = $file
        }
    }

    $result = New-Object System.Collections.Generic.List[object]

    foreach ($name in ($RequiredNames | Sort-Object -Unique)) {
        if (-not $regex.IsMatch($name)) {
            throw "VC 재배포 이름 규칙에 맞지 않는 DLL 이 의존 폐포에 있습니다: $name`n  ReleaseConfig.json 의 vcRuntime.releaseFileNamePattern 과 Visual Studio 재배포 내용을 확인하세요."
        }
        $key = $name.ToLowerInvariant()
        if (-not $available.ContainsKey($key)) {
            throw "VC 재배포 폴더에 필요한 DLL 이 없습니다: $name (버전 $($Source.Version))`n  $($Source.CrtDir)"
        }

        $file = $available[$key]
        $component = 'Microsoft.VC143.CRT'
        if ($Source.OpenMpDir -and (Test-IsSubPathOf -Child $file.FullName -Parent $Source.OpenMpDir)) {
            $component = 'Microsoft.VC143.OpenMP'
        }

        $result.Add([pscustomobject]@{
            name      = $file.Name
            component = $component
            source    = $file.FullName
            size      = [int64]$file.Length
            sha256    = Get-Sha256 -Path $file.FullName
        })
    }

    return $result.ToArray()
}

# 배포 폴더에 복사된 VC 런타임 DLL 이 실제로 로드되는지 확인할 때 쓰는 기대 목록.
function Get-VcRuntimeManifestNames {
    param([Parameter(Mandatory)][string]$Folder)

    if (-not (Test-Path -LiteralPath $Folder)) { return @() }
    return @(Get-ChildItem -LiteralPath $Folder -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
}
