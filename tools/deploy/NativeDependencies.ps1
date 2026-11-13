#requires -Version 5.1
<#
    PE(실행 파일/라이브러리) 가져오기 테이블 해석.

    목적
      - 네이티브 DLL 의존 관계를 실제 PE import directory 로 확인한다.
        (프로젝트의 직접 링크 목록이나 과거 테스트 출력 폴더를 근거로 삼지 않는다.)
      - 배포 폴더에 넣을 파일과 Windows/VC 런타임이 제공하는 파일을 구분한다.

    dumpbin.exe 가 없는 환경에서도 동작해야 하므로 PE 구조를 직접 읽는다.
    이 파일은 파일을 읽기만 한다.
#>

function Read-PeBytes {
    param([Parameter(Mandatory)][string]$Path)
    # 쉼표 연산자로 감싸지 않으면 PowerShell 이 byte[] 를 원소 단위로 파이프라인에
    # 풀어 놓아, 수 MB 파일에서 수십 초가 걸린다.
    return ,([System.IO.File]::ReadAllBytes($Path))
}

function Convert-RvaToOffset {
    param(
        [Parameter(Mandatory)]$Sections,
        [Parameter(Mandatory)][int]$Rva,
        [Parameter(Mandatory)][int]$SizeOfHeaders,
        [Parameter(Mandatory)][int]$FileLength
    )

    if ($Rva -lt 0) { return $null }
    if ($Rva -lt $SizeOfHeaders) {
        if ($Rva -lt $FileLength) { return $Rva }
        return $null
    }
    foreach ($s in $Sections) {
        $span = [Math]::Max($s.VirtualSize, $s.SizeOfRawData)
        if ($Rva -ge $s.VirtualAddress -and $Rva -lt ($s.VirtualAddress + $span)) {
            $off = [long]$s.PointerToRawData + ([long]$Rva - [long]$s.VirtualAddress)
            if ($off -lt 0 -or $off -ge $FileLength) { return $null }
            return [int]$off
        }
    }
    return $null
}

function Read-AsciiZ {
    param(
        # [byte[]] 형으로 선언하면 PowerShell 5.1 이 호출마다 배열 전체를 원소 단위로
        # 복사해, 1.7MB 파일에서 호출당 150ms 가 걸린다. 형 선언을 두지 않는다.
        [Parameter(Mandatory)]$Bytes,
        [Parameter(Mandatory)][int]$Offset,
        [int]$MaxLength = 512
    )
    if ($Offset -lt 0 -or $Offset -ge $Bytes.Length) { return $null }
    $limit = [Math]::Min($Bytes.Length, $Offset + $MaxLength)
    $count = $limit - $Offset
    if ($count -le 0) { return $null }
    # [System.Array]::IndexOf 는 PowerShell 5.1 에서 byte[] 를 object[] 로 변환해
    # 호출당 수백 ms 가 걸린다. 512바이트 슬라이스만 문자열로 바꿔 찾는다.
    $slice = [System.Text.Encoding]::ASCII.GetString($Bytes, $Offset, $count)
    $nul = $slice.IndexOf([char]0)
    if ($nul -le 0) { return $null }
    return $slice.Substring(0, $nul)
}

