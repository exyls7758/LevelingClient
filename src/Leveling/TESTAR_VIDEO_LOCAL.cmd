@echo off
setlocal
cd /d "%~dp0"
echo Teste local: captura, NVENC, UDP loopback, NVDEC e janela.
echo Nenhum video sera enviado para outro computador.
echo Feche outras instancias do Leveling antes de executar.
start "" /wait "%~dp0Leveling.exe" --video-self-test "%~dp0TESTE_VIDEO_LOCAL.log"
type "%~dp0TESTE_VIDEO_LOCAL.log"
echo.
echo Este teste nao valida internet, o computador remoto nem latencia de input.
pause
