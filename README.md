# Fake Server Email

Um servidor de e-mail **de mentira** para testar sistemas sem mandar e-mail de verdade para ninguém.

Você aponta o sistema que está testando para o Fake Server Email, e todo e-mail que ele enviar fica preso aqui, visível numa caixa de entrada no navegador. Nada sai para a internet.

É um único arquivo `FakeServerEmail.exe` para Windows. Não precisa instalar nada: dois cliques e ele está funcionando.

## Para que serve

- Conferir se o sistema envia os e-mails que deveria (cadastro, recuperação de senha, avisos, relatórios) sem risco de chegar a clientes reais.
- Ver o e-mail exatamente como o destinatário veria: HTML, texto, anexos e cabeçalhos.
- Simular situações de servidor real: senha errada, servidor que exige login, servidor sem criptografia, login por token OAuth.
- Descobrir por que o sistema não conseguiu enviar, com o registro completo de cada conexão.

## Como usar

1. Baixe o `FakeServerEmail.exe` na página de **Releases** deste repositório. Funciona no Windows 10 e 11.
2. Dê dois cliques no arquivo. Uma janela preta (o console) abre e o navegador mostra a caixa de entrada em `http://localhost:5080`.
   - Como o arquivo não é assinado digitalmente, o Windows pode mostrar "O Windows protegeu o computador". Clique em **Mais informações** e depois em **Executar assim mesmo**.
3. No sistema que você quer testar, configure o envio de e-mail (SMTP) com estes dados:

   | Campo | Valor |
   |---|---|
   | Servidor (host) | `localhost` |
   | Porta | `25` ou `587` |
   | Usuário | `teste@teste.local` |
   | Senha | `teste` |
   | Segurança (SSL/TLS) | nenhuma, ou STARTTLS aceitando certificado inválido |

4. Faça o sistema enviar um e-mail. Ele aparece na **Caixa de entrada** na hora.

Para encerrar, feche a janela preta. Se abrir o arquivo de novo enquanto ele já estiver rodando, ele só abre a caixa de entrada que já existe.

> **Dica:** a aba **Como conectar** mostra os valores reais deste computador (se a porta 25 estiver ocupada, por exemplo, o servidor usa a 2025) e exemplos prontos para colar em C#, Java (Spring Boot), Python, Node.js e PowerShell, além de instruções para Outlook e Thunderbird.

Se o sistema testado roda em outra máquina ou num container Docker, marque **Aceitar conexões de outras máquinas e de containers Docker** em Configurações → Portas. No Docker, use `host.docker.internal` como servidor.

## O que vem ligado

Por padrão só o envio de e-mails vem ligado. Os outros serviços ficam desligados até você precisar deles.

| Serviço | Para quê | Porta | Ligado por padrão |
|---|---|---|---|
| SMTP | enviar e-mails | 25 | sim |
| SMTP submissão | enviar e-mails | 587 | sim |
| SMTPS | enviar, com criptografia desde o início | 465 (sugerida) | não |
| POP3 / POP3S | o sistema testado ler e-mails | 110 / 995 (sugeridas) | não |
| IMAP / IMAPS | o sistema testado ler e-mails | 143 / 993 (sugeridas) | não |
| Interface web | esta tela | 5080 | sempre |
| HTTPS | login OAuth com bibliotecas que exigem conexão segura | 5443 | sim |

Neste servidor, SMTP e SMTP submissão funcionam igual. Os dois existem porque cada sistema vem configurado com uma porta diferente. Para ligar ou desligar um serviço, use as caixas em **Configurações → Portas**. Ao ligar, ele usa a porta sugerida, e dá para trocá-la.

## As telas

- **Caixa de entrada:** lista os e-mails recebidos e mostra cada um em HTML, texto, cabeçalhos e código-fonte, com anexos para baixar. O botão **Enviar teste** manda uma mensagem de exemplo para conferir se tudo funciona.
- **OAuth:** emite e revoga tokens para testar login por OAuth 2.0.
- **Conexões:** o registro de cada conexão, linha por linha. É aqui que se descobre por que um envio falhou.
- **Como conectar:** os dados de conexão deste computador e exemplos de código.
- **Configurações:** usuários e senhas, regras de envio, STARTTLS, mecanismos de login, portas, certificado e dados guardados.

## Cenários de teste comuns

| Quero testar... | Onde mudar |
|---|---|
| senha errada | já é o padrão: só entra `teste@teste.local` com a senha `teste`. Cadastre outros em **Usuário e senha** |
| sistema que envia sem login | desmarque **Exigir login para enviar pelo SMTP** |
| servidor que aceita qualquer senha | **Usuário e senha → Aceitar qualquer usuário e senha** |
| servidor sem criptografia (sem STARTTLS) | desmarque **Oferecer STARTTLS** |
| servidor que só aceita um tipo de login (só PLAIN, por exemplo) | **STARTTLS e mecanismos de login** |
| sistema que lê e-mails por IMAP ou POP3 | ligue o serviço em **Portas** |
| login com token OAuth (XOAUTH2, OAUTHBEARER) | aba **OAuth** |
| mensagem grande demais | **Tamanho máximo da mensagem** |

