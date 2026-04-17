@echo off
setlocal EnableExtensions

cd /d "%~dp0"

set "FORCE_BUILD=0"
set "SKIP_ENV=0"
set "APP_ARGS="

:parse_args
if "%~1"=="" goto after_parse
if /I "%~1"=="--build" (
    set "FORCE_BUILD=1"
    shift
    goto parse_args
)
if /I "%~1"=="--rebuild" (
    set "FORCE_BUILD=1"
    shift
    goto parse_args
)
if /I "%~1"=="--no-env" (
    set "SKIP_ENV=1"
    shift
    goto parse_args
)
if /I "%~1"=="--help" goto help
call set "APP_ARGS=%%APP_ARGS%% \"%~1\""
shift
goto parse_args

:after_parse
if not "%SKIP_ENV%"=="1" call :load_vs_env

set "Platform="
set "PlatformTarget="

call :pick_existing_target
if "%FORCE_BUILD%"=="1" goto build_and_run
if defined RUN_TARGET goto run_existing

:build_and_run
call :prep_before_build
echo [run] Building Debug output...
dotnet build DH.AppHost.csproj -c Debug /nr:false -m:1
if errorlevel 1 goto build_failed
call :pick_existing_target
if not defined RUN_TARGET goto missing_output

:run_existing
for %%I in ("%RUN_TARGET%") do set "RUN_DIR=%%~dpI"
echo [run] Starting "%RUN_TARGET%"
start "" /D "%RUN_DIR%" "%RUN_TARGET%" %APP_ARGS%
goto end

:load_vs_env
set "VS_PATH="
if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" (
    echo [warn] Visual Studio C++ environment was not found. Continuing without vcvars64.
    exit /b 0
)
call "%VS_PATH%" >nul
exit /b 0

:prep_before_build
if exist "src\DH.UI\AlgorithmModule\obj\x64" rd /s /q "src\DH.UI\AlgorithmModule\obj\x64"
if exist "src\DH.UI\AlgorithmModule\bin\x64" rd /s /q "src\DH.UI\AlgorithmModule\bin\x64"
exit /b 0

:pick_existing_target
set "RUN_TARGET="
if exist "%~dp0bin\Debug\net6.0-windows7.0\DH.Client.App.exe" set "RUN_TARGET=%~dp0bin\Debug\net6.0-windows7.0\DH.Client.App.exe"
if not defined RUN_TARGET if exist "%~dp0src\DH.Client.App\bin\Debug\net6.0-windows7.0\DH.Client.App.exe" set "RUN_TARGET=%~dp0src\DH.Client.App\bin\Debug\net6.0-windows7.0\DH.Client.App.exe"
exit /b 0

:build_failed
echo.
echo [error] dotnet build failed.
pause
goto end

:missing_output
echo.
echo [error] Build finished but no runnable executable was found.
pause
goto end

:help
echo Usage:
echo   run_with_env_root.bat
echo   run_with_env_root.bat --rebuild
echo   run_with_env_root.bat --no-env
echo.
echo Default mode reuses an existing executable and only builds when output is missing.
echo --rebuild forces a fresh Debug build before launch.
echo --no-env skips vcvars64 loading and starts faster, but MSVC-based features may be unavailable.

:end
endlocal
