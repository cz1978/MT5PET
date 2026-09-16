param(
    [string]$MetaEditorPath = 'C:\Program Files\WeTrade MetaTrader 5 Terminal\MetaEditor64.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repoRoot 'mt5\TradePetBridge.mq5'
$artifactDirectory = Join-Path $repoRoot 'artifacts\bridge'
$logPath = Join-Path $artifactDirectory 'compile.log'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $MetaEditorPath)) {
    throw "MetaEditor not found: $MetaEditorPath"
}

$process = Start-Process -FilePath $MetaEditorPath -ArgumentList @(
    "/compile:$sourcePath",
    "/log:$logPath"
) -Wait -PassThru -WindowStyle Hidden

$compiledPath = [System.IO.Path]::ChangeExtension($sourcePath, '.ex5')
if (-not (Test-Path -LiteralPath $compiledPath)) {
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath
    }
    throw "Bridge compilation did not produce $compiledPath"
}

Copy-Item -LiteralPath $compiledPath -Destination (Join-Path $artifactDirectory 'TradePetBridge.ex5') -Force
Write-Output (Join-Path $artifactDirectory 'TradePetBridge.ex5')
