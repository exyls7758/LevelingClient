# Leveling 11.75.1 RC7

- O cliente limita a espera pela thread de apresentação ao conectar, redimensionar e liberar a superfície. Uma parada pendente preserva os recursos para nova tentativa.
- Após três esperas sem sinal de capacidade da fila gráfica, a tentativa de apresentação permite retorno imediato se o compositor estiver ocupado.
- As consultas do HUD reaproveitam a última amostra quando o decoder está ocupado.
- A regressão nativa bloqueia o apresentador, confirma a retenção após o prazo e verifica a liberação após o retorno.
