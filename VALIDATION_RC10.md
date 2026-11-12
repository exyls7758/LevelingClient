# Validação RC10 — central de problemas

## Executado
- Teste inicialmente falhou pela ausência de ProblemJournal; implementação adicionada depois.
- scripts/Test.ps1 passou, incluindo privacidade, persistência, rotação e falha de escrita do novo journal.
- Suíte managed reexecutada após testes de anexos ZIP: passou. Confirma UTF-8 e preservação do incidente em andamento.
- Publish Release x64 com núcleo nativo validado da RC9; núcleo não modificado.
- Aplicativo compilado aberto: geração real do ZIP pela central funcionou. manifest.json e report.json válidos; resumo, journal, amostras recentes e incidentes presentes.
- Prévia pública revisada; primeira versão tinha altura de campo de linha única. Substituída por texto selecionável com rolagem antes da publicação.
- Nenhuma issue de teste e nenhum diagnóstico foram publicados.

## Limites
- Não houve nova sessão WAN entre dois PCs nem teste GPU sob carga nesta alteração de diagnóstico.
- Crashes de kernel/driver, perda de energia e falhas sem exceção registrada podem não deixar evidência final.
- Metadados de exceções persistem a partir desta versão; logs antigos não são convertidos.
- Abrir rascunho não envia automaticamente o ZIP e não inicia análise autônoma fora do chat.

## Relato novo de crash
Adicionado Desconectar à janela remota e impedido resize após fechamento. A causa do novo crash do PC remoto ainda depende do diagnóstico recente; não é declarada resolvida nesta versão.

## Vídeo local após alteração de fechamento
Ensaio de 30 amostras com duas reanexações da superfície: PASS. 1607 quadros codificados, 14619 pacotes enviados/recebidos, 1544 decodificados e 1537 exibidos; CLEANUP confirmed. Não mede WAN ou input-to-photon.