function Get-PeInfo {
    param(
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Cache = $null
    )

    $cacheKey = $Path.ToLowerInvariant()
    if ($null -ne $Cache -and $Cache.ContainsKey($cacheKey)) { return $Cache[$cacheKey] }

    $bytes = Read-PeBytes -Path $Path
    if ($bytes.Length -lt 0x40) { throw "PE 헤더가 너무 짧습니다: $Path" }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "MZ 시그니처가 없습니다: $Path" }

    $lfanew = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($lfanew -le 0 -or ($lfanew + 24) -gt $bytes.Length) { throw "PE 헤더 위치가 잘못되었습니다: $Path" }
    if ($bytes[$lfanew] -ne 0x50 -or $bytes[$lfanew + 1] -ne 0x45) { throw "PE 시그니처가 없습니다: $Path" }

    $machine = [System.BitConverter]::ToUInt16($bytes, $lfanew + 4)
    $numberOfSections = [System.BitConverter]::ToUInt16($bytes, $lfanew + 6)
    $sizeOfOptionalHeader = [System.BitConverter]::ToUInt16($bytes, $lfanew + 20)
    $optionalStart = $lfanew + 24
    $magic = [System.BitConverter]::ToUInt16($bytes, $optionalStart)

    if ($magic -eq 0x20B) { $dataDirStart = $optionalStart + 112; $is64 = $true }
    elseif ($magic -eq 0x10B) { $dataDirStart = $optionalStart + 96; $is64 = $false }
    else { throw "지원하지 않는 PE optional header magic: 0x$('{0:X}' -f $magic) ($Path)" }

    $sizeOfHeaders = [System.BitConverter]::ToInt32($bytes, $optionalStart + 60)
    $sectionStart = $optionalStart + $sizeOfOptionalHeader

    $sections = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $numberOfSections; $i++) {
        $off = $sectionStart + ($i * 40)
        if ($off + 40 -gt $bytes.Length) { break }
        $sections.Add([pscustomobject]@{
            Name            = [System.Text.Encoding]::ASCII.GetString($bytes, $off, 8).TrimEnd([char]0)
            VirtualSize     = [System.BitConverter]::ToUInt32($bytes, $off + 8)
            VirtualAddress  = [System.BitConverter]::ToUInt32($bytes, $off + 12)
            SizeOfRawData   = [System.BitConverter]::ToUInt32($bytes, $off + 16)
            PointerToRawData= [System.BitConverter]::ToUInt32($bytes, $off + 20)
        })
    }

    $imports = New-Object System.Collections.Generic.List[string]
    $importRva = 0
    if (($dataDirStart + 16) -le $bytes.Length) {
        $importRva = [System.BitConverter]::ToInt32($bytes, $dataDirStart + 8)
    }

    if ($importRva -gt 0) {
        $importOff = Convert-RvaToOffset -Sections $sections -Rva $importRva -SizeOfHeaders $sizeOfHeaders -FileLength $bytes.Length
        if ($null -ne $importOff) {
            # import 디렉터리가 속한 섹션의 raw 범위를 넘어가면 더 읽지 않는다.
            # 종료자(20바이트 전부 0)를 못 만나면 파일 끝까지 20바이트씩 도는 사고가 난다.
            $limit = $bytes.Length
            foreach ($s in $sections) {
                $rawStart = [long]$s.PointerToRawData
                $rawEnd = $rawStart + [long]$s.SizeOfRawData
                if ($importOff -ge $rawStart -and $importOff -lt $rawEnd) {
                    $limit = [int][Math]::Min([long]$bytes.Length, $rawEnd)
                    break
                }
            }

            $maxDescriptors = 4096
            $cursor = [int]$importOff
            $count = 0
            while ((($cursor + 20) -le $limit) -and ($count -lt $maxDescriptors)) {
                $nameRva = [System.BitConverter]::ToInt32($bytes, $cursor + 12)
                $firstThunk = [System.BitConverter]::ToInt32($bytes, $cursor + 16)
                if ($nameRva -eq 0 -and $firstThunk -eq 0) { break }
                if ($nameRva -gt 0) {
                    $nameOff = Convert-RvaToOffset -Sections $sections -Rva $nameRva -SizeOfHeaders $sizeOfHeaders -FileLength $bytes.Length
                    if ($null -ne $nameOff) {
                        $name = Read-AsciiZ -Bytes $bytes -Offset ([int]$nameOff)
                        if ($name) { $imports.Add($name) }
                    }
                }
                $cursor += 20
                $count++
            }
        }
    }

    $machineName = switch ($machine) {
        0x8664 { 'AMD64' }
        0x14C  { 'I386' }
        0xAA64 { 'ARM64' }
        0x1C4  { 'ARMNT' }
        default { ('0x{0:X4}' -f $machine) }
    }

    $result = [pscustomobject]@{
        Path        = $Path
        Machine     = $machine
        MachineName = $machineName
        Is64Bit     = $is64
        Imports     = @($imports | Select-Object -Unique)
    }
    if ($null -ne $Cache) { $Cache[$cacheKey] = $result }
    return $result
}

