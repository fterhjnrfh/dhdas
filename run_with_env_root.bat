@echo off
setlocal

REM --- Locate VS C++ environment (same list as DH.UI script) ---
set "VS_PATH="
if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"

if "%VS_PATH%"=="" (
    echo [警告] 未找到 Visual Studio C++ 编译器环境。
    echo 正在尝试直接启动...
) else (
    call "%VS_PATH%"
)

REM --- Move to repository root (this script location) ---
cd /d "%~dp0"

REM 清理解耦變量，避免 VS 腳本殘留配置影響 dotnet build
set Platform=
set PlatformTarget=

REM 同步 DH.UI 腳本的快取清理（避免舊架構殘留）
if exist "src\DH.UI\AlgorithmModule\obj\x64" rd /s /q "src\DH.UI\AlgorithmModule\obj\x64"
if exist "src\DH.UI\AlgorithmModule\bin\x64" rd /s /q "src\DH.UI\AlgorithmModule\bin\x64"

REM 需要時可解開下一行對整個解決方案進行乾淨構建
REM dotnet clean DH.sln

REM 啟動主應用 (DH.AppHost)；透傳附加參數
dotnet run --project DH.AppHost.csproj %*
if errorlevel 1 goto :run_failed

goto :end

:run_failed
echo .
echo dotnet run 失敗，請檢查輸出以了解詳細原因。

:end
endlocal
pause
