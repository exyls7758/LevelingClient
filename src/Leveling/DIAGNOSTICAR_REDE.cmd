@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DIAGNOSTICAR_REDE.ps1" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Falha no diagnostico. Nenhuma configuracao foi alterada.
pause
exit /b %RESULT%
