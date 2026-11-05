# Leveling 11.75.1 RC8

- Recepção H.264 pelo decoder do Windows quando NVDEC não está disponível, permitindo usar GPUs Intel/AMD com D3D11. O caminho NVIDIA permanece prioritário. Há fallback de decodificação por CPU, com desempenho dependente do computador.
- Exibição pelo processador de vídeo D3D11, sem cópia de cada quadro acelerado para a CPU. Apresentação não bloqueante e recuperação dos recursos quando o dispositivo gráfico é removido.
- Corrigido o menu de permissões de Amigos que era destruído pelas atualizações de presença enquanto estava aberto.
- Configurações → Atualizações permite consultar até cinco versões anteriores da mesma linha e restaurar uma delas após confirmação. O pacote completo é verificado antes da substituição e reinício.
- Atualização e restauração compartilham bloqueio de concorrência e proteção de sessões ativas, inclusive quando a sessão começa durante o download.
- Conexão rápida oferece Tailscale como padrão e Direto na rede local como opção experimental. O modo local aceita somente IPv4 privado e tem uma ação para voltar ao Tailscale.
- O modo direto local não oferece descoberta pública, travessia de NAT ou P2P entre redes diferentes. Nessas redes, continue usando Tailscale.
- Diagnósticos registram a categoria do endereço da sessão e os estágios da tentativa de conexão. Um endereço Tailscale não é apresentado como prova de rota direta ou de entrega de vídeo.
- Ensaios automatizados incluem atualização real, restauração real com reinício e rejeição de pacote adulterado. GPU/WAN entre dois PCs não foram revalidados nesta versão.
- O host continua usando NVENC/NVIDIA. Receber no Positivo Intel, latência WAN e 60 FPS contínuos nesse notebook ainda precisam de validação no equipamento; não há garantia universal de 60 FPS ou 50 ms.
