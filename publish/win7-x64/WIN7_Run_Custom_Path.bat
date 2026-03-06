@echo off
chcp 65001 >nul 2>&1
title DH Client App (Win7 Custom)

echo ========================================
echo       DH Client App - Win7 指定路径启动
echo ========================================
echo.

REM 指定您的 Win7 电脑上的 VS 安装路径
REM 根据您提供的路径：D:\Program File (x86)\Microsoft Visual Studio\2019\Community
REM 对应的环境脚本通常在 VC\Auxiliary\Build\vcvars64.bat

set "VS_SCRIPT_PATH=D:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"

REM 容错处理：也是为了防止目录名是 "Program File (x86)" 而不是 "Program Files (x86)"
if not exist "%VS_SCRIPT_PATH%" set "VS_SCRIPT_PATH=D:\Program File (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"

if exist "%VS_SCRIPT_PATH%" (
    echo [信息] 找到 VS 环境脚本，正在加载...
    echo 路径: %VS_SCRIPT_PATH%
    call "%VS_SCRIPT_PATH%" >nul
    echo [成功] 环境加载完成。
) else (
    echo [错误] 在指定路径未找到 vcvars64.bat。
    echo 请确认文件是否存在: %VS_SCRIPT_PATH%
    echo.
    echo 尝试继续启动，但动态编译可能失败...
    pause
)

echo.
echo 正在启动程序...
start "" "DH.Client.App.exe"
exit
