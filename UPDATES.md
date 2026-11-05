# Atualizações do Leveling

## Distribuição
Velopack 1.2.0, .NET 8/WinUI 3, Windows x64. Repositório público: exyls7758/LevelingClient. Sem tokens no aplicativo. O feed HTTPS do GitHub inclui RC; o pacote é verificado pelo Velopack antes da troca. Atualizações são explicitamente confirmadas, não aplicadas ao iniciar. Instalação usa Update.exe separado; preferências existentes ficam fora da pasta substituída (registro/AppData).

RC20.4 e anteriores precisam de uma instalação inicial via Setup.exe, ou da extração inicial do Portable oficial. Depois, Configurações → Atualizações permite atualizar sem navegador. Uma pasta de publish crua não é uma instalação atualizável e apresenta mensagem explicativa.

## Build e CI
O núcleo C++ requer Visual Studio, Windows SDK, NV_CODEC_SDK e CUDA_PATH. O build local deve compilar NVENC/NVDEC e gerar native/Multiverse.Core.dll com scripts/NativeArtifact.ps1 -Refresh. O arquivo manifest.json vincula os hashes de todos os headers, fontes e vcxproj à DLL. O runner windows-2025 valida esse inventário e compila a camada C#/WinUI. Mudanças nativas sem novo artefato falham antes da publicação. Isso não equivale a testar GPU/WAN no runner.

scripts/Release.ps1 -Summary "Descrição" incrementa RC automaticamente, testa, compila, empacota, faz commit e push. Exige autenticação Git normal no computador de desenvolvimento. O workflow publica somente após testes, build e empacotamento bem-sucedidos. GITHUB_TOKEN é fornecido pelo GitHub Actions, não incluído no cliente. Requer permissão de workflows e contents:write no repositório.

Os arquivos de distribuição são Setup.exe, Portable.zip, nupkg e releases.win.json. Não substitua assets de uma versão já publicada; publique uma versão maior. Não publique diretamente a pasta publish.

## Segurança e limites
O identificador de instalação é LevelingStream. A instalação padrão fica separada de %LOCALAPPDATA%/Leveling, onde versões anteriores guardam layouts e diagnósticos. Isso evita que a manutenção/desinstalação do programa utilize a pasta de dados existente.
Não há certificado Authenticode configurado; Windows/SmartScreen pode avisar na primeira instalação. SHA256 protege a integridade dos pacotes, não substitui assinatura de editor. Proteja a conta GitHub e as permissões de publicação. Nenhum atualizador pode reparar um comprometimento da própria origem confiável.

Atualização durante sessão/transmissão é bloqueada. Se uma sessão começar durante o download, o pacote fica pronto e aguarda nova confirmação. Falha de download mantém a versão atual; Velopack gerencia a substituição separada dos arquivos em uso. Não há promessa de rollback automático por falha lógica do aplicativo após iniciar. Para recuperação operacional, preserve a distribuição anterior e publique uma nova versão com a reversão.

Progresso percentual vem do Velopack; MB/s é estimativa média calculada a partir do progresso do pacote completo. Não são usados deltas nesta primeira integração.

## Validação
Em 02/11/2026, uma cópia portátil isolada do Leveling RC1, empacotada com o identificador definitivo LevelingStream, detectou a RC2 publicada no GitHub, exibiu as notas, baixou o pacote real e substituiu o executável. O arquivo atualizado informa 11.75.1-rc.2; o log de inicialização posterior registra MainWindow.Activate/READY. O ensaio não alterou a instalação principal. A instalação inicial por Setup em um Windows limpo e a transmissão entre dois PCs não foram revalidadas nesta tarefa.
Teste local executado: pacote portátil 1.0.0 detectou e instalou 1.0.1, reiniciou e confirmou a versão. Um segundo pacote com byte adulterado foi rejeitado por integridade antes da instalação. Esse ensaio usa um aplicativo isolado com a mesma biblioteca Velopack, não duas instalações do Leveling pela internet. O workflow repete esse ensaio antes de publicar.
scripts/Test.ps1 inclui testes de atualização, sessão ativa, sessão iniciada durante download, falha, clique duplo e regressões de vídeo/input. Teste ponta a ponta do updater deve usar dois pacotes com versões distintas em uma instalação isolada, incluindo falha de download e reinício. Não considerar apenas build como confirmação de atualização instalada.

