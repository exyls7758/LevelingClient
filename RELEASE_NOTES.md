# Leveling 11.75.1 RC6

- Recria os recursos D3D11 quando a reconexão da superfície encontra um dispositivo removido/resetado.
- Falhas de apresentação passam a reconectar a superfície, em vez de repetir Resize no dispositivo inválido.
- Detecta decodificação ativa com apresentação parada e tenta recuperar somente a superfície, com intervalo e limite de tentativas.
- Testes de ausência de vídeo, Present em andamento, limite, intervalo e nova sessão.
- A origem do reset do driver e a recuperação no PC remoto ainda exigem validação; não há garantia de 120 FPS nem eliminação de todas as falhas.
- Repete pedidos de quadro-chave durante espera por IDR, respeitando o cooldown existente, para recuperar pedidos perdidos na partida.
