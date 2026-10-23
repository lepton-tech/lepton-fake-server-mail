# Como contribuir

## Regras do repositório

- `main` e `develop` são protegidas. Ninguém faz commit nem push direto nelas, e elas não podem ser apagadas nem receber push forçado.
- Toda mudança entra por pull request (PR), e cada PR precisa da aprovação de uma pessoa que não seja o autor. Quem está na lista de bypass das regras (hoje `lenerson` e `ramon-oliveira-lepton`) pode fazer o merge sem aprovação com `gh pr merge <número> --merge --admin`, mas também não consegue fazer push direto.
- O código só chega à `main` passando pela `develop`.

```
feature/minha-mudanca ──PR──▶ develop ──PR──▶ main ──▶ tag vX.Y.Z + release com os .exe
```

Não há pipeline no GitHub Actions. Build, testes e publicação rodam no seu computador, e o GitHub CLI (`gh`) cuida das PRs e das releases.

## Preparar o computador (uma vez)

```powershell
winget install --id GitHub.cli
gh auth login
```

Depois de instalar, abra um terminal novo (ou reinicie o VS Code) para o `gh` aparecer no PATH. No `gh auth login`, entre com a conta que tem acesso à organização `lepton-tech`.

O script de versão precisa do PowerShell 7 (`pwsh`) e do [.NET SDK 10](https://dotnet.microsoft.com/download).

## 1. Fazer uma mudança

Crie a branch a partir da `develop` atualizada:

```powershell
git switch develop
git pull
git switch -c feature/descricao-curta
```

Use `feature/` para novidade, `fix/` para correção e `docs/` para documentação.

Antes de abrir a PR, rode o que o GitHub não vai rodar por você:

```powershell
dotnet build
dotnet test
dotnet format whitespace --verify-no-changes
```

Envie e abra a PR para a `develop`:

```powershell
git push -u origin HEAD
gh pr create --base develop
```

Depois da aprovação:

```powershell
gh pr merge <número> --merge --delete-branch
```

## 2. Levar a develop para a main

```powershell
gh pr create --base main --head develop --title "Versão X.Y.Z"
```

Depois da aprovação:

```powershell
gh pr merge <número> --merge
```

- Use sempre merge commit (`--merge`). Squash e rebase criam na `main` commits que não existem na `develop`, e a PR seguinte da `develop` para a `main` passa a mostrar conflitos e mudanças que já entraram.
- Não use `--delete-branch` nessa PR: a `develop` não pode ser apagada.

## 3. Lançar uma versão

A versão é a `<Version>` de `src/FakeServerEmail/FakeServerEmail.csproj` e vira a tag `v<Version>`.

1. Numa PR para a `develop` (passo 1), aumente a `<Version>`: `1.0.1` para correção, `1.1.0` para novidade, `2.0.0` para mudança que quebra o uso.
2. Leve a `develop` para a `main` (passo 2).
3. Com a `main` atualizada, rode o script:

   ```powershell
   git switch main
   git pull
   .\lancar-versao.ps1
   ```

O script leva alguns minutos e faz, nesta ordem:

1. Confere que não há alterações locais, que o código é exatamente o da `main` do GitHub e que a tag ainda não existe.
2. Roda `dotnet test`.
3. Gera os três executáveis com o `publicar.ps1`.
4. Cria a release como rascunho, envia os executáveis e só então publica. É nesse momento que a tag é criada, no commit da `main`.

| Arquivo da release | Para |
|---|---|
| `FakeServerEmail.exe` | Windows 10 e 11 de 64 bits (a maioria dos computadores) |
| `FakeServerEmail-x86.exe` | Windows de 32 bits |
| `FakeServerEmail-arm64.exe` | Windows em processador ARM |

As notas da release trazem essa tabela e a lista das PRs desde a versão anterior.

Se o script parar no meio, nada foi publicado. A mensagem de erro diz o que fazer, por exemplo apagar um rascunho que ficou para trás com `gh release delete vX.Y.Z --yes`.

## Com o Claude Code

O repositório traz duas skills em `.claude/skills/`, que seguem este fluxo:

- `/pr`: cria a branch quando você está na `develop` ou na `main`, roda build e testes, faz o commit, envia e abre a PR para a `develop`.
- `/lancar-versao`: confere a versão, abre a PR da `develop` para a `main` e, depois do merge, roda o `lancar-versao.ps1`.

As duas param na aprovação, que é sempre de outra pessoa.
