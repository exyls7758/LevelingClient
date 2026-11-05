# Validação RC8 — 05/11/2026

## Alterações

- Receptor H.264 Media Foundation/D3D11 quando NVDEC não está disponível. NVIDIA continua prioritária; NVENC, bitrate, FEC e pacing do host foram preservados.
- Saída GPU sem leitura de pixels para CPU; fallback CPU com normalização de NV12 e conversão/apresentação D3D11. Corrigida durante o teste uma textura incompatível com VideoProcessorInputView.
- Recriação dos recursos de decoder/apresentação ao detectar dispositivo D3D11 removido. Isso não garante recuperação de travamento do driver ou do Windows.
- Menu de Amigos mantém seu proprietário enquanto está aberto, adiando atualização de presença.
- Restauração confirmada de versões oficiais anteriores e seleção entre Tailscale e conexão direta na rede local confiável. Não é P2P WAN.

## Executado

- Build Release x64 C++/C#/WinUI, com NVENC/NVDEC habilitados.
- Suíte de regressão: presenter, conexões, áudio, atualização, sessão/ownership/recuperação de vídeo e input. Testes de input não injetam comandos no sistema.
- Teste nativo Windows: H.264 gerado por encoder real, três ciclos com flush/retomada, saída CPU e saída DXGI verificadas separadamente. 29, 58 e 87 quadros acumulados em cada modo; dimensões e entrada vazias rejeitadas.
- Loopback 1080p60 usando decoder Windows acelerado: 56.491 pacotes enviados/recebidos, 1.715 quadros decodificados e 1.698 apresentados; duas recriações da superfície e encerramento confirmado. Contadores provêm das APIs reais, não do FPS configurado.
- Loopback 1080p60 forçando CPU: 62.877 pacotes enviados/recebidos, 1.821 quadros decodificados e 1.802 apresentados; duas recriações da superfície e encerramento confirmado.
- Comparação NVIDIA também executada em loopback. O teste de captura depende da atividade do desktop; esses resultados não são uma garantia de 60 FPS constantes. A animação do ensaio foi mantida visível para evitar throttling por oclusão.
- Atualizador portátil isolado: atualização 1.0.0→1.0.1 e restauração 1.0.1→1.0.0, ambas com reinício; pacotes adulterados de atualização e restauração rejeitados pelo backend de produção.
- Inventário de hashes relacionando fontes nativas à DLL publicada. Não há importação obrigatória de CUDA/NVCUVID para carregar o núcleo; NVCUVID é carregado sob demanda.

## Ainda não validado

- Positivo N8440/Intel e GPUs AMD reais; ausência física de driver NVIDIA nesse computador de teste.
- Duas máquinas pela internet, imagem inspecionada no notebook, latência de input ponta a ponta ou RTT de 50 ms. O RTT depende da rede e não é o tempo total até a imagem.
- Estresse prolongado, suspensão/retomada e reset real do driver gráfico. Os ensaios locais não reproduzem um congelamento completo do Windows.
- Menu de Amigos inspecionado manualmente na versão final; a correção foi compilada e revisada.
- Transmissão originada em Intel/AMD: o host permanece dependente de NVENC/NVIDIA. A compatibilidade adicionada é do receptor.

O checkpoint é separado da distribuição FINAL. Não instalar o checkpoint para validar a versão final. Diagnósticos particulares e identidades de execução não fazem parte da publicação.
