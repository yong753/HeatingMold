[CmdletBinding()]
param(
    [ValidateSet('All', 'Managed', 'Core', 'Design', 'Electrical', 'Application', 'Infrastructure', 'App', 'Native', 'WorkingSide', 'PowerLead')]
    [string]$Group = 'All',

    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [string]$MSBuildPath,

    [string]$VcVars64Path,

    [string]$OcctRoot,

    [string]$ThirdPartyRoot,

    [string]$Geo00MoldPath
)

$ErrorActionPreference = 'Stop'
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedGeo00MoldPath = $null
if (-not [string]::IsNullOrWhiteSpace($Geo00MoldPath)) {
    if ($Group -ne 'WorkingSide' -and $Group -ne 'All') {
        throw '-Geo00MoldPath는 -Group WorkingSide 또는 All과 함께 사용합니다.'
    }
    if (-not (Test-Path -LiteralPath $Geo00MoldPath -PathType Leaf)) {
        throw "GEO-00 STEP 파일을 찾을 수 없습니다: $Geo00MoldPath"
    }
    $resolvedGeo00MoldPath = (Resolve-Path -LiteralPath $Geo00MoldPath).Path
}

$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$verificationRoot = Join-Path $repositoryRoot "x64/Verification/$runId"
$ResolvedOcctRoot = if ([string]::IsNullOrWhiteSpace($OcctRoot)) {
    Join-Path $repositoryRoot 'third_party/opencascade-7.9.3-vc14-64'
} else {
    (Resolve-Path $OcctRoot -ErrorAction SilentlyContinue).Path
}
$ResolvedThirdPartyRoot = if ([string]::IsNullOrWhiteSpace($ThirdPartyRoot)) {
    Join-Path $repositoryRoot 'third_party/3rdparty-vc14-64'
} else {
    (Resolve-Path $ThirdPartyRoot -ErrorAction SilentlyContinue).Path
}

$paths = @{
    Native  = Join-Path $verificationRoot 'native'
    App     = Join-Path $verificationRoot 'app'
    Tests   = Join-Path $verificationRoot 'tests'
    Fixtures = Join-Path $verificationRoot 'fixtures'
    Logs    = Join-Path $verificationRoot 'logs'
}
$verificationIntermediateRoot = Join-Path $verificationRoot 'intermediate'

foreach ($path in $paths.Values) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
    $env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'HeatingMold.DotnetCliHome'
}

if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget/packages'
}

function Write-RunLog {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Message
    )

    $logPath = Join-Path $paths.Logs $Name
    Add-Content -Path $logPath -Value $Message -Encoding utf8
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [string]$LogName,
        [Parameter(Mandatory)] [string]$Description
    )

    $logPath = Join-Path $paths.Logs $LogName
    $output = & $FilePath @ArgumentList 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { $_.ToString() } | Set-Content -Path $logPath -Encoding utf8

    if ($exitCode -ne 0) {
        Get-Content -Path $logPath -Tail 50 | ForEach-Object { Write-Host $_ }
        throw "$Description 실패 (종료 코드: $exitCode, 로그: $logPath)"
    }

    Write-Host "$Description 완료. 로그: $logPath"
}

function Invoke-XmlSummaryLanguageCheck {
    $checkerPath = Join-Path $repositoryRoot 'scripts/Check-XmlSummaries.ps1'
    $powerShellExecutable = if ($PSVersionTable.PSEdition -eq 'Core') {
        Join-Path $PSHOME 'pwsh.exe'
    } else {
        Join-Path $PSHOME 'powershell.exe'
    }

    if (-not (Test-Path -LiteralPath $powerShellExecutable -PathType Leaf)) {
        throw "PowerShell 실행 파일을 찾을 수 없습니다: $powerShellExecutable"
    }

    Invoke-LoggedCommand -FilePath $powerShellExecutable -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $checkerPath, '-Root', $repositoryRoot
    ) -LogName 'xml-summary-language.log' -Description 'XML summary 한글 검사'
}

