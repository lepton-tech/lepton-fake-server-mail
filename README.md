# Fake Server Email

Servidor de e-mail falso para testes locais no Windows: **SMTP, POP3, IMAP e OAuth 2.0** num único `FakeServerEmail.exe`, autocontido (não precisa instalar .NET nem nada).

Dê dois cliques no executável: os serviços sobem e o navegador abre a caixa de entrada em `http://localhost:5080`. Para encerrar, feche a janela do console.

## Serviços e portas padrão

| Serviço | Porta | Segurança |
|---|---|---|
| SMTP | 25 | sem TLS ou STARTTLS |
| SMTP submissão | 587 | sem TLS ou STARTTLS |
| SMTPS | 465 | TLS direto |
| POP3 | 110 | sem TLS ou STLS |
| POP3S | 995 | TLS direto |
| IMAP | 143 | sem TLS ou STARTTLS |
| IMAPS | 993 | TLS direto |
| Interface web e OAuth | 5080 (HTTP) e 5443 (HTTPS) | |

Se uma porta estiver ocupada, o servidor escolhe outra sozinho (por exemplo, 25 vira 2025) e mostra a porta real no console e na interface.

- **Autenticação:** por padrão, aceita qualquer usuário e senha (PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER). Em **Configurações → Usuário e senha** dá para aceitar só usuários cadastrados, para testar senha errada. Em **Regras de envio e de tokens** dá para exigir login no SMTP e validar os tokens OAuth.
- **OAuth 2.0:** `authorization_code` com PKCE, `refresh_token`, `client_credentials`, `password` e `device_code`, com discovery OIDC, JWKS e rotas no estilo Microsoft (`/{tenant}/oauth2/v2.0/token`). Os tokens são JWT assinados, e os serviços de e-mail recusam token expirado, revogado ou de outro usuário.
- **Interface:** visualização HTML, texto, cabeçalhos, código-fonte, anexos, dados de entrega, log completo das conexões, tokens emitidos e exemplos de código para conectar.
- **Dados:** ficam em `%LOCALAPPDATA%\FakeServerEmail` (mensagens em `.eml`, configurações, certificado TLS autoassinado e chave do OAuth). Use `--data <pasta>` para outra pasta e `--no-browser` para não abrir o navegador.

## Agentes de IA (MCP)

O mesmo executável é um servidor [MCP](https://modelcontextprotocol.io/), feito com o SDK oficial para .NET (`ModelContextProtocol`). Há duas formas de conectar:

- **HTTP:** `http://localhost:5080/mcp`, enquanto o aplicativo estiver aberto.
- **stdio:** `FakeServerEmail.exe --mcp`. O agente inicia o processo. Se o aplicativo já estiver aberto, ele usa o mesmo servidor; se não estiver, sobe um em segundo plano e encerra junto com o agente.

```powershell
claude mcp add --transport http fake-email http://localhost:5080/mcp
claude mcp add fake-email -- "C:\caminho\FakeServerEmail.exe" --mcp
```

A aba **Como conectar** da interface mostra a configuração pronta para Claude Code, Claude Desktop, VS Code, Cursor e Codex, já com o caminho real do executável.

| Ferramenta | Para quê |
|---|---|
| `server_info` | portas reais, regras de autenticação, endpoints OAuth |
| `list_messages`, `get_message`, `get_message_source` | ler e-mails (com links extraídos do corpo) |
| `wait_for_message` | esperar o e-mail chegar depois de disparar uma ação no sistema testado |
| `get_attachment` | anexos como texto, imagem ou arquivo em disco |
| `send_email` | simular a chegada de mensagens para quem lê via IMAP/POP3 |
| `delete_messages`, `mark_messages` | limpar a caixa, marcar como lida ou não lida |
| `create_oauth_token`, `list_oauth_tokens`, `revoke_oauth_token` | tokens para XOAUTH2/OAUTHBEARER e teste de recusa |
| `list_connections`, `get_connection_log` | depurar por que o sistema não conseguiu enviar ou autenticar |
| `update_settings` | exigir senha, exigir AUTH no SMTP, validade dos tokens etc. |

## Estrutura do frontend

A interface fica em `src/FakeServerEmail/wwwroot` e vai embutida no `.exe`. Usa [Alpine.js](https://alpinejs.dev/) 3.17.4 (cópia local em `vendor/`, sem CDN) e módulos ES, sem etapa de compilação.

```
wwwroot/
  index.html            casca: cabeçalho, abas, aviso e painel de "encerrado"
  css/                  base.css (cores, botões, campos), layout.css, components.css
  js/main.js            carrega as telas, registra os componentes e inicia o Alpine
  js/store.js           estado global (status do servidor, aba atual, avisos)
  js/lib/               api.js, format.js, events.js (atualização em tempo real)
  js/views/             um componente por tela (inbox, oauth, sessions, connect, settings, send-dialog)
  views/                o HTML de cada tela
```

Para mudar uma tela, edite o par `views/<tela>.html` + `js/views/<tela>.js`.

## Gerar o executável

Requer o .NET SDK 10.

```powershell
.\publicar.ps1                                  # gera dist\FakeServerEmail.exe (x64)
.\publicar.ps1 -Runtime win-x64,win-x86,win-arm64
```

## Testes

```powershell
dotnet test --project tests\FakeServerEmail.Tests
```
