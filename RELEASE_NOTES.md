# Leveling 11.75.1 RC4

- A apresentação lenta no Windows deixa de segurar o bloqueio necessário para copiar os próximos quadros decodificados.
- Recuperação da fila passa a sinalizar a descontinuidade antes do primeiro quadro após o descarte, incluindo recuperações ocorridas durante a decodificação.
- Testes de apresentação bloqueada, restauração do bloqueio após erro e ordenação da recuperação.
- Mantidos os perfis de qualidade, bitrate, FEC e interface. Candidata para validar novamente no cliente; 90/120 FPS pela internet ainda não garantidos.