function Find-VisualStudioRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:VSINSTALLDIR) -and (Test-Path $env:VSINSTALLDIR)) {
        return (Resolve-Path $env:VSINSTALLDIR).Path
    }

    $vswhereCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio/Installer/vswhere.exe')
    ) | Select-Object -Unique

    foreach ($vswhere in $vswhereCandidates) {
        if (-not (Test-Path $vswhere)) {
            continue
        }

        $installPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installPath)) {
            return $installPath.Trim()
        }
    }

    return $null
}

function Resolve-MSBuildPath {
    if (-not [string]::IsNullOrWhiteSpace($MSBuildPath)) {
        if (-not (Test-Path $MSBuildPath -PathType Leaf)) {
            throw "지정한 MSBuild.exe를 찾을 수 없습니다: $MSBuildPath"
        }

        return (Resolve-Path $MSBuildPath).Path
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $visualStudioRoot = Find-VisualStudioRoot
    if ($null -ne $visualStudioRoot) {
        $candidate = Join-Path $visualStudioRoot 'MSBuild/Current/Bin/MSBuild.exe'
        if (Test-Path $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'MSBuild.exe를 찾을 수 없습니다. -MSBuildPath로 C++/CLI 지원 Visual Studio의 경로를 지정하세요.'
}

function Resolve-VcVars64Path {
    if (-not [string]::IsNullOrWhiteSpace($VcVars64Path)) {
        if (-not (Test-Path $VcVars64Path -PathType Leaf)) {
            throw "지정한 vcvars64.bat을 찾을 수 없습니다: $VcVars64Path"
        }

        return (Resolve-Path $VcVars64Path).Path
    }

    $visualStudioRoot = Find-VisualStudioRoot
    if ($null -ne $visualStudioRoot) {
        $candidate = Join-Path $visualStudioRoot 'VC/Auxiliary/Build/vcvars64.bat'
        if (Test-Path $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'vcvars64.bat을 찾을 수 없습니다. -VcVars64Path로 C++ 빌드 도구 경로를 지정하세요.'
}

function Resolve-NativePrerequisites {
    $script:MSBuildPath = Resolve-MSBuildPath
    $script:VcVars64Path = Resolve-VcVars64Path

    $requiredPaths = @(
        (Join-Path $ResolvedOcctRoot 'inc'),
        (Join-Path $ResolvedOcctRoot 'win64/vc14/lib'),
        (Join-Path $ResolvedOcctRoot 'win64/vc14/bin'),
        $ResolvedThirdPartyRoot
    )

    $missingPaths = @($requiredPaths | Where-Object { -not (Test-Path $_) })
    if ($missingPaths.Count -gt 0) {
        throw "Native 빌드 의존 경로가 없습니다:`n$($missingPaths -join "`n")"
    }

    return Get-ChildItem -Path $ResolvedThirdPartyRoot -Recurse -Filter '*.dll' -File |
        ForEach-Object { $_.DirectoryName } |
        Select-Object -Unique
}

function Invoke-CoreTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.Core.Tests/HeatingMold.Core.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'core-restore.log' -Description 'Core 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'core-tests.log' -Description 'Core 테스트'
}

function Invoke-DesignTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.Design.Tests/HeatingMold.Design.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'design-restore.log' -Description 'Design 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'design-tests.log' -Description 'Design 테스트'
}

function Invoke-ElectricalTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.Electrical.Tests/HeatingMold.Electrical.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'electrical-restore.log' -Description 'Electrical 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'electrical-tests.log' -Description 'Electrical 테스트'
}

function Invoke-ApplicationTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.Application.Tests/HeatingMold.Application.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'application-restore.log' -Description 'Application 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'application-tests.log' -Description 'Application 테스트'
}

function Invoke-InfrastructureTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.Infrastructure.Tests/HeatingMold.Infrastructure.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'infrastructure-restore.log' -Description 'Infrastructure 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'infrastructure-tests.log' -Description 'Infrastructure 테스트'
}

function Invoke-AppTests {
    $project = Join-Path $repositoryRoot 'tests/HeatingMold.App.Tests/HeatingMold.App.Tests.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'app-tests-restore.log' -Description 'App 테스트 패키지 복원'

    $testArguments = @(
        'test', $project, '--configuration', $Configuration, '--no-restore',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $testArguments `
        -LogName 'app-tests.log' -Description 'App 테스트'
}

function Build-NativeBridge {
    if ($Configuration -ne 'Release') {
        throw 'OccBridge는 현재 Release 구성만 지원합니다.'
    }

    $null = Resolve-NativePrerequisites
    $nativeOutput = (Join-Path $paths.Native '') + [IO.Path]::DirectorySeparatorChar
    $nativeIntermediate = (Join-Path $paths.Native 'obj') + [IO.Path]::DirectorySeparatorChar
    $project = Join-Path $repositoryRoot 'src/OccBridge/OccBridge.vcxproj'
    $arguments = @(
        $project,
        '/p:Configuration=Release',
        "/p:Platform=$Platform",
        "/p:OutDir=$nativeOutput",
        "/p:IntDir=$nativeIntermediate",
        "/p:OcctRoot=$ResolvedOcctRoot",
        '/v:minimal'
    )
    Invoke-LoggedCommand -FilePath $MSBuildPath -ArgumentList $arguments `
        -LogName 'native-build.log' -Description 'OccBridge 격리 빌드'

    $bridgePath = Join-Path $nativeOutput 'OccBridge.dll'
    if (-not (Test-Path $bridgePath -PathType Leaf)) {
        throw "빌드 산출물에 OccBridge.dll이 없습니다: $bridgePath"
    }

    $hash = (Get-FileHash -Path $bridgePath -Algorithm SHA256).Hash
    Write-RunLog -Name 'native-bridge.sha256.txt' -Message "$hash  $bridgePath"
    return $bridgePath
}

function Build-App {
    param([Parameter(Mandatory)] [string]$BridgeAssemblyPath)

    $project = Join-Path $repositoryRoot 'src/HeatingMold.App/HeatingMold.App.csproj'
    $restoreArguments = @(
        'restore', $project, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName 'app-restore.log' -Description 'App 패키지 복원'

    $arguments = @(
        'build', $project, '--configuration', $Configuration, '--no-restore',
        '--output', $paths.App,
        "/p:NativeBridgeAssembly=$BridgeAssemblyPath",
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        "/p:Platform=$Platform",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $arguments `
        -LogName 'app-build.log' -Description 'App 격리 빌드'

    $copiedBridgePath = Join-Path $paths.App 'OccBridge.dll'
    Assert-SameFileHash -SourcePath $BridgeAssemblyPath -CopiedPath $copiedBridgePath
    return $copiedBridgePath
}

function Assert-SameFileHash {
    param(
        [Parameter(Mandatory)] [string]$SourcePath,
        [Parameter(Mandatory)] [string]$CopiedPath
    )

    if (-not (Test-Path $CopiedPath -PathType Leaf)) {
        throw "빌드 출력에 브리지 DLL이 복사되지 않았습니다: $CopiedPath"
    }

    $sourceHash = (Get-FileHash -Path $SourcePath -Algorithm SHA256).Hash
    $copiedHash = (Get-FileHash -Path $CopiedPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $copiedHash) {
        throw "빌드한 브리지와 복사된 브리지의 해시가 다릅니다: $CopiedPath"
    }

    Write-RunLog -Name 'native-bridge.sha256.txt' -Message "$copiedHash  $CopiedPath"
}

function Invoke-RegressionHarness {
    param(
        [Parameter(Mandatory)] [ValidateSet('WorkingSide', 'PowerLead')] [string]$Harness,
        [Parameter(Mandatory)] [string]$BridgeAssemblyPath
    )

    $harnessDirectory = Join-Path $repositoryRoot "tests/$Harness.Regression"
    $harnessProject = Join-Path $harnessDirectory "$Harness.Regression.csproj"
    $testOutput = Join-Path $paths.Tests $Harness
    $fixtureOutput = Join-Path $paths.Fixtures $Harness
    New-Item -ItemType Directory -Path $testOutput, $fixtureOutput -Force | Out-Null

    $appAssemblyPath = Join-Path $paths.App 'HeatingMold.App.dll'
    $coreAssemblyPath = Join-Path $paths.App 'HeatingMold.Core.dll'
    $designAssemblyPath = Join-Path $paths.App 'HeatingMold.Design.dll'
    $applicationAssemblyPath = Join-Path $paths.App 'HeatingMold.Application.dll'
    $ijwHostPath = Join-Path $paths.App 'ijwhost.dll'
    $restoreArguments = @(
        'restore', $harnessProject, '--ignore-failed-sources',
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        '-p:NuGetAudit=false')
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $restoreArguments `
        -LogName "$Harness-restore.log" -Description "$Harness harness 패키지 복원"

    $buildArguments = @(
        'build', $harnessProject, '--configuration', $Configuration, '--no-restore',
        '--output', $testOutput,
        "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
        "/p:BridgeAssemblyPath=$BridgeAssemblyPath",
        "/p:AppAssemblyPath=$appAssemblyPath",
        "/p:CoreAssemblyPath=$coreAssemblyPath",
        "/p:DesignAssemblyPath=$designAssemblyPath",
        "/p:ApplicationAssemblyPath=$applicationAssemblyPath",
        "/p:IjwHostPath=$ijwHostPath",
        '-p:NuGetAudit=false'
    )
    Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $buildArguments `
        -LogName "$Harness-build.log" -Description "$Harness harness 격리 빌드"

    Assert-SameFileHash -SourcePath $BridgeAssemblyPath -CopiedPath (Join-Path $testOutput 'OccBridge.dll')

    $fixtureSource = Join-Path $harnessDirectory 'Fixtures.cpp'
    $fixtureExecutable = Join-Path $fixtureOutput 'Fixtures.exe'
    $fixtureObject = Join-Path $fixtureOutput 'Fixtures.obj'
    $occtInclude = Join-Path $ResolvedOcctRoot 'inc'
    $occtLibrary = Join-Path $ResolvedOcctRoot 'win64/vc14/lib'
    $libraries = if ($Harness -eq 'WorkingSide') {
        'TKernel.lib TKMath.lib TKG2d.lib TKG3d.lib TKGeomBase.lib TKBRep.lib TKTopAlgo.lib TKPrim.lib TKDESTEP.lib TKXSBase.lib'
    }
    else {
        'TKernel.lib TKMath.lib TKG2d.lib TKG3d.lib TKGeomBase.lib TKBRep.lib TKTopAlgo.lib TKDESTEP.lib TKXSBase.lib'
    }

    $compileScript = Join-Path $fixtureOutput 'compile-fixture.cmd'
    $compileLines = @(
        '@echo off',
        ('call "{0}" >nul' -f $VcVars64Path),
        'if errorlevel 1 exit /b %ERRORLEVEL%',
        ('cl /nologo /EHsc /MD /I"{0}" "{1}" /Fo"{2}" /Fe"{3}" /link /LIBPATH:"{4}" {5}' -f `
            $occtInclude, $fixtureSource, $fixtureObject, $fixtureExecutable, $occtLibrary, $libraries)
    )
    $compileLines | Set-Content -Path $compileScript -Encoding ascii
    $commandProcessor = if ([string]::IsNullOrWhiteSpace($env:ComSpec)) { 'cmd.exe' } else { $env:ComSpec }
    $compileOutput = & $commandProcessor /d /c $compileScript 2>&1
    $compileExitCode = $LASTEXITCODE
    $compileLog = Join-Path $paths.Logs "$Harness-fixture-build.log"
    $compileOutput | ForEach-Object { $_.ToString() } | Set-Content -Path $compileLog -Encoding utf8
    if ($compileExitCode -ne 0) {
        Get-Content -Path $compileLog -Tail 50 | ForEach-Object { Write-Host $_ }
        throw "$Harness fixture 컴파일 실패 (종료 코드: $compileExitCode, 로그: $compileLog)"
    }

    Invoke-LoggedCommand -FilePath $fixtureExecutable -ArgumentList @($fixtureOutput) `
        -LogName "$Harness-fixture-generation.log" -Description "$Harness fixture 생성"

    $dllDirectories = @((Join-Path $ResolvedOcctRoot 'win64/vc14/bin')) + @(Resolve-NativePrerequisites)
    $oldPath = $env:PATH
    $oldResourcePath = $env:CSF_OCCTResourcePath
    try {
        $dllPath = [string]::Join([IO.Path]::PathSeparator, $dllDirectories)
        $env:PATH = "$dllPath;$oldPath"
        $occtResourcePath = "$ResolvedOcctRoot\src"
        $env:CSF_OCCTResourcePath = $occtResourcePath

        $harnessAssembly = Join-Path $testOutput "$Harness.Regression.dll"
        $harnessArguments = @($harnessAssembly, $fixtureOutput)
        if ($Harness -eq 'WorkingSide' -and $null -ne $resolvedGeo00MoldPath) {
            $geo00ReportDirectory = Join-Path $paths.Fixtures 'WorkingSide/GEO-00'
            $geo00ReportPath = Join-Path $geo00ReportDirectory 'report.json'
            $harnessArguments += @('--geo-00', $resolvedGeo00MoldPath, $geo00ReportPath)
            $runMetadata.Geo00MoldPath = $resolvedGeo00MoldPath
            $runMetadata.Geo00ReportPath = $geo00ReportPath
            $runMetadata.Geo00ControlFixtureReportDirectory = Join-Path $geo00ReportDirectory 'control-fixtures'
        }
        Invoke-LoggedCommand -FilePath 'dotnet' -ArgumentList $harnessArguments `
            -LogName "$Harness-regression.log" -Description "$Harness Native 회귀"
    }
    finally {
        $env:PATH = $oldPath
        $env:CSF_OCCTResourcePath = $oldResourcePath
    }
}

$runMetadata = [ordered]@{
    RunId = $runId
    Group = $Group
    Configuration = $Configuration
    Platform = $Platform
    StartedAtUtc = [DateTime]::UtcNow.ToString('O')
    RepositoryRoot = $repositoryRoot
    OutputRoot = $verificationRoot
    Geo00MoldPath = $resolvedGeo00MoldPath
    Status = 'Running'
}

try {
    Invoke-XmlSummaryLanguageCheck

    if ($Group -eq 'Core' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-CoreTests
    }

    if ($Group -eq 'Design' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-DesignTests
    }

    if ($Group -eq 'Electrical' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-ElectricalTests
    }

    if ($Group -eq 'Application' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-ApplicationTests
    }

    if ($Group -eq 'Infrastructure' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-InfrastructureTests
    }

    if ($Group -eq 'App' -or $Group -eq 'Managed' -or $Group -eq 'All') {
        Invoke-AppTests
    }

    if ($Group -ne 'Core' -and $Group -ne 'Design' -and $Group -ne 'Electrical' -and
        $Group -ne 'Application' -and $Group -ne 'Infrastructure' -and $Group -ne 'App' -and $Group -ne 'Managed') {
        $nativeBridgePath = Build-NativeBridge
        if ($Group -eq 'WorkingSide' -or $Group -eq 'PowerLead' -or $Group -eq 'All') {
            $null = Build-App -BridgeAssemblyPath $nativeBridgePath

            if ($Group -eq 'WorkingSide' -or $Group -eq 'All') {
                Invoke-RegressionHarness -Harness 'WorkingSide' -BridgeAssemblyPath $nativeBridgePath
            }

            if ($Group -eq 'PowerLead' -or $Group -eq 'All') {
                Invoke-RegressionHarness -Harness 'PowerLead' -BridgeAssemblyPath $nativeBridgePath
            }
        }
    }

    $runMetadata.Status = 'Passed'
    Write-Host "검증 통과: $verificationRoot"
}
catch {
    $runMetadata.Status = 'Failed'
    $runMetadata.Failure = $_.Exception.Message
    Write-RunLog -Name 'failure.log' -Message $_.Exception.ToString()
    throw
}
finally {
    $runMetadata.CompletedAtUtc = [DateTime]::UtcNow.ToString("O")
    $runManifestPath = "$verificationRoot\run.json"
    $runMetadata | ConvertTo-Json -Depth 4 | Set-Content -Path $runManifestPath -Encoding utf8
    Write-Host "검증 기록: $runManifestPath"
}
