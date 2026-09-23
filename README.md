# Leveling — página pública para App ID Review

Este pacote contém uma página estática mínima, pronta para publicar.

## Arquivo principal
- `index.html`

## Publicação rápida

### GitHub Pages
1. Crie um repositório público.
2. Envie `index.html` para a raiz.
3. Abra Settings → Pages.
4. Em Source, escolha a branch principal e `/ (root)`.
5. Aguarde a URL pública.

### Cloudflare Pages / Vercel
Crie um projeto estático e envie esta pasta. Não há build, dependências ou backend.

## Dados já incluídos
- Aplicativo: Leveling Launcher
- Microsoft Client ID: `43470ec5-ade5-431a-9368-b46cfb198d7b`
- Contato: `Contatolevelingclient@hotmail.com`

## Antes de enviar o App ID Review
Abra a URL publicada em uma janela anônima e confirme que:
- carrega sem login;
- não retorna 404;
- funciona em HTTPS;
- exibe claramente o nome Leveling Client;
- mostra o contato e a finalidade da integração Microsoft/Minecraft.

Não publique Client Secret, tokens, chaves privadas ou credenciais.
