$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$occt = "$root/third_party/opencascade-7.9.3-vc14-64"
$out = "$root/x64/WorkingSideRegression"
New-Item -ItemType Directory -Force $out | Out-Null
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
# Native first: the app intentionally references x64/Release/OccBridge.dll.
& $msbuild "$root/src/OccBridge/OccBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\Release\" /v:minimal
if ($LASTEXITCODE) { throw 'Native bridge build failed' }
& $msbuild "$root/HeatingMold.Mvp.sln" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\WorkingSideBuild\" /m /v:minimal
if ($LASTEXITCODE) { throw 'Isolated Release build failed' }
$dllDirs = @("$occt/win64/vc14/bin") + @(Get-ChildItem "$root/third_party/3rdparty-vc14-64" -Recurse -Filter *.dll | ForEach-Object DirectoryName | Select-Object -Unique)
$env:CSF_OCCTResourcePath = "$occt/src"
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
# cmd.exe quoting through PowerShell 5.1 mangles the /Fe and /Fo switches, so emit a batch file.
$batch = "$out/fixtures-build.cmd"
@(
    '@echo off',
    "call `"$vcvars`" >nul || exit /b 1",
    "cl /nologo /EHsc /MD /I`"$occt/inc`" `"$PSScriptRoot/Fixtures.cpp`" /Fo`"$out/Fixtures.obj`" /Fe`"$out/Fixtures.exe`" /link /LIBPATH:`"$occt/win64/vc14/lib`" TKernel.lib TKMath.lib TKG2d.lib TKG3d.lib TKGeomBase.lib TKBRep.lib TKTopAlgo.lib TKPrim.lib TKDESTEP.lib TKXSBase.lib"
) | Set-Content -Path $batch -Encoding ASCII
# vcvars64.bat aborts with "The input line is too long" once PATH carries every OCCT
# third-party directory, so compile with a minimal PATH and widen it afterwards.
$savedPath = $env:PATH
$env:PATH = "$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\Wbem"
& cmd.exe /d /c "`"$batch`""
if ($LASTEXITCODE) { throw 'Fixture compilation failed' }
$env:PATH = ($dllDirs -join ';') + ';' + $savedPath
& "$out/Fixtures.exe" $out
if ($LASTEXITCODE) { throw 'Fixture generation failed' }
& dotnet run --project "$PSScriptRoot/WorkingSide.Regression.csproj" -c Release -- $out
if ($LASTEXITCODE) { throw 'Native regression failed' }
