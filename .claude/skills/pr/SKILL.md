---
name: pr
description: Envia a mudança atual para o GitHub e abre uma PR para a develop do lepton-tech/lepton-fake-server-mail, criando a branch quando preciso. Use quando pedirem para subir, enviar, commitar e abrir PR ou mandar uma mudança para o GitHub.
---

# Abrir PR para a develop

O fluxo completo está no `CONTRIBUTING.md`. A `main` e a `develop` só recebem PR: nunca faça commit nem push nelas.

Se o `gh` não estiver no PATH, use `& "$env:ProgramFiles\GitHub CLI\gh.exe"`. Se `gh auth status` falhar, peça ao usuário para rodar `gh auth login`.

1. Veja a branch atual e as alterações (`git status`, `git branch --show-current`).
   - Na `develop` ou na `main`: `git fetch origin` e `git switch -c <prefixo>/<nome-curto> origin/develop`. As alterações não commitadas vão junto. Prefixos: `feature/`, `fix/`, `docs/`.
   - Numa branch que já tem PR aberta (`gh pr view`), só faça commit e push: a PR se atualiza sozinha.
2. Rode `dotnet build`, `dotnet test` e `dotnet format whitespace --verify-no-changes`. Não há CI, então isso é a única verificação: se algo falhar, corrija antes de seguir.
3. Faça o commit com mensagem em pt-br, no estilo do `git log`.
4. `git push -u origin HEAD`.
5. `gh pr create --base develop`, com título e descrição em pt-br: o que mudou, por quê e como foi testado.
6. Mostre o link da PR e lembre que ela precisa da aprovação de outra pessoa.

Só faça o merge quando o usuário pedir e a PR estiver aprovada: `gh pr merge <número> --merge --delete-branch`.
