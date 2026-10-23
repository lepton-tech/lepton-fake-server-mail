#Requires -Version 7.0

param(
    [Parameter(Mandatory)]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Versao
)

$ErrorActionPreference = "Stop"
$repo = "lepton-tech/lepton-fake-server-mail"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FakeServerEmail\FakeServerEmail.csproj"
$changelog = Join-Path $root "CHANGELOG.md"
$branch = "release/$Versao"

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles "GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI não encontrado. Instale com: winget install --id GitHub.cli" }

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "Faça login no GitHub CLI antes: gh auth login" }

$consoleEncoding = [Console]::OutputEncoding
Push-Location $root
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    if (git status --porcelain) { throw "Há alterações não salvas. Faça commit ou descarte antes de preparar a versão." }

    git fetch origin develop --quiet
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível buscar a develop no GitHub." }

    $current = ([xml]((git show origin/develop:src/FakeServerEmail/FakeServerEmail.csproj) -join "`n")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ([version]$Versao -le [version]$current) { throw "A versão $Versao precisa ser maior que a da develop ($current)." }

    $text = (git show origin/develop:CHANGELOG.md) -join "`n"
    $unreleased = [regex]::Match($text, "(?ms)^## Não lançado\n(.*?)(?=^## |\z)")
    if (-not $unreleased.Success) { throw "O CHANGELOG.md da develop não tem a seção '## Não lançado'." }
    $changes = $unreleased.Groups[1].Value.Trim()
    if (-not $changes) { throw "A seção 'Não lançado' do CHANGELOG.md da develop está vazia. Registre o que mudou, por PR, antes de preparar a versão." }

    git switch --quiet --no-track -c $branch origin/develop
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível criar a branch $branch." }

    $section = "## Não lançado`n`n## $Versao - $(Get-Date -Format yyyy-MM-dd)`n`n$changes`n`n"
    $text = $text.Remove($unreleased.Index, $unreleased.Length).Insert($unreleased.Index, $section).TrimEnd() + "`n"
    Set-Content -NoNewline -Encoding utf8NoBOM $changelog $text

    $csproj = Get-Content -Raw $project
    Set-Content -NoNewline -Encoding utf8NoBOM $project $csproj.Replace("<Version>$current</Version>", "<Version>$Versao</Version>")

    git commit --quiet -am "Versão $Versao"
    if ($LASTEXITCODE -ne 0) { throw "Falha ao fazer o commit da versão." }
    git push --quiet -u origin $branch
    if ($LASTEXITCODE -ne 0) { throw "Falha ao enviar a branch $branch." }

    $body = "$changes`n`nDepois do merge, leve a ``develop`` para a ``main`` e rode ``.\scripts\lancar-versao.ps1``."
    & $gh pr create --repo $repo --base develop --head $branch --title "Versão $Versao" --body $body
    if ($LASTEXITCODE -ne 0) { throw "A branch $branch foi enviada, mas a PR não foi aberta. Abra com: gh pr create --base develop --head $branch" }
}
finally {
    [Console]::OutputEncoding = $consoleEncoding
    Pop-Location
}
