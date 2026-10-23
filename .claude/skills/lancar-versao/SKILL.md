---
name: lancar-versao
description: Lança uma versão do Fake Server Email no GitHub, com tag e os executáveis como assets da release, levando antes a develop para a main por PR. Use quando pedirem para lançar, publicar ou gerar uma versão, release ou tag.
---

# Lançar versão

O fluxo completo está no `CONTRIBUTING.md`. Tudo roda localmente com o `gh`; não há pipeline.

Se o `gh` não estiver no PATH, use `& "$env:ProgramFiles\GitHub CLI\gh.exe"`. Se `gh auth status` falhar, peça ao usuário para rodar `gh auth login`.

1. **Versão.** Compare a `<Version>` de `src/FakeServerEmail/FakeServerEmail.csproj` na `origin/develop` com a última release (`gh release list --limit 1`). Se a versão já tem release, leia o `## Não lançado` do `CHANGELOG.md` na `origin/develop` e sugira a próxima: só `### Correções` sobe o terceiro número, `### Novidades` ou `### Mudanças` sobem o segundo, algo que quebra o uso sobe o primeiro. Se o `Não lançado` estiver vazio, compare com `git log <tag>..origin/develop` e registre o que falta por PR, como na skill `/pr`. Confirme o número com o usuário e rode `.\preparar-versao.ps1 X.Y.Z`: ele cria a branch `release/X.Y.Z`, muda a versão e o changelog e abre a PR para a `develop`. Nunca mude a `<Version>` à mão.
2. **develop → main.** Se `git log origin/main..origin/develop` não estiver vazio, procure uma PR aberta (`gh pr list --base main --head develop`). Se não houver, abra uma com `gh pr create --base main --head develop --title "Versão X.Y.Z"` e liste na descrição as PRs que entram. Ela precisa da aprovação de outra pessoa. Quando estiver aprovada e o usuário pedir, faça o merge com `gh pr merge <número> --merge`: nunca squash nem rebase, que fazem a `main` e a `develop` divergirem, e nunca `--delete-branch`.
3. **Release.** Com a versão já na `main`, rode `git switch main`, `git pull` e `.\lancar-versao.ps1` no PowerShell 7. O script testa, gera os três executáveis e publica a release `v<Version>` com eles. Leva alguns minutos: rode em segundo plano.
4. Mostre o link da release. Se o script falhar, a mensagem dele diz o que fazer.
