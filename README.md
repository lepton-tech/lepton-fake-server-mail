<div align="center">

<img src="docs/imagens/logo.svg" width="96" alt="">

# Fake Server Email

**Um servidor de e-mail de mentira para testar sistemas sem mandar e-mail de verdade para ninguém.**

Todo e-mail que o sistema testado enviar fica preso aqui, numa caixa de entrada no navegador.<br>
É um único arquivo para Windows: não precisa instalar nada nem configurar servidor.

<a href="https://github.com/lepton-tech/lepton-fake-server-mail/releases/latest/download/FakeServerEmail.exe"><img src="https://img.shields.io/badge/Baixar_para_Windows-FakeServerEmail.exe-2f6fde?style=for-the-badge&logo=windows&logoColor=white" height="48" alt="Baixar para Windows: FakeServerEmail.exe"></a>

[![Versão](https://img.shields.io/github/v/release/lepton-tech/lepton-fake-server-mail?label=vers%C3%A3o&color=2f6fde)](https://github.com/lepton-tech/lepton-fake-server-mail/releases/latest)
[![Windows 10 e 11](https://img.shields.io/badge/Windows-10_%7C_11-0078D6?logo=windows&logoColor=white)](#qual-arquivo-baixar)
[![Licença MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-555)](LICENSE)

[Qual arquivo baixar](#qual-arquivo-baixar) · [Primeiro uso](#abrir-pela-primeira-vez) · [Problemas comuns](#quando-algo-não-funciona) · [Novidades](CHANGELOG.md)

**Português** · [English](README.en.md)

<br>

<img src="docs/imagens/caixa-de-entrada.png" width="900" alt="Caixa de entrada do Fake Server Email mostrando um e-mail de confirmação de cadastro">

</div>

## Comece em 3 passos

1. **Baixe** o `FakeServerEmail.exe` no botão azul acima.
2. **Abra** o arquivo com dois cliques. A caixa de entrada abre no navegador.
3. **Configure** o sistema que você vai testar para enviar e-mails para o servidor `localhost`, porta `25`, usuário `teste@teste.local` e senha `teste`.

Pronto: cada e-mail que o sistema enviar aparece na caixa de entrada na hora. Nenhum e-mail sai do seu computador.

## Para que serve

- ✉️ **Conferir os e-mails do sistema** (cadastro, recuperação de senha, avisos, relatórios) sem risco de chegar a clientes reais.
- 👀 **Ver o e-mail como o destinatário veria:** HTML, texto, anexos e cabeçalhos.
- 🧪 **Simular situações de servidor real:** senha errada, servidor que exige login, servidor sem criptografia, login por token OAuth.
- 🔎 **Descobrir por que um envio falhou**, com o registro completo de cada conexão.

## Baixar

### Qual arquivo baixar

**Na dúvida, baixe o `FakeServerEmail.exe`: ele serve para quase todos os computadores.**

| Seu computador | Arquivo para baixar |
|---|---|
| Windows 10 ou 11 comum (64 bits). **É o caso da maioria.** | ⬇️ [**FakeServerEmail.exe**](https://github.com/lepton-tech/lepton-fake-server-mail/releases/latest/download/FakeServerEmail.exe) |
| Windows com processador ARM (Snapdragon, Surface Pro X) | ⬇️ [FakeServerEmail-arm64.exe](https://github.com/lepton-tech/lepton-fake-server-mail/releases/latest/download/FakeServerEmail-arm64.exe) |
| Windows de 32 bits (computadores antigos) | ⬇️ [FakeServerEmail-x86.exe](https://github.com/lepton-tech/lepton-fake-server-mail/releases/latest/download/FakeServerEmail-x86.exe) |

Os links baixam sempre a versão mais recente. Cada arquivo tem cerca de 50 MB.

<details>
<summary><b>Como descobrir qual é o meu Windows</b></summary>

<br>

Abra **Iniciar → Configurações → Sistema → Sobre** e veja a linha **Tipo de sistema**:

| Se aparecer... | Baixe |
|---|---|
| "Sistema operacional de 64 bits, processador baseado em x64" | `FakeServerEmail.exe` |
| "processador baseado em ARM" | `FakeServerEmail-arm64.exe` |
| "Sistema operacional de 32 bits" | `FakeServerEmail-x86.exe` |

</details>

Versões anteriores e o que mudou em cada uma estão na [página de versões](https://github.com/lepton-tech/lepton-fake-server-mail/releases) e no [CHANGELOG](CHANGELOG.md).

### Se o navegador avisar sobre o download

Como o programa é novo e não tem assinatura digital, o navegador pode dizer que o arquivo "não é baixado com frequência". Isso é esperado.

- **Edge:** na lista de downloads, clique nos três pontinhos (**…**) ao lado do arquivo, depois em **Manter**, em **Mostrar mais** e em **Manter mesmo assim**.
- **Chrome:** na lista de downloads, escolha a opção de manter o arquivo.

## Abrir pela primeira vez

1. Guarde o arquivo num lugar fácil de achar, como a **Área de Trabalho**. Não há instalação: esse arquivo é o programa.
2. Dê dois cliques nele.
3. Se aparecer a tela azul **"O Windows protegeu o computador"**, clique em **Mais informações** e depois em **Executar assim mesmo**. Isso só acontece na primeira vez.
4. Abre uma janela preta, e o navegador mostra a caixa de entrada em `http://localhost:5080`.

A janela preta mostra o endereço da caixa de entrada e as portas em uso:

```
  Fake Server Email 1.0.0
  Servidor de e-mail falso para testes: SMTP, POP3, IMAP e OAuth 2.0

  Caixa de entrada: http://localhost:5080

  Serviço           Porta   Segurança
  SMTP              25      STARTTLS opcional
  SMTP submissão    587     STARTTLS opcional
```

> [!IMPORTANT]
> **Deixe a janela preta aberta enquanto estiver testando.** Fechar a janela desliga o servidor. Se abrir o arquivo de novo com ele já rodando, ele só abre a caixa de entrada que já existe.

## Configurar o sistema que você vai testar

No sistema testado, procure a configuração de envio de e-mail. Costuma se chamar "SMTP", "servidor de e-mail" ou "e-mail de saída". Preencha assim:

| Campo | Valor |
|---|---|
| Servidor (host) | `localhost` |
| Porta | `25` ou `587` |
| Usuário | `teste@teste.local` |
| Senha | `teste` |
| Segurança (SSL/TLS) | nenhuma, ou STARTTLS aceitando certificado inválido |

Se você não encontrar onde fica essa configuração, peça a quem desenvolve o sistema: são os mesmos campos de qualquer servidor de e-mail.

A aba **Como conectar** mostra os valores reais do seu computador. Se a porta 25 estiver ocupada, por exemplo, o servidor usa a 2025, e é lá que você vê isso. A aba também traz configurações prontas para colar em `.env`, .NET, Spring Boot, Django, Outlook e Thunderbird, e exemplos de código em C#, Java, Python, Node.js e PowerShell.

<p align="center"><img src="docs/imagens/como-conectar.png" width="900" alt="Aba Como conectar com servidor, usuário, senha e portas"></p>

> [!TIP]
> **O sistema roda em outra máquina ou num container Docker?** Marque **Aceitar conexões de outras máquinas e de containers Docker** em **Configurações → Portas**. No Docker, use `host.docker.internal` como servidor.

<details>
<summary><b>Todos os serviços e portas</b></summary>

<br>

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

SMTP e SMTP submissão funcionam igual neste servidor. Os dois existem porque cada sistema vem configurado com uma porta diferente. Para ligar ou desligar um serviço, use as caixas em **Configurações → Portas**. Ao ligar, ele usa a porta sugerida, e dá para trocá-la.

</details>

## As telas

- **Caixa de entrada:** os e-mails recebidos, cada um em HTML, texto, cabeçalhos e código-fonte, com anexos para baixar. O botão **Enviar teste** manda uma mensagem de exemplo para conferir se tudo funciona.
- **OAuth:** cria e revoga tokens para testar login por OAuth 2.0.
- **Conexões:** o registro de cada conexão, linha por linha. É aqui que se descobre por que um envio falhou.
- **Como conectar:** os dados de conexão deste computador e exemplos prontos.
- **Configurações:** usuários e senhas, regras de envio, criptografia, portas, certificado e dados guardados.

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
| HTTPS confiável para o IIS, serviços do Windows ou o Firefox | **Certificado TLS → Confiar para todo o computador** |

## Quando algo não funciona

<details>
<summary><b>O e-mail não chegou na caixa de entrada</b></summary>

<br>

Abra a aba **Conexões**. Ela mostra cada tentativa do sistema de falar com o servidor, linha por linha.

- **Não aparece nenhuma conexão:** o sistema não está chegando ao servidor. Confira se a janela preta está aberta e se o servidor e a porta do sistema são os mesmos da aba **Como conectar**. Se o sistema roda em outra máquina ou no Docker, veja a dica em [Configurar o sistema](#configurar-o-sistema-que-você-vai-testar).
- **A conexão aparece com falhas (em vermelho):** a linha da falha diz o motivo. No exemplo abaixo, o sistema usou a senha errada.

<p align="center"><img src="docs/imagens/conexoes.png" width="900" alt="Aba Conexões mostrando uma tentativa com senha incorreta"></p>

</details>

<details>
<summary><b>O navegador não abriu a caixa de entrada</b></summary>

<br>

Abra o navegador e digite `http://localhost:5080`. Se a porta 5080 estiver ocupada, o programa usa outra, e o endereço certo aparece na janela preta, na linha **Caixa de entrada**.

</details>

<details>
<summary><b>Apareceu "O Windows protegeu o computador"</b></summary>

<br>

Clique em **Mais informações** e depois em **Executar assim mesmo**. O aviso aparece porque o programa não tem assinatura digital, não porque há algo errado com ele.

</details>

<details>
<summary><b>O antivírus bloqueou ou apagou o arquivo</b></summary>

<br>

Alguns antivírus desconfiam de programas sem assinatura digital. Peça à equipe de TI para liberar o `FakeServerEmail.exe` e baixe o arquivo de novo.

</details>

<details>
<summary><b>O sistema diz que a porta está em uso, ou não conecta na porta 25</b></summary>

<br>

Quando uma porta já está ocupada por outro programa, o Fake Server Email usa outra sozinho (a 25 vira 2025, por exemplo). A porta real aparece na janela preta e na aba **Como conectar**. Use essa porta no sistema testado.

</details>

<details>
<summary><b>O sistema reclama de certificado, SSL ou TLS</b></summary>

<br>

O caminho mais simples é configurar o sistema sem segurança (SSL/TLS "nenhuma"). Se o sistema exigir conexão segura, use STARTTLS aceitando certificado inválido, ou marque o certificado como confiável em **Configurações → Certificado TLS** (veja [Certificado](#certificado-https-e-tls)).

</details>

<details>
<summary><b>O Windows perguntou sobre o Firewall</b></summary>

<br>

Isso só acontece quando **Aceitar conexões de outras máquinas e de containers Docker** está ligado. Clique em **Permitir** para o sistema testado conseguir chegar ao servidor por outra máquina ou pelo Docker.

</details>

<details>
<summary><b>Como atualizar para uma versão nova</b></summary>

<br>

Feche a janela preta, baixe o arquivo de novo pelo [botão do topo](#qual-arquivo-baixar) e substitua o antigo. Os e-mails e as configurações continuam, porque ficam guardados fora do arquivo. A versão em uso aparece na primeira linha da janela preta, e o que mudou em cada versão está no [CHANGELOG](CHANGELOG.md).

</details>

## Certificado (HTTPS e TLS)

As conexões com criptografia usam um certificado que o programa cria para `localhost` no primeiro uso. Para os sistemas aceitarem sem aviso, há duas opções em **Configurações → Certificado TLS**:

- **Só para o meu usuário:** vale para os programas que você abre (Chrome, Edge, aplicativos .NET). Não precisa de administrador; o Windows mostra uma confirmação.
- **Para todo o computador:** vale também para o IIS, serviços do Windows, outros usuários e o Firefox (quando ele usa os certificados do Windows). O Windows pede permissão de administrador.

O certificado serve só para `localhost` e não é de autoridade certificadora: mesmo que alguém copie a chave, não consegue usá-la para outros sites. Para instalar pela linha de comando, baixe o `.cer` na mesma tela e rode, como administrador:

```powershell
Import-Certificate -FilePath .\fake-server-email-localhost.cer -CertStoreLocation Cert:\LocalMachine\Root
```

## Dados e remoção

Os e-mails, configurações, certificado e chave do OAuth ficam em `%LOCALAPPDATA%\FakeServerEmail`. Cada e-mail é um arquivo `.eml`.

Em **Configurações → Dados guardados** dá para abrir essa pasta, apagar só os e-mails e tokens, ou remover tudo o que o programa gravou. A remoção também tira o certificado da lista de confiáveis do Windows (do usuário ou do computador). Depois disso, basta apagar o `.exe`: nada mais fica no computador.

Opções de linha de comando:

| Opção | O que faz |
|---|---|
| `--data <pasta>` | guarda os dados em outra pasta |
| `--no-browser` | não abre o navegador ao iniciar |
| `--mcp` | modo MCP por stdio, para agentes de IA (veja abaixo) |

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

## Detalhes técnicos

- **Autenticação:** mecanismos PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER, com STARTTLS (STLS no POP3) nas portas sem TLS. Dá para escolher quais mecanismos anunciar e desligar o STARTTLS, para imitar servidores mais limitados.
- **OAuth 2.0:** `authorization_code` com PKCE, `refresh_token`, `client_credentials`, `password` e `device_code`, com discovery OIDC, JWKS e rotas no estilo Microsoft (`/{tenant}/oauth2/v2.0/token`). Os tokens são JWT assinados, e os serviços de e-mail recusam token expirado, revogado ou de outro usuário.
- **Segurança da interface:** a API recusa pedidos vindos de outros sites abertos no navegador e, com o acesso pela rede desligado, só atende endereços deste computador. Anexos HTML e SVG de e-mails capturados abrem isolados, sem rodar scripts.
- **SMTP:** ESMTP com SIZE, 8BITMIME, SMTPUTF8, PIPELINING, CHUNKING (BDAT) e códigos de status estendidos.
- **IMAP:** IMAP4rev1 com IDLE, UIDPLUS, MOVE, SPECIAL-USE e pastas (Sent, Drafts, Trash, Junk, Archive).

## Para desenvolvedores

Requer Windows e o [.NET SDK 10](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src\FakeServerEmail          # roda a partir do código
dotnet test                                       # testes de ponta a ponta
.\scripts\publicar.ps1                            # gera dist\FakeServerEmail.exe (x64)
.\scripts\publicar.ps1 -Runtime win-x64,win-x86,win-arm64 # também 32 bits e ARM
```

```
src/FakeServerEmail/
  Program.cs    ponto de entrada: escolhe o modo (janela, MCP por stdio ou comando de administrador)
  Hosting/      modo janela: linha de comando, pasta de dados, console e encerramento
  Core/         configurações, armazenamento das mensagens, certificado, log das conexões
  Protocols/    SMTP, POP3, IMAP e SASL, cada um sobre o mesmo serviço TCP
  OAuth/        servidor OAuth 2.0 e emissão/validação dos tokens
  Web/          API usada pela interface e eventos em tempo real
  Mcp/          ferramentas MCP (HTTP e stdio)
  wwwroot/      interface web, embutida no .exe
tests/FakeServerEmail.Tests/  testes com clientes reais (MailKit, MSAL, cliente MCP)
scripts/      publicação, preparo e lançamento de versões
```

O build usa os analisadores do .NET no nível `latest-recommended` e trata qualquer aviso como erro. O estilo do código está no `.editorconfig`, e as versões dos pacotes, no `Directory.Packages.props`.

A interface usa [Alpine.js](https://alpinejs.dev/) 3.17.4 (cópia local em `wwwroot/vendor/`, sem CDN) e módulos ES, sem etapa de compilação. Cada tela é um par `views/<tela>.html` + `js/views/<tela>.js`.

A arquitetura e as convenções do projeto estão no [CLAUDE.md](CLAUDE.md). O fluxo de branches, PRs, changelog e versões está no [CONTRIBUTING.md](CONTRIBUTING.md): `main` e `develop` só recebem PR, e a release com os executáveis é gerada localmente pelos scripts de `scripts/`.

## Licença

[MIT](LICENSE).