## Dados e remoção

Os e-mails, configurações, certificado e chave do OAuth ficam em `%LOCALAPPDATA%\FakeServerEmail`. Cada e-mail é um arquivo `.eml`.

Em **Configurações → Dados guardados** dá para abrir essa pasta, apagar só os e-mails e tokens, ou remover tudo o que o programa gravou. Depois de remover tudo, basta apagar o `.exe`: nada mais fica no computador.

Opções de linha de comando:

| Opção | O que faz |
|---|---|
| `--data <pasta>` | guarda os dados em outra pasta |
| `--no-browser` | não abre o navegador ao iniciar |
| `--mcp` | modo MCP por stdio, para agentes de IA (veja abaixo) |

## Detalhes técnicos

- **Autenticação:** mecanismos PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER, com STARTTLS (STLS no POP3) nas portas sem TLS. Dá para escolher quais mecanismos anunciar e desligar o STARTTLS, para imitar servidores mais limitados.
- **OAuth 2.0:** `authorization_code` com PKCE, `refresh_token`, `client_credentials`, `password` e `device_code`, com discovery OIDC, JWKS e rotas no estilo Microsoft (`/{tenant}/oauth2/v2.0/token`). Os tokens são JWT assinados, e os serviços de e-mail recusam token expirado, revogado ou de outro usuário.
- **TLS:** certificado autoassinado para `localhost`, gerado no primeiro uso. Em **Configurações → Certificado TLS** dá para marcá-lo como confiável no seu usuário do Windows.
- **SMTP:** ESMTP com SIZE, 8BITMIME, SMTPUTF8, PIPELINING, CHUNKING (BDAT) e códigos de status estendidos.
- **IMAP:** IMAP4rev1 com IDLE, UIDPLUS, MOVE, SPECIAL-USE e pastas (Sent, Drafts, Trash, Junk, Archive).

## Agentes de IA (MCP)

O mesmo executável é um servidor [MCP](https://modelcontextprotocol.io/), feito com o SDK oficial para .NET (`ModelContextProtocol`). Com ele, agentes como Claude Code, Claude Desktop, Cursor, VS Code e Codex leem, esperam e enviam e-mails, geram tokens e mudam as regras do servidor. Há duas formas de conectar:

- **HTTP:** `http://localhost:5080/mcp`, enquanto o aplicativo estiver aberto.
- **stdio:** `FakeServerEmail.exe --mcp`. O agente inicia o processo. Se o aplicativo já estiver aberto, ele usa o mesmo servidor; se não estiver, sobe um em segundo plano e encerra junto com o agente.

```powershell
claude mcp add --transport http fake-email http://localhost:5080/mcp
claude mcp add fake-email -- "C:\caminho\FakeServerEmail.exe" --mcp
```

A aba **Como conectar** mostra a configuração pronta para cada agente, já com o caminho real do executável.

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
| `update_settings` | usuários e senhas, exigir login no SMTP, STARTTLS, mecanismos de login, portas (0 desliga o serviço), validade dos tokens etc. |

## Para desenvolvedores

Requer Windows e o [.NET SDK 10](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src\FakeServerEmail          # roda a partir do código
dotnet test                                       # testes de ponta a ponta
.\publicar.ps1                                    # gera dist\FakeServerEmail.exe (x64)
.\publicar.ps1 -Runtime win-x64,win-x86,win-arm64 # também 32 bits e ARM
```

```
src/FakeServerEmail/
  Core/         configurações, armazenamento das mensagens, certificado, log das conexões
  Protocols/    SMTP, POP3, IMAP e SASL, cada um sobre o mesmo serviço TCP
  OAuth/        servidor OAuth 2.0 e emissão/validação dos tokens
  Web/          API usada pela interface e eventos em tempo real
  Mcp/          ferramentas MCP (HTTP e stdio)
  wwwroot/      interface web, embutida no .exe
tests/FakeServerEmail.Tests/  testes com clientes reais (MailKit, MSAL, cliente MCP)
```

A interface usa [Alpine.js](https://alpinejs.dev/) 3.17.4 (cópia local em `wwwroot/vendor/`, sem CDN) e módulos ES, sem etapa de compilação. Cada tela é um par `views/<tela>.html` + `js/views/<tela>.js`.

A arquitetura e as convenções do projeto estão descritas no [CLAUDE.md](CLAUDE.md).
