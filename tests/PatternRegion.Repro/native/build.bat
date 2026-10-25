@echo off
rem Builds the OCCT-level diagnostic probe for the piece-87 projection failure.
rem Must line up with the OCCT tree the bridge links against (7.9.3, MSVC 2022 x64).
setlocal
set HERE=%~dp0
set ROOT=%HERE%..\..\..\third_party\opencascade-7.9.3-vc14-64
if not exist "%ROOT%\inc\BRepProj_Projection.hxx" (echo OCCT not found at "%ROOT%" & exit /b 2)
call "%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 3
cl /nologo /MD /EHsc /std:c++17 /O2 /I"%ROOT%\inc" "%HERE%SectionProbe.cpp" /Fe:"%HERE%SectionProbe.exe" /Fo:"%HERE%SectionProbe.obj" /link /LIBPATH:"%ROOT%\win64\vc14\lib" TKernel.lib TKMath.lib TKG2d.lib TKG3d.lib TKGeomBase.lib TKBRep.lib TKTopAlgo.lib TKPrim.lib TKBO.lib TKBool.lib TKShHealing.lib TKGeomAlgo.lib TKXSBase.lib TKDESTEP.lib TKDE.lib
