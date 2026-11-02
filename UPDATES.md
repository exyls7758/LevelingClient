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
Teste local executado: pacote portátil 1.0.0 detectou e instalou 1.0.1, reiniciou e confirmou a versão. Um segundo pacote com byte adulterado foi rejeitado por integridade antes da instalação. Esse ensaio usa um aplicativo isolado com a mesma biblioteca Velopack, não duas instalações do Leveling pela internet. O workflow repete esse ensaio antes de publicar.
scripts/Test.ps1 inclui testes de atualização, sessão ativa, sessão iniciada durante download, falha, clique duplo e regressões de vídeo/input. Teste ponta a ponta do updater deve usar dois pacotes com versões distintas em uma instalação isolada, incluindo falha de download e reinício. Não considerar apenas build como confirmação de atualização instalada.
