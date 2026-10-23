# Changelog

O que mudou em cada versão do Fake Server Email. O terceiro número da versão sobe com correções, o segundo com novidades e o primeiro com mudanças que quebram o uso.

## Não lançado

## 1.0.0 - 2026-10-23

Primeira versão.

### Novidades

- Servidor de e-mail de mentira num único `FakeServerEmail.exe` para Windows, sem instalação, com versões para 64 bits, 32 bits e ARM.
- Recebe e-mails por SMTP (portas 25 e 587) e mostra cada um numa caixa de entrada no navegador, com HTML, texto, anexos e cabeçalhos.
- POP3, IMAP e SMTPS para o sistema testado ler e-mails ou usar criptografia desde o início, ligados em **Configurações → Portas**.
- Login por usuário e senha ou por token OAuth 2.0 (XOAUTH2 e OAUTHBEARER), com servidor OAuth embutido.
- Aba **Conexões** com o registro de cada conexão, para descobrir por que um envio falhou.
- Aba **Como conectar** com os dados reais do computador e exemplos prontos em C#, Java, Python, Node.js e PowerShell.
- Certificado para `localhost` que pode ser marcado como confiável só para o seu usuário ou para todo o computador.
- Servidor MCP para agentes de IA lerem, esperarem e enviarem e-mails.
