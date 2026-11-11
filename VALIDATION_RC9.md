# Validação RC9 — recuperação de apresentação e dependências

## Evidência analisada

Seis arquivos de diagnóstico fornecidos em 10/11, todos com os mesmos binários RC8. Os arquivos incluem incidentes históricos; eventos repetidos foram considerados com seus horários e instâncias. O contador dropped do gravador também representa descarte por retenção limitada e não foi interpretado como perda UDP.

Um incidente correlacionado apresenta recuperação do host falhando após o receptor deixar de apresentar quadros. O log de inicialização local correspondente contém a mensagem devolvida pelo receptor: Escala D3D11: 0x80070102. No código, esse status é devolvido quando o acesso sincronizado ao decoder excede 100 ms. A janela tratava essa espera como falha definitiva e notificava o host antes de tentar a recuperação local. Isso explica uma interrupção observada; não comprova a causa de todos os travamentos e crashes.

Outros eventos incluem encoder ocupado, lacunas de captura, recuperação aceita e desconexão. Os seis arquivos não fornecem uma stack fatal suficiente para atribuir todos os crashes a uma causa única. Nem conexão de controle ativa, nem perda exibida como zero comprovam apresentação contínua.

## Alterações

- RemoteSessionWindow.cs e UiThreadOperation.cs: espera transitória com retentativa assíncrona limitada, preservação da superfície e comunicação terminal após tentativas locais.
- Multiverse.Core.vcxproj: runtime C++ estática, sem alterar encoder/decoder/pacing/FEC.
- NativeNvdecPlayer.cs e RemoteStreamingRuntime.cs: status nativo e etapa numérica no diagnóstico de inicialização.
- Test-NativeDeployment.ps1, Test.ps1 e Pack.ps1: validação de imports do núcleo e regressões de janela incluídas na suíte/release.
- Versão 11.75.1-rc.9, notas e documentação de distribuição atualizadas.

## Executado

- Teste de regressão novo falhou antes da implementação; passou após a correção.
- scripts/Test.ps1: concluído com código 0, incluindo apresentação nativa, janela, conexões, áudio, atualizações, regressões gerenciadas e input.
- scripts/Test-VideoCompatibility.ps1: H.264 real decodificado em três ciclos, saídas CPU e textura GPU; 87 quadros acumulados em cada caminho após flush.
- Build Release x64/Publish com NVENC e NVDEC habilitados.
- Verificação PE da DLL: sem imports obrigatórios de runtime VC++ externo ou de drivers NVIDIA. Não equivale a teste de instalação em Windows limpo.

## Limites

Não executado entre os dois computadores dos usuários, no notebook Intel específico ou em uma rede WAN real. Não comprovada resolução de todos os crashes, estabilidade prolongada, latência de 50 ms ou 60 FPS constantes. Não modificados bitrate, qualidade, perfis, FEC ou algoritmo de codificação para mascarar falhas.

## Falha adicional reproduzida no ensaio

O primeiro ensaio NVIDIA com duas recriações de superfície falhou em DetachRendererAsync: uma única tentativa de acesso ao decoder ocupado era considerada terminal. A liberação agora usa até dez tentativas espaçadas por 100 ms, todas despachadas para a UI, sem considerar recursos liberados antes da confirmação. Uma troca de decoder durante a espera cancela a operação em vez de desconectar a nova sessão. Regressões cobrem sucesso após contenção, orçamento esgotado e propagação de erro do dispatcher.

Esse primeiro ensaio também apresentou descontinuidades e esperas por IDR. Não foi considerado aprovação de desempenho, embora houvesse quadros apresentados. Seu log foi preservado localmente em evidence/rc9-nvidia.log.

## Ensaio completo após a segunda correção

NVIDIA, 1080p60 solicitado, aproximadamente 31 segundos: 1.325 quadros codificados, 41.288 pacotes enviados, 41.287 recebidos, 247 quadros decodificados e 213 apresentados. Duas recriações da superfície concluídas, progresso após ambas e CLEANUP confirmado. O critério funcional do ensaio passou; o desempenho NÃO atingiu 60 FPS. Houve 814 descartes esperando referência e 39 pedidos de IDR. A GPU local foi medida em 97–98% e havia jogo/gravadores concorrentes. Não se atribui automaticamente toda a degradação à carga: este resultado não isola as causas nem comprova ausência de regressão de desempenho. Log evidence/rc9-nvidia-final.log preservado.

## Ensaios com GPU livre em 11/11

A medição inicial por nvidia-smi registrou 0% de uso da GPU antes do ensaio. Em todos os ensaios abaixo houve recriação da superfície aos 10 e 20 segundos e encerramento confirmado.

| Versão/caminho | Duração aproximada | Codificados | Pacotes enviados/recebidos | Decodificados | Apresentados |
|---|---:|---:|---:|---:|---:|
| RC9 NVIDIA | 30 s | 1545 | 14967 / 14967 | 1508 | 1495 |
| RC8 final NVIDIA | 30 s | 1631 | 13740 / 13740 | 1614 | 1605 |
| RC9 Windows DXVA | 30 s | 1527 | 11170 / 11170 | 1520 | 1512 |

A RC8 de comparação tem a mesma DLL nativa dos seis diagnósticos (SHA256 4BC42F66519256552DAADF9F090D41AA5282313BBF93DC1243646C5A155F5511). Um ensaio anterior usou um checkpoint RC8 e foi excluído desta comparação. A captura varia com a atividade do desktop: estes números não demonstram ganho de FPS ou garantia de 60 constantes. O caminho Windows teve zero pedidos de IDR, e o primeiro ensaio RC9 NVIDIA teve apenas um pedido inicial, sem a sequência de recuperação observada sob carga extrema.

O ZIP FINAL teve a DLL nativa conferida contra o inventário compilado: SHA256 C3B1B522B65823BE65ACD622E7CC0215CEBE09B5F4341DFF630939E6CF54BF70. Checkpoint e FINAL são arquivos distintos; o FINAL inclui a segunda correção de liberação assíncrona.

Ensaio NVIDIA estendido: 121,4 s, 6.897 quadros codificados, 61.937 pacotes enviados e recebidos, 6.885 decodificados e 6.875 apresentados. Zero pedidos de recuperação IDR, zero descartes esperando quadro-chave e status do renderer 0 no fim. Duas recriações da superfície, continuidade e encerramento confirmados. Entre 10 s e o fim: +2 handles e +5 MB privados; isso não substitui teste de vazamento prolongado. Log evidence/rc9-nvidia-120s.log. Média de apresentação próxima de 56,6 FPS neste desktop com captura variável, não garantia de 60 constantes.

Conclusão: corrigidas duas falhas de tratamento de contenção comprovadas pelo código/log e pelo ensaio local. A candidata passou regressões e apresentação real NVIDIA/Windows com GPU livre. Não há evidência suficiente para declarar eliminados todos os crashes relatados ou prometer fluidez sob GPU saturada. O congelamento completo do PC e o comportamento WAN/Intel precisam de validação específica; nenhuma proteção do driver foi desativada.
