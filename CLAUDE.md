# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## O que é

Servidor de e-mail falso para testes locais: SMTP, POP3, IMAP, OAuth 2.0 e MCP num único `FakeServerEmail.exe` para Windows, com interface web. Tudo o que chega fica capturado; nada é entregue de verdade. O uso está descrito no [README.md](README.md).

## Requisitos que não podem quebrar

- **Executável único e autocontido.** O publish em Release é single-file e self-contained (win-x64, win-x86 e win-arm64). Nada pode precisar de arquivo ao lado do exe nem de instalação: a interface (`wwwroot/`) vai como `EmbeddedResource` e é servida por `ManifestEmbeddedFileProvider`, o Alpine.js é uma cópia local e não há CDN. O target `ExcludeIisModuleFromPublish` do `.csproj` tira do pacote o `aspnetcorev2_inprocess.dll` do IIS, que não é usado; sem ele, o exe não extrai nada para `%TEMP%`.
- **Sempre Windows.**
- **Porta ocupada não derruba nada.** `TcpService.CandidatePorts` tenta alternativas (porta abaixo de 1024 vira 2000 + porta). A verificação de porta livre testa IPv4 e IPv6, como o Kestrel faz ao abrir, e o status mostra a porta real.
- **Público leigo.** Textos de interface, mensagens de protocolo e logs são em pt-br simples. Prefira controles explícitos a convenções escondidas, e não mostre um estado normal (serviço desligado, certificado ainda não confiável) com cor de aviso.

## Comandos

```powershell
dotnet build
dotnet run --project src\FakeServerEmail -- --data C:\temp\fse --no-browser
dotnet test                                                         # suíte inteira, ~30 s
dotnet test --project tests\FakeServerEmail.Tests --filter-method "*Nome_do_teste*"
dotnet test --project tests\FakeServerEmail.Tests --filter-class "*SmtpTests"
dotnet format whitespace --verify-no-changes
.\publicar.ps1                                                      # dist\FakeServerEmail.exe (win-x64)
.\publicar.ps1 -Runtime win-x64,win-x86,win-arm64
.\lancar-versao.ps1                                                 # na main: testa, gera os três exe e publica a release
```

