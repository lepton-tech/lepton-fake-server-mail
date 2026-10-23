#Requires -Version 7.0

$ErrorActionPreference = "Stop"
$repo = "lepton-tech/lepton-fake-server-mail"
$project = Join-Path $PSScriptRoot "src\FakeServerEmail\FakeServerEmail.csproj"
$dist = Join-Path $PSScriptRoot "dist"
$assets = "FakeServerEmail.exe", "FakeServerEmail-x86.exe", "FakeServerEmail-arm64.exe" | ForEach-Object { Join-Path $dist $_ }

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles "GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI não encontrado. Instale com: winget install --id GitHub.cli" }

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "Faça login no GitHub CLI antes: gh auth login" }

$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

Push-Location $PSScriptRoot
try {
    if (git status --porcelain) { throw "Há alterações não salvas. Faça commit ou descarte antes de lançar a versão." }

    git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível buscar a main no GitHub." }
    $commit = git rev-parse HEAD
    if ($commit -ne (git rev-parse origin/main)) { throw "O código atual não é o da main do GitHub. Rode: git switch main; git pull" }

    if (git ls-remote --tags origin "refs/tags/$tag") { throw "A tag $tag já existe. Aumente a <Version> do FakeServerEmail.csproj, por uma PR, antes de lançar." }
    & $gh release view $tag --repo $repo *> $null
    if ($LASTEXITCODE -eq 0) { throw "Já existe uma release $tag, talvez um rascunho de uma tentativa anterior. Apague com: gh release delete $tag --repo $repo --yes" }

    dotnet test
    if ($LASTEXITCODE -ne 0) { throw "Os testes falharam. Nada foi publicado." }

    & (Join-Path $PSScriptRoot "publicar.ps1") -Runtime win-x64, win-x86, win-arm64

    $notes = Join-Path $PSScriptRoot "artifacts\notas-da-versao.md"
    Set-Content -Encoding utf8NoBOM $notes @'
Baixe o arquivo do seu Windows e dê dois cliques. Não precisa instalar nada.

| Arquivo | Para |
|---|---|
| `FakeServerEmail.exe` | Windows 10 e 11 de 64 bits (a maioria dos computadores) |
| `FakeServerEmail-x86.exe` | Windows de 32 bits |
| `FakeServerEmail-arm64.exe` | Windows em processador ARM (Snapdragon, Surface Pro X) |
'@

    & $gh release create $tag @assets --repo $repo --target $commit --title "Fake Server Email $version" --notes-file $notes --generate-notes --draft
    if ($LASTEXITCODE -ne 0) { throw "Falha ao criar a release. Se ficou um rascunho, apague com: gh release delete $tag --repo $repo --yes" }

    & $gh release edit $tag --repo $repo --draft=false
    if ($LASTEXITCODE -ne 0) { throw "Os executáveis foram enviados, mas a release ficou como rascunho. Publique em: https://github.com/$repo/releases" }

    git fetch origin --tags --quiet
    Write-Host "Versão $version publicada: https://github.com/$repo/releases/tag/$tag"
}
finally {
    Pop-Location
}
