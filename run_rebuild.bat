@echo off
setlocal
cd /d "%~dp0"
call "%~dp0run_with_env_root.bat" --rebuild %*
endlocal