# VC 런타임 계열은 vc_redist 설치 또는 앱 로컬 복사로 제공된다.
# 검색 폴더에 같은 이름이 있어도 앱/OCCT DLL 로 취급하지 않는다.
function Test-VcRuntimeName {
    param([Parameter(Mandatory)][string]$Name)

    $n = $Name.ToLowerInvariant()
    if ($n -match '^msvcp140') { return $true }
    if ($n -match '^vcruntime140') { return $true }
    if ($n -eq 'concrt140.dll' -or $n -eq 'concrt140d.dll') { return $true }
    if ($n -eq 'vcomp140.dll' -or $n -eq 'vcomp140d.dll') { return $true }
    return $false
}

$script:SystemBinaryCache = @{}

function Test-SystemBinaryName {
    param([Parameter(Mandatory)][string]$Name)

    $n = $Name.ToLowerInvariant()
    if ($n.StartsWith('api-ms-win-')) { return $true }
    if ($n.StartsWith('ext-ms-')) { return $true }
    if ($script:SystemBinaryCache.ContainsKey($n)) { return $script:SystemBinaryCache[$n] }

    $win = [Environment]::GetFolderPath('Windows')
    if (-not $win) { $win = $env:SystemRoot }

    $found = $false
    if ($win) {
        foreach ($sub in @('System32', 'SysWOW64')) {
            $p = Join-Path (Join-Path $win $sub) $Name
            if (Test-Path -LiteralPath $p) { $found = $true; break }
        }
    }
    $script:SystemBinaryCache[$n] = $found
    return $found
}

