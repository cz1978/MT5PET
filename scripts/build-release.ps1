param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Mt5MetaEditorPath,
    [string]$Mt4MetaEditorPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mt5Options = @{}
if ($Mt5MetaEditorPath) { $mt5Options.MetaEditorPath = $Mt5MetaEditorPath }
& (Join-Path $PSScriptRoot 'build-bridge.ps1') @mt5Options
& (Join-Path $PSScriptRoot 'build-mt4-bridge.ps1') -MetaEditorPath $Mt4MetaEditorPath

$bridgeArtifact = Join-Path $repoRoot 'artifacts\bridge\TradePetBridge.ex5'
$appBridge = Join-Path $repoRoot 'src\TradePet.App\Runtime\TradePetBridge.ex5'
Copy-Item -LiteralPath $bridgeArtifact -Destination $appBridge -Force

$publishDirectory = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) }
    else { Join-Path $repoRoot "artifacts\TradePet-$RuntimeIdentifier" }
dotnet publish (Join-Path $repoRoot 'src\TradePet.App\TradePet.App.csproj') `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --property:PublishSingleFile=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'TradePet publish failed.' }

Write-Output $publishDirectory
