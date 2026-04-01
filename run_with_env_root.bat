@echo off
setlocal EnableExtensions

REM --- Locate VS C++ environment (same list as DH.UI script) ---
set "VS_PATH="
if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"

if "%VS_PATH%"=="" (
    echo [WARN] Visual Studio C++ environment was not found.
    echo [WARN] Trying dotnet directly...
) else (
    call "%VS_PATH%"
    if errorlevel 1 echo [WARN] Failed to initialize Visual Studio environment. Continuing anyway...
)

REM --- Move to repository root (this script location) ---
cd /d "%~dp0"
set "REPO_ROOT=%CD%"
for /f %%I in ('powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Get-Date -Format yyyyMMdd_HHmmss_fff"') do set "RUN_ID=%%I"
set "ARTIFACTS_DIR=.artifacts\run\%RUN_ID%"

REM Clear inherited VS environment that can affect dotnet build.
set "Platform="
set "PlatformTarget="
set "MSBUILDDISABLENODEREUSE=1"
set "MSBuildEnableWorkloadResolver=false"
call :shutdown_build_servers

REM Use a fresh artifacts folder so stale locked outputs do not block startup.
echo [INFO] Using isolated build artifacts: %ARTIFACTS_DIR%

REM Uncomment the next line if a full clean is needed.
REM dotnet clean DH.sln

REM Start the host app and forward any extra arguments.
echo [INFO] Launching dotnet run...
dotnet run --disable-build-servers --artifacts-path "%ARTIFACTS_DIR%" --project DH.AppHost.csproj %*
if errorlevel 1 goto :run_failed

goto :end

:shutdown_build_servers
echo [INFO] Shutting down dotnet build servers...
dotnet build-server shutdown >nul 2>&1
exit /b 0

:run_failed
echo .
echo [ERROR] dotnet run failed. Check the output above for details.

:end
endlocal
pause
