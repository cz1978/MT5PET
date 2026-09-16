param(
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-bridge.ps1')

$bridgeArtifact = Join-Path $repoRoot 'artifacts\bridge\TradePetBridge.ex5'
$appBridge = Join-Path $repoRoot 'src\TradePet.App\Runtime\TradePetBridge.ex5'
Copy-Item -LiteralPath $bridgeArtifact -Destination $appBridge -Force

$publishDirectory = Join-Path $repoRoot "artifacts\TradePet-$RuntimeIdentifier"
dotnet publish (Join-Path $repoRoot 'src\TradePet.App\TradePet.App.csproj') `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --property:PublishSingleFile=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'TradePet publish failed.' }

Write-Output $publishDirectory
