---
name: pr
description: Envia a mudança atual para o GitHub e abre uma PR para a develop do lepton-tech/lepton-fake-server-mail, criando a branch quando preciso. Use quando pedirem para subir, enviar, commitar e abrir PR ou mandar uma mudança para o GitHub.
---

# Abrir PR para a develop

O fluxo completo está no `CONTRIBUTING.md`. A `main` e a `develop` só recebem PR: nunca faça commit nem push nelas.

Se o `gh` não estiver no PATH, use `& "$env:ProgramFiles\GitHub CLI\gh.exe"`. Se `gh auth status` falhar, peça ao usuário para rodar `gh auth login`.

1. Veja a branch atual e as alterações (`git status`, `git branch --show-current`).
   - Na `develop` ou na `main`: `git fetch origin` e `git switch --no-track -c <prefixo>/<nome-curto> origin/develop`. As alterações não commitadas vão junto. Prefixos: `feature/`, `fix/`, `docs/`.
   - Numa branch que já tem PR aberta (`gh pr view`), só faça commit e push: a PR se atualiza sozinha.
2. Se a mudança aparece para quem usa o programa, acrescente uma linha em pt-br simples no `CHANGELOG.md`, na seção `## Não lançado`, embaixo de `### Novidades`, `### Mudanças` ou `### Correções` (crie o título se faltar). Escreva para quem usa o programa, não para quem lê o código. Mudança só interna (testes, refatoração, documentação de desenvolvimento) não entra.
3. Rode `dotnet build`, `dotnet test` e `dotnet format whitespace --verify-no-changes`. Não há CI, então isso é a única verificação: se algo falhar, corrija antes de seguir.
4. Faça o commit com mensagem em pt-br, no estilo do `git log`.
5. `git push -u origin HEAD`.
6. `gh pr create --base develop`, com título e descrição em pt-br: o que mudou, por quê e como foi testado.
7. Mostre o link da PR e lembre que ela precisa da aprovação de outra pessoa.

Só faça o merge quando o usuário pedir e a PR estiver aprovada: `gh pr merge <número> --merge --delete-branch`.
