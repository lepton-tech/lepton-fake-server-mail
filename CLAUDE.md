# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## O que é

Servidor de e-mail falso para testes locais: SMTP, POP3, IMAP, OAuth 2.0 e MCP num único `FakeServerEmail.exe` para Windows, com interface web. Tudo o que chega fica capturado; nada é entregue de verdade. O uso está descrito no [README.md](README.md).

## Requisitos que não podem quebrar

- **Executável único e autocontido.** O publish em Release é single-file e self-contained (win-x64, win-x86 e win-arm64). Nada pode precisar de arquivo ao lado do exe nem de instalação: a interface (`wwwroot/`) vai como `EmbeddedResource` e é servida por `ManifestEmbeddedFileProvider`, o Alpine.js é uma cópia local e não há CDN.
- **Sempre Windows.**
- **Porta ocupada não derruba nada.** `TcpService.CandidatePorts` tenta alternativas (porta abaixo de 1024 vira 2000 + porta) e o status mostra a porta real.
- **Público leigo.** Textos de interface, mensagens de protocolo e logs são em pt-br simples. Prefira controles explícitos a convenções escondidas, e não mostre um estado normal (serviço desligado, por exemplo) com cor de aviso.

## Comandos

```powershell
dotnet build
dotnet run --project src\FakeServerEmail -- --data C:\temp\fse --no-browser
dotnet test                                                         # suíte inteira, ~30 s
dotnet test --project tests\FakeServerEmail.Tests --filter-method "*Nome_do_teste*"
dotnet test --project tests\FakeServerEmail.Tests --filter-class "*SmtpTests"
.\publicar.ps1                                                      # dist\FakeServerEmail.exe (win-x64)
.\publicar.ps1 -Runtime win-x64,win-x86,win-arm64
```

