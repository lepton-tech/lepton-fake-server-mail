param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string[]]$Runtime = @("win-x64")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\FakeServerEmail\FakeServerEmail.csproj"
$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($rid in $Runtime) {
    $staging = Join-Path $PSScriptRoot "artifacts\publish\$rid"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    dotnet publish $project -c Release -r $rid --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $rid" }

    $name = if ($rid -eq "win-x64") { "FakeServerEmail.exe" } else { "FakeServerEmail-$($rid.Substring(4)).exe" }
    Copy-Item (Join-Path $staging "FakeServerEmail.exe") (Join-Path $dist $name) -Force
    $size = [math]::Round((Get-Item (Join-Path $dist $name)).Length / 1MB, 1)
    Write-Host "Gerado: dist\$name ($size MB)"
}