function New-NativeIndex {
    param([Parameter(Mandatory)][string[]]$SearchRoots)

    $index = @{}
    foreach ($root in $SearchRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        # 검색 폴더는 재귀하지 않는다. 배포는 평탄 배치이며, 다른 하위 폴더의
        # 동일 파일명 DLL 을 조용히 섞지 않기 위함이다.
        foreach ($f in Get-ChildItem -LiteralPath $root -File -Filter *.dll) {
            $key = $f.Name.ToLowerInvariant()
            if ($index.ContainsKey($key)) {
                $existing = $index[$key]
                $h1 = (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash
                $h2 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
                if ($h1 -ne $h2) {
                    throw "같은 파일명의 DLL 이 서로 다른 내용으로 두 곳에 있습니다: $($f.Name)`n  $existing`n  $($f.FullName)"
                }
                continue
            }
            $index[$key] = $f.FullName
        }
    }
    return $index
}

function Resolve-NativeDependencyClosure {
    param(
        [Parameter(Mandatory)][string[]]$SeedPaths,
        [Parameter(Mandatory)][string[]]$SearchRoots
    )

    $index = New-NativeIndex -SearchRoots $SearchRoots
    $appDlls = [ordered]@{}
    $vcRuntime = [ordered]@{}
    $system = [ordered]@{}
    $unresolved = [ordered]@{}
    $machineByName = @{}
    $peCache = @{}

    $queue = New-Object System.Collections.Queue
    $visited = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $seeds = @()

    foreach ($seed in $SeedPaths) {
        if (-not (Test-Path -LiteralPath $seed)) {
            throw "기준 바이너리가 없습니다: $seed`n  네이티브를 먼저 빌드해야 합니다(OutDir=x64\Release)."
        }
        $info = Get-PeInfo -Path $seed -Cache $peCache
        $machineByName[(Split-Path -Leaf $seed)] = $info.MachineName
        $seeds += [pscustomobject]@{
            name     = Split-Path -Leaf $seed
            sha256   = Get-Sha256 -Path $seed
            size     = (Get-Item -LiteralPath $seed).Length
            machine  = $info.MachineName
        }
        $queue.Enqueue([pscustomobject]@{ Name = (Split-Path -Leaf $seed); Path = $seed })
    }

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        if (-not $visited.Add($current.Path)) { continue }

        $info = Get-PeInfo -Path $current.Path -Cache $peCache
        $machineByName[$current.Name] = $info.MachineName

        foreach ($importName in $info.Imports) {
            $key = $importName.ToLowerInvariant()

            if (Test-VcRuntimeName -Name $importName) {
                if (-not $vcRuntime.Contains($key)) {
                    $vcRuntime[$key] = [pscustomobject]@{
                        name       = $importName
                        importedBy = New-Object System.Collections.Generic.List[string]
                    }
                }
                if (-not $vcRuntime[$key].importedBy.Contains($current.Name)) {
                    $vcRuntime[$key].importedBy.Add($current.Name)
                }
                continue
            }

            if ($appDlls.Contains($key) -or ($index.ContainsKey($key))) {
                if (-not $appDlls.Contains($key)) {
                    $src = $index[$key]
                    $appDlls[$key] = [pscustomobject]@{
                        name       = $importName
                        source     = $src
                        sha256     = Get-Sha256 -Path $src
                        size       = (Get-Item -LiteralPath $src).Length
                        importedBy = New-Object System.Collections.Generic.List[string]
                    }
                    $queue.Enqueue([pscustomobject]@{ Name = $importName; Path = $src })
                }
                if (-not $appDlls[$key].importedBy.Contains($current.Name)) {
                    $appDlls[$key].importedBy.Add($current.Name)
                }
                continue
            }

            if (Test-SystemBinaryName -Name $importName) {
                if (-not $system.Contains($key)) {
                    $system[$key] = [pscustomobject]@{
                        name       = $importName
                        importedBy = New-Object System.Collections.Generic.List[string]
                    }
                }
                if (-not $system[$key].importedBy.Contains($current.Name)) {
                    $system[$key].importedBy.Add($current.Name)
                }
                continue
            }

            if (-not $unresolved.Contains($key)) {
                $unresolved[$key] = [pscustomobject]@{
                    name       = $importName
                    importedBy = New-Object System.Collections.Generic.List[string]
                }
            }
            if (-not $unresolved[$key].importedBy.Contains($current.Name)) {
                $unresolved[$key].importedBy.Add($current.Name)
            }
        }
    }

    return [pscustomobject]@{
        seeds          = $seeds
        appDlls        = @($appDlls.Values)
        vcRuntimeDlls  = @($vcRuntime.Values)
        systemDlls     = @($system.Values)
        unresolvedDlls = @($unresolved.Values)
    }
}

# 배포 폴더 하나를 검사한다. 폴더 안 모든 PE 파일의 비시스템 import 가
# 같은 폴더에 있는지 확인한다. 네이티브 DLL 누락(0x8007007E) 사고를 배포 전에 잡는다.
function Test-PackageDependencyFolder {
    param(
        [Parameter(Mandatory)][string]$Folder,
        [string[]]$ExtraProvidedNames = @()
    )

    $present = @{}
    foreach ($f in Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.Extension -in @('.dll', '.exe') }) {
        $present[$f.Name.ToLowerInvariant()] = $f.FullName
    }
    foreach ($n in $ExtraProvidedNames) { $present[$n.ToLowerInvariant()] = '<provided>' }

    $missing = @()
    $checked = 0
    $peCache = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.Extension -in @('.dll', '.exe') }) {
        $isPe = $true
        $info = $null
        try { $info = Get-PeInfo -Path $file.FullName -Cache $peCache } catch { $isPe = $false }
        if (-not $isPe -or $null -eq $info) { continue }
        $checked++
        foreach ($importName in $info.Imports) {
            $key = $importName.ToLowerInvariant()
            if ($present.ContainsKey($key)) { continue }
            if (Test-SystemBinaryName -Name $importName) { continue }
            $missing += [pscustomobject]@{ file = $file.Name; missing = $importName }
        }
    }

    return [pscustomobject]@{
        checkedFiles = $checked
        missing      = $missing
    }
}