- O `Directory.Build.props` liga os analisadores `latest-recommended`, o estilo do `.editorconfig` no build e `TreatWarningsAsErrors`: qualquer aviso quebra o build. As versões dos pacotes ficam em `Directory.Packages.props`.
- Os testes usam o Microsoft.Testing.Platform (definido em `global.json`) com xUnit v3, por isso o filtro é `--filter-method`/`--filter-class`, não `--filter`.
- O `wwwroot` está embutido: mudança de HTML, CSS ou JS só aparece depois de recompilar e reiniciar o servidor. `node --check arquivo.js` serve para conferir a sintaxe de um JS.
- O `publicar.ps1` apaga `artifacts\publish\<rid>` e sobrescreve `dist\`. Ele falha se alguma instância do exe estiver aberta. Para encerrar uma instância, use `POST /api/shutdown` na URL gravada em `%LOCALAPPDATA%\FakeServerEmail\em-execucao.json`.

## Git, PRs e versões

O repositório é `lepton-tech/lepton-fake-server-mail`, e o fluxo completo está no [CONTRIBUTING.md](CONTRIBUTING.md).

- **Branches protegidas:** nunca faça commit nem push na `main` ou na `develop`. Toda mudança sai de uma branch criada a partir da `develop` (`feature/`, `fix/`, `docs/`) e entra por PR na `develop`. A `develop` vai para a `main` por outra PR. Cada PR precisa da aprovação de outra pessoa.
- **Merge:** sempre com merge commit (`gh pr merge --merge`). Squash ou rebase na PR da `develop` para a `main` fazem as duas divergirem.
- **Versão:** é a `<Version>` do `.csproj`, que vira a tag `v<Version>`. O `lancar-versao.ps1`, rodado na `main` igual à do GitHub, testa, gera os três exe e publica a release com eles como assets.
- **Sem pipeline:** não há GitHub Actions. Build, testes e release rodam localmente, com o `gh`. As skills `/pr` e `/lancar-versao` seguem esse fluxo.

## Arquitetura

**Inicialização (`Program.cs`, `Hosting/`).** O `Program.cs` só despacha:
- os comandos do processo com administrador (`ComputerTrust`), que precisam rodar antes de tudo;
- o modo MCP por stdio (`McpStdioHost`);
- o modo janela (`ConsoleHost`), com mutex por pasta de dados, banner, sinais de encerramento (`ShutdownSignals`) e atividade escrita no console por uma fila (`ActivityPrinter`), para o console não travar os protocolos.

`CommandLineOptions` lê os argumentos e `DataDirectoryResolver` escolhe a pasta de dados. A URL da instância fica em `em-execucao.json`; abrir o exe de novo só abre o navegador na instância existente.

**Composição e injeção de dependência (`FakeMailServer.cs`).** O `FakeMailServer` é a raiz de composição:
- **Criação:** `StartAsync(dataDirectory, configure)` cria uma vez os serviços centrais (`SettingsStore`, `MessageStore`, `TokenService`, `SessionLog`, `CertificateProvider`, `Authenticator`, `EventHub`) e os junta no `ServerContext`.
- **Compartilhamento:** eles são compartilhados por duas partes com ciclos de vida diferentes, os `TcpService` de SMTP/POP3/IMAP e a aplicação web. Por isso não são criados pelo contêiner.
- **Web:** `BuildWebApplication` registra essas instâncias no contêiner do ASP.NET Core (`AddSharedServices`). Daí em diante, endpoints, o middleware do OAuth e as ferramentas MCP recebem as dependências por injeção.
- **Porta ocupada:** se o Kestrel não conseguir abrir a porta, a aplicação web é recriada na próxima porta.
- **Mudança de porta:** pelo `PUT /api/settings`, reinicia os serviços de protocolo; trocar a porta HTTP exige reiniciar o aplicativo.

**Protocolos (`Protocols/`).** Cada conexão passa por três camadas:
- `TcpService`: escuta a porta e cuida do TLS implícito.
- `MailConnection`: leitura por linha e por blocos com limites de tamanho, upgrade para STARTTLS e leitura terminada em ponto.
- `IProtocolHandler`: `SmtpHandler`/`SmtpSession`, `Pop3Handler`/`Pop3Session`, `ImapHandler`/`ImapSession`, cada sessão com um método por comando.

Os handlers leem `context.Settings.Current` a cada comando, então mudanças de configuração valem sem reiniciar. O `Sasl.cs` concentra PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER e a troca de desafios (`ExchangeAsync`), que mascara credenciais no log. O `Authenticator` valida senha e token.

Entrada do cliente nunca é alocada pelo tamanho que ele declara. BDAT e literais IMAP são copiados em fatias, e o tokenizer e a busca IMAP têm limite de aninhamento: proteção contra derrubar o processo com uma conexão.

**Log de conexões.** Cada conexão vira uma `ProtocolSession` no `SessionLog`, que fica em memória e não é gravado em disco. É a fonte da aba Conexões e das ferramentas MCP `list_connections`/`get_connection_log`. Ao mexer num handler, registre o que importa com `log.Info(...)`.

**Arquivos de dados (`Core/`).**
- **Nomes:** todos os nomes de arquivo ficam em `DataFiles`, que também é a lista do que "Remover dados do computador" apaga (`DataCleaner`). Arquivo de dados novo entra ali, senão a remoção deixa sobra.
- **Gravação:** gravações que precisam sobreviver a um encerramento forçado usam `AtomicFile`, que grava num temporário e troca de uma vez.
- **Mensagens:** o `MessageStore` grava cada `.eml` na hora. O índice (`mensagens/indice.json`) é salvo com atraso de 400 ms, de forma serializada, e no `Dispose`. Ao carregar, `.eml` fora do índice são recuperados, porque no modo stdio o cliente MCP mata o processo sem encerrar. Um índice sem `cleanShutdown` faz o próximo UID pular 1000, para não reaproveitar UIDs.
- **Configurações:** o `SettingsStore` normaliza ao carregar e a cada `Update`, inclusive portas inválidas (tabela `PortSetting`). `Update` guarda uma cópia, não a instância recebida. Todos os campos são gravados, então mudar um padrão em `AppSettings` só vale para instalações novas.

**Certificado (`CertificateProvider`, `ComputerTrust`).** O certificado gerado é de site, não de autoridade (`CN=localhost, O=Fake Server Email`, `CA=false`). Há três níveis de confiança:
- **Nenhum.**
- **Usuário:** `CurrentUser\Root`, sem administrador, com uma confirmação do Windows.
- **Computador:** `LocalMachine\Root`. O exe se reabre com `runas` (UAC) e os argumentos `--trust-computer-certificate`/`--untrust-computer-certificate`. O processo com administrador só aceita certificados que passam em `IsOwnCertificate`.

**OAuth (`OAuth/`).**
- **Middleware:** o `OAuthServer` é um `IMiddleware` com uma tabela de rotas por sufixo, para valer sob qualquer prefixo de tenant. Cada assunto fica num arquivo parcial (autorização, token, dispositivo); as páginas HTML ficam em `OAuthPages`.
- **Redirecionamento:** `redirect_uri` com `javascript:`, `data:` e semelhantes é recusado, e PKCE segue a RFC 7636.
- **Tokens:** o `TokenService` emite JWT RS256 e só aceita token de acesso conhecido. `id_token` é recusado, e token apagado da lista também.

**API web (`Web/`).**
- **Rotas:** `MapFakeMailApi` monta o grupo `/api` em arquivos parciais por assunto (`ApiEndpoints.*.cs`). Os handlers recebem os serviços por parâmetro.
- **Filtro do grupo:** recusa escrita vinda de outro site (`Sec-Fetch-Site`/`Origin`). O host filtering aceita só nomes locais quando o acesso pela rede está desligado.
- **Anexos:** anexos HTML/SVG/XML saem com `Content-Security-Policy: sandbox` e `nosniff`.
- **Contratos:** os tipos de status e sessão (`StatusModels.cs`) são compartilhados com o MCP.
- **Envios internos:** o "Enviar teste" e o `send_email` passam pelo `LocalSmtpSender`, que faz login quando o SMTP exige.

**Tempo real.** O `EventHub` publica eventos nomeados por SSE em `/api/events` (`ListenAsync` remove o assinante sozinho). No navegador, `js/lib/events.js` gera `fse-<nome>` e `fse-disconnected`, e as telas recarregam.

**Interface (`wwwroot/`).**
- **Montagem:** `index.html` declara as telas com `data-include`, e o `main.js` carrega os parciais antes de `Alpine.start()`.
- **Store `app`:** a store global (`js/store.js`) guarda o status, a aba atual (em `sessionStorage`), a âncora de rolagem e o estado offline. O `run(action, success)` mostra erro ou sucesso no toast. Use-o em toda ação.
- **Telas:** cada tela é um par `views/<tela>.html` + `js/views/<tela>.js`. Respostas antigas são descartadas por contadores de sequência.

**MCP (`Mcp/`).**
- **Acesso ao servidor:** o `MailTools` (arquivos parciais por assunto) sempre fala com o servidor pela API HTTP (`MailApiClient`), mesmo no mesmo processo. Uma capacidade nova entra primeiro na API e depois na ferramenta.
- **Erros:** precisam virar `McpException` com mensagem em pt-br; outro tipo chega ao agente só como "An error occurred invoking".

## Testes

- **Abordagem:** são de ponta a ponta, com clientes reais: MailKit para SMTP/POP3/IMAP, MSAL para OAuth e o cliente MCP do SDK. `CoreTests` cobre partes do núcleo sem servidor.
- **Servidor compartilhado:** a coleção `servidor` compartilha um único servidor (`ServerFixture`). Ele usa `AcceptAnyPassword = true` e `SmtpRequireAuth = false`, diferente do padrão de produção.
- **Mudar configuração num teste:** use `using var settings = fx.OverrideSettings(s => ...)`. Ele restaura o original no fim e reinicia os serviços se alguma porta mudou. `settings.Apply(...)` muda de novo no meio do teste.
- **Auxiliares:**
  - **Socket cru:** use `RawLineClient`, com `SmtpAsync`, `ImapAsync` e `ReadSmtpReplyAsync`.
  - **Certificado:** use `fx.IsServerCertificate`; nunca aceite qualquer certificado.
  - **Ferramentas MCP:** use `CallJsonAsync`/`CallTextAsync`/`CallBlocksAsync`, que já conferem se a ferramenta falhou.
- **Testes com servidor próprio:** `PersistenceTests`, `DataRemovalTests` e `McpStdioTests` sobem o próprio servidor com `ServerFixture.OnlySmtpAndWeb` e os padrões de produção. Por isso fazem login com `teste@teste.local`/`teste`. O `McpStdioTests` executa o `FakeServerEmail.exe` copiado para a pasta de saída dos testes.
- **MailKit:** o MailKit transforma o `530` do SMTP em `ServiceNotAuthenticatedException`.
- **Nomes:** os testes têm nomes em pt-br com sublinhados (`Sem_STARTTLS_e_so_PLAIN_quando_configurado`). O código de produção usa nomes em inglês.
