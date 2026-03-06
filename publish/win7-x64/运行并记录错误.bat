@echo off
chcp 65001 >nul 2>&1
title DH Client App - 错误诊断

echo ========================================
echo       DH Client App 错误诊断
echo ========================================
echo.
echo 正在启动程序并捕获错误信息...
echo.

cd /d "%~dp0"
DH.Client.App.exe 2>&1

echo.
echo ========================================
echo 程序已退出，错误代码: %errorlevel%
echo ========================================
echo.
echo 如果上面显示了错误信息，请截图发给开发人员。
echo.
pause
