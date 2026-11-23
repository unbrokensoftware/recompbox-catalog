@echo off
rem RecompBox: build a psxrecomp game from the player's disc and BIOS without its setup wizard, then point the game at them.
rem   %1 disc copy in the install (disc\...)   %2 BIOS in the install (bios\<STEM>.BIN)   %3 compiler pack
rem   %4 generated marker file   %5 game exe name   %6 build folder
rem The pack's env.bat has Unix line endings, which cmd misreads, so its settings are made here.
setlocal
set "PACK_ROOT=%~3"
set "PATH=%PACK_ROOT%\bin;%PACK_ROOT%\python;%PATH%"
set "CC=%PACK_ROOT%\bin\clang.exe"
set "CXX=%PACK_ROOT%\bin\clang++.exe"
set "AR=%PACK_ROOT%\bin\llvm-ar.exe"
set "RANLIB=%PACK_ROOT%\bin\llvm-ranlib.exe"
set "RETCOMM_TOOLCHAIN_DIR=%PACK_ROOT%"
set "RETCOMM_PYTHON=%PACK_ROOT%\python\python.exe"
set "PYTHONHOME="
set "PYTHONPATH="
set "PYTHONNOUSERSITE=1"
if exist "%PACK_ROOT%\deps\include\zlib.h" set "ZLIB_ROOT=%PACK_ROOT%\deps"
if exist "%PACK_ROOT%\deps\lib\cmake\SDL3\SDL3Config.cmake" set "SDL3_DIR=%PACK_ROOT%\deps\lib\cmake\SDL3"
if exist "%PACK_ROOT%\deps\lib\cmake\SDL3\SDL3-config.cmake" set "SDL3_DIR=%PACK_ROOT%\deps\lib\cmake\SDL3"
cd /d "%~dp0"
"%RETCOMM_PYTHON%" psxrecomp\psxrecomp_cli.py generate --project-root "%~dp0." --config game.toml --disc "%~1" --bios "%~2" --bios-stem "%~n2" --gen-marker "%~4" --json-progress || exit /b 11
"%RETCOMM_PYTHON%" psxrecomp\psxrecomp_cli.py rebuild --project-root "%~dp0." --config game.toml --build-dir "%~dp0%~6" --target psx-runtime --exe-basename "%~5" --no-pgo --prune-after build-intermediates || exit /b 12
if not exist "%~dp0%~6\%~5.exe" exit /b 13
rem The game reads its disc and BIOS from files beside it; paths relative to the game folder survive the install being moved.
set "DISC_REL=../disc/%~nx1"
set "BIOS_REL=../bios/%~nx2"
> "%~dp0%~6\disc.cfg" echo %DISC_REL%
> "%~dp0%~6\bios.cfg" echo %BIOS_REL%
> "%~dp0%~6\settings.toml" (
  echo [bios]
  echo path = "%BIOS_REL%"
  echo.
  echo [disc]
  echo path = "%DISC_REL%"
  echo.
  echo [launcher]
  echo skip_launcher = true
)
exit /b 0
