@echo off
chcp 65001 >nul 2>&1
title DH Client App

echo ========================================
echo       DH Client App 启动检测
echo ========================================
echo.

REM --- 新增部分：自动查找并加载 VS C++ 编译器环境 ---
set "VS_PATH="
REM 查找 VS 2022 (Win10/11 常用)
if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"

REM 查找 VS 2019 (Win7 常用)
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\VC\Auxiliary\Build\vcvars64.bat"

if not "%VS_PATH%"=="" (
    echo [信息] 已找到 Visual Studio 环境，正在加载...
    call "%VS_PATH%" >nul
    echo [信息] 编译器环境加载完成。
) else (
    echo [警告] 未找到 Visual Studio C++ 编译器环境。
    echo        如果需要使用动态算法功能，请确保安装了 VS2019/2022 的 C++ 桌面开发工作负载。
)
REM ----------------------------------------------------

REM 检查 VC++ Runtime
if not exist "%SystemRoot%\System32\vcruntime140.dll" (
    echo [警告] 未检测到 Visual C++ 2015-2022 Redistributable
    echo.
    echo 请下载并安装：
    echo https://aka.ms/vs/17/release/vc_redist.x64.exe
    echo.
    echo 按任意键尝试启动程序...
    pause >nul
)

echo 正在启动 DH Client App...
echo.

cd /d "%~dp0"
start "" "DH.Client.App.exe"

if %errorlevel% neq 0 (
    echo.
    echo [错误] 程序启动失败！
    echo 错误代码: %errorlevel%
    echo.
    echo 请确保：
    echo 1. 已安装 Visual C++ 2015-2022 Redistributable (x64)
    echo 2. 已安装 Windows 7 SP1 所有重要更新
    echo.
    pause
)