- Os testes usam o Microsoft.Testing.Platform (definido em `global.json`) com xUnit v3, por isso o filtro é `--filter-method`/`--filter-class`, não `--filter`.
- O `wwwroot` está embutido: mudança de HTML, CSS ou JS só aparece depois de recompilar e reiniciar o servidor.
- O `publicar.ps1` apaga `artifacts\publish\<rid>` e sobrescreve `dist\`. Ele falha se alguma instância do exe estiver aberta. Para encerrar uma instância, use `POST /api/shutdown` na URL gravada em `%LOCALAPPDATA%\FakeServerEmail\em-execucao.json`.

## Arquitetura

**Inicialização (`Program.cs`, `FakeMailServer.cs`).** Há um mutex por pasta de dados (`SingleInstance`), e a URL da instância fica em `em-execucao.json`. Abrir o exe de novo só abre o navegador na instância existente, e `--mcp` desvia para o `McpStdioHost`.

`FakeMailServer.StartAsync(dataDirectory, configure)` monta o `ServerContext` (configurações, mensagens, tokens, autenticador, log de sessões, certificado). Em seguida cria os 7 `TcpService` e sobe o Kestrel com a API, o OAuth, o `/mcp` e a interface. Trocar porta pelo `PUT /api/settings` reinicia todos os serviços de protocolo; trocar a porta HTTP exige reiniciar o aplicativo.

**Protocolos (`Protocols/`).** Cada conexão passa por três camadas:
- `TcpService`: escuta a porta e cuida do TLS implícito.
- `MailConnection`: leitura por linha com buffer, upgrade para STARTTLS, leitura terminada em ponto e limites de tamanho.
- `IProtocolHandler`: `SmtpHandler`, `Pop3Handler` ou `ImapHandler`/`ImapSession`.

Os handlers leem `context.Settings.Current` a cada comando, então mudanças de configuração valem sem reiniciar. O parâmetro `allowStartTls` do construtor só diz se a porta não tem TLS implícito; o STARTTLS de fato depende também de `OfferStartTls`. O `Sasl.cs` concentra PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER para os três protocolos. O `Authenticator` valida senha (`AcceptAnyPassword`/`Users`) e token (`TokenService`).

**Log de conexões.** Cada conexão vira uma `ProtocolSession` no `SessionLog`, que fica em memória e não é gravado em disco. É a fonte da aba Conexões e das ferramentas MCP `list_connections`/`get_connection_log`. Ao mexer num handler, registre o que importa com `log.Info(...)`.

**Mensagens (`MessageStore`).**
- **Arquivos:** cada mensagem é gravada na hora em `mensagens/<id>.eml`. Os metadados (UIDs, flags, pastas) ficam em `mensagens/indice.json`, salvo com atraso de 400 ms e no `Flush` do encerramento.
- **Recuperação:** ao carregar, todo `.eml` ausente do índice é recuperado. Isso é proposital: no modo stdio, o cliente MCP mata a árvore de processos sem encerrar o servidor.

**Configurações (`SettingsStore`).**
- **Normalização:** `settings.json` é normalizado (`Normalize`) ao carregar e a cada `Update`.
- **Padrões novos:** todos os campos são gravados, então mudar um padrão em `AppSettings` só vale para instalações novas. Quem já tem `settings.json` mantém o que está salvo.

**OAuth (`OAuth/`).** O `OAuthServer` é um middleware que atende as rotas OIDC antes dos endpoints da API. O `TokenService` emite JWT RS256 com a chave `oauth-chave-privada.pem` e guarda o estado em `oauth-tokens.json`.

**Tempo real.** O `EventHub` publica eventos nomeados (`messages`, `sessions`, `tokens`, `settings`, `status`, `ping`) por SSE em `/api/events`. No navegador, `js/lib/events.js` transforma cada um em `window` event `fse-<nome>`, e as telas recarregam ao recebê-lo.

**Interface (`wwwroot/`).**
- **Montagem:** `index.html` declara as telas com `data-include`, e o `main.js` carrega esses parciais antes de `Alpine.start()`.
- **Estado:** a store global `app` (`js/store.js`) guarda o status, a aba atual (em `sessionStorage`) e uma âncora de rolagem entre abas.
- **Telas:** cada tela é um par `views/<tela>.html` + `js/views/<tela>.js`, registrado com `Alpine.data`. Não há etapa de compilação.

**MCP (`Mcp/`).**
- **Acesso ao servidor:** o `MailTools` sempre fala com o servidor pela API HTTP (`MailApiClient`), mesmo no mesmo processo. Uma capacidade nova para agentes entra primeiro na API e depois na ferramenta.
- **Modo stdio:** o `McpStdioHost` reaproveita a instância aberta ou sobe uma própria em segundo plano. O `MailApiClient` volta a procurar o servidor se a instância cair.

**Envios internos.** O "Enviar teste" da interface (`/api/send-test`) e o `send_email` do MCP passam pelo SMTP local. Quando `SmtpRequireAuth` está ligado, eles fazem login pelo `LocalSmtpLogin`.

**Dados gravados.** `DataCleaner` lista todo arquivo e pasta que o programa cria. Qualquer arquivo novo de dados precisa entrar nessa lista, senão "Remover dados do computador" deixa sobra.

## Testes

- São de ponta a ponta, com clientes reais: MailKit para SMTP/POP3/IMAP, MSAL para OAuth e o cliente MCP do SDK.
- **Servidor compartilhado:** a coleção `servidor` compartilha um único servidor (`ServerFixture`), em portas livres e pasta temporária. Ele usa `AcceptAnyPassword = true` e `SmtpRequireAuth = false`, diferente do padrão de produção.
- **Testes que mudam configurações:** precisam clonar a configuração original e restaurá-la no `finally`, porque os testes da coleção rodam em série sobre o mesmo servidor.
- **Testes com servidor próprio:** `PersistenceTests`, `DataRemovalTests` e `McpStdioTests` sobem o próprio servidor com os padrões de produção. Por isso fazem login com `teste@teste.local`/`teste`, e só SMTP e SMTP submissão vêm ligados. O `McpStdioTests` executa o `FakeServerEmail.exe` copiado para a pasta de saída dos testes.
- **MailKit:** o MailKit transforma o `530` do SMTP em `ServiceNotAuthenticatedException`, não em `SmtpCommandException`.
- **Nomes:** os testes têm nomes em pt-br com sublinhados (`Sem_STARTTLS_e_so_PLAIN_quando_configurado`). O código de produção usa nomes em inglês.