## Restauração a partir da RC8

Configurações → Atualizações → Versões anteriores consulta os pacotes completos das releases oficiais recentes. Selecione uma versão e confirme Restaurar e reiniciar. A lista contém até cinco versões menores que a instalada, da mesma linha major/minor. A consulta normal continua oferecendo somente atualizações mais novas. A restauração é manual e depende do aplicativo abrir e da disponibilidade do pacote no GitHub. As releases antigas podem voltar a apresentar problemas corrigidos depois.

Velopack verifica o SHA256 do pacote selecionado. A operação usa o mesmo bloqueio da atualização e recusa instalação durante sessão ou transmissão. Uma sessão iniciada durante o download adia a instalação até nova confirmação. Preferências ficam fora da instalação e não são revertidas. Mudanças futuras de formato de preferências precisam manter compatibilidade ou implementar migração reversível antes de permitir retorno a uma versão incompatível.

A suíte Test-UpdateLifecycle.ps1 também instala 1.0.1 e restaura 1.0.0 usando ReleaseUpdateBackend, reiniciando e conferindo a versão do binário. É um aplicativo isolado com o backend de produção. Não representa um teste WAN ou GPU do Leveling.

## Caminhos de conexão na RC8

A opção Direto na rede local usa o transporte IPv4 já existente em uma rede privada confiável. Não abre portas no roteador, não instala serviços e não faz travessia de NAT. Tailscale continua sendo a opção entre redes diferentes. O controle de acesso existente continua ativo. As configurações de Amigos usam o caminho Tailscale, independentemente da opção da conexão rápida.

O projeto ainda depende do Tailscale para criptografia de tráfego pela internet. Um modo WAN próprio requer proteção autenticada para controle, vídeo, áudio e input, negociação de endpoints, descoberta e fallback de NAT. GitHub Releases distribui arquivos e não atua como servidor de sinalização/relay. Um serviço gratuito, se escolhido no futuro, terá limites de disponibilidade e uso. Não há serviço contratado nem domínio necessário nesta versão.

## Receptor Windows na RC8

O núcleo tenta NVDEC primeiro quando o runtime NVIDIA está disponível. Se ele não puder iniciar, usa o H.264 Media Foundation do Windows com gerenciador D3D11/DXVA; o fallback por CPU continua usando D3D11 para conversão e apresentação. Não muda o encoder NVENC do host, bitrate, FEC ou pacing. Requer Windows x64 com componentes de mídia e driver D3D11 compatível; não significa suporte a qualquer computador ou sistema operacional.

`scripts/Test-VideoCompatibility.ps1` gera H.264 real e verifica saída CPU e texturas GPU, além de retomada após flush. Esse teste exige GPU de vídeo real e deve ser executado localmente. `LEVELING_DECODER=mf` e `mf-software` são seletores de diagnóstico por processo; sem eles, NVIDIA permanece prioritária. O campo reservado 0 das estatísticas do decoder Windows informa a memória de saída observada: 1 CPU, 2 textura DXGI. O campo continua 0 no NVDEC legado.

Os eventos CONNECTION_ATTEMPT, CONNECTION_CONTROL_REACHED, CONNECTION_SESSION_ACCEPTED, CONNECTION_PATH_REJECTED, CONNECTION_TIMEOUT e CONNECTION_SOCKET_ERROR usam mode 0 para fluxo legado, 1 para Tailscale e 2 para local direto. A amostra session.connectionPath classifica o endereço ativo. Tailscale direto versus relay continua exigindo diagnóstico de rota, pois um IP 100.x sozinho não comprova o caminho. Eventos de primeiro quadro e contadores de UDP/decoder/apresentação existentes continuam sendo a evidência de progresso de vídeo.
