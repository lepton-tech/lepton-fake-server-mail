---
name: lancar-versao
description: Lança uma versão do Fake Server Email no GitHub, com tag e os executáveis como assets da release, levando antes a develop para a main por PR. Use quando pedirem para lançar, publicar ou gerar uma versão, release ou tag.
---

# Lançar versão

O fluxo completo está no `CONTRIBUTING.md`. Tudo roda localmente com o `gh`; não há pipeline.

Se o `gh` não estiver no PATH, use `& "$env:ProgramFiles\GitHub CLI\gh.exe"`. Se `gh auth status` falhar, peça ao usuário para rodar `gh auth login`.

1. **Versão.** Compare a `<Version>` de `src/FakeServerEmail/FakeServerEmail.csproj` na `origin/develop` com a última release (`gh release list --limit 1`). Se a versão já tem tag, sugira a próxima pelo que entrou desde a última tag (`git log <tag>..origin/develop`): `fix` sobe o terceiro número, `feature` o segundo. Confirme com o usuário e faça a mudança por PR para a `develop`, como na skill `/pr`.
2. **develop → main.** Se `git log origin/main..origin/develop` não estiver vazio, procure uma PR aberta (`gh pr list --base main --head develop`). Se não houver, abra uma com `gh pr create --base main --head develop --title "Versão X.Y.Z"` e liste na descrição as PRs que entram. Ela precisa da aprovação de outra pessoa. Quando estiver aprovada e o usuário pedir, faça o merge com `gh pr merge <número> --merge`: nunca squash nem rebase, que fazem a `main` e a `develop` divergirem, e nunca `--delete-branch`.
3. **Release.** Com a versão já na `main`, rode `git switch main`, `git pull` e `.\lancar-versao.ps1` no PowerShell 7. O script testa, gera os três executáveis e publica a release `v<Version>` com eles. Leva alguns minutos: rode em segundo plano.
4. Mostre o link da release. Se o script falhar, a mensagem dele diz o que fazer.
