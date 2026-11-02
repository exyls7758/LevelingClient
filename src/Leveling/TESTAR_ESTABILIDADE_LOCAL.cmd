@echo off
setlocal
cd /d "%~dp0"
echo Teste local de 120 segundos, com movimento controlado.
echo Abre uma fonte animada e uma janela de video. Nao envia video a outro PC.
echo Feche outras instancias do Leveling antes do teste.
start "" /wait "%~dp0Leveling.exe" --video-self-test "%~dp0TESTE_ESTABILIDADE_LOCAL.log" --seconds 120
type "%~dp0TESTE_ESTABILIDADE_LOCAL.log"
echo.
echo Este teste nao confirma a internet, o jogo ou a GPU do computador remoto.
pause
