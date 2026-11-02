@echo off
setlocal
cd /d "%~dp0"
set "APP=%~dp0"
if not exist "%APP%Leveling.exe" set "APP=%~dp0dist\Leveling_CORE11_74c4_Candidate_x64"
if not exist "%APP%\Leveling.exe" (
  echo Cole o caminho completo da pasta que contem o Leveling.exe:
  set /p "APP=> "
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0COLETAR_ERROS.ps1" -AppDirectory "%APP%"
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
