$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$occt = "$root/third_party/opencascade-7.9.3-vc14-64"
$out = "$root/x64/PowerLeadRegression"
New-Item -ItemType Directory -Force $out | Out-Null
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
# The app's existing HintPath is fixed to x64/Release. Refresh that bridge first
# so its copy-local step cannot overwrite the isolated DLL with an older build.
& $msbuild "$root/src/OccBridge/OccBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\Release\" /v:minimal
if ($LASTEXITCODE) { throw 'Native bridge build failed' }
& $msbuild "$root/HeatingMold.Mvp.sln" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\PowerLeadBuild\" /m /v:minimal
if ($LASTEXITCODE) { throw 'Isolated Release build failed' }
$dllDirs = @("$occt/win64/vc14/bin") + @(Get-ChildItem "$root/third_party/3rdparty-vc14-64" -Recurse -Filter *.dll | ForEach-Object DirectoryName | Select-Object -Unique)
$env:PATH = ($dllDirs -join ';') + ';' + $env:PATH
$env:CSF_OCCTResourcePath = "$occt/src"
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
$compile = "call `"$vcvars`" >nul && cl /nologo /EHsc /MD /I`"$occt/inc`" `"$PSScriptRoot/Fixtures.cpp`" /Fo`"$out/Fixtures.obj`" /Fe`"$out/Fixtures.exe`" /link /LIBPATH:`"$occt/win64/vc14/lib`" TKernel.lib TKMath.lib TKG2d.lib TKG3d.lib TKGeomBase.lib TKBRep.lib TKTopAlgo.lib TKDESTEP.lib TKXSBase.lib"
& cmd.exe /d /c $compile
if ($LASTEXITCODE) { throw 'Fixture compilation failed' }
& "$out/Fixtures.exe" $out
if ($LASTEXITCODE) { throw 'Fixture generation failed' }
& dotnet run --project "$PSScriptRoot/PowerLead.Regression.csproj" -c Release -- $out
if ($LASTEXITCODE) { throw 'Native regression failed' }
