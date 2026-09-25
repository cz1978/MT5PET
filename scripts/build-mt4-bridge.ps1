param([string]$MetaEditorPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $MetaEditorPath) {
    $MetaEditorPath = Get-ChildItem -LiteralPath ${env:ProgramFiles(x86)}, $env:ProgramFiles -Filter 'metaeditor.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName 'terminal.exe') } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MetaEditorPath -or -not (Test-Path -LiteralPath $MetaEditorPath)) { throw 'MT4 MetaEditor not found. Pass -MetaEditorPath.' }
$sourcePath = Join-Path $repoRoot 'mt4\TradePetBridge.mq4'
$compiledPath = Join-Path $repoRoot 'mt4\TradePetBridge.ex4'
$logDirectory = Join-Path $repoRoot 'artifacts\bridge-mt4'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ('compile-' + [Guid]::NewGuid().ToString('N') + '.log')
$started = [DateTime]::UtcNow
Start-Process -FilePath $MetaEditorPath -ArgumentList @("/compile:`"$sourcePath`"", "/log:`"$logPath`"") -Wait -WindowStyle Hidden
if (-not (Test-Path -LiteralPath $logPath)) { throw 'MT4 compiler did not produce a log.' }
$log = Get-Content -LiteralPath $logPath -Raw
if ($log -notmatch '0 errors, 0 warnings' -or -not (Test-Path -LiteralPath $compiledPath) -or
    (Get-Item -LiteralPath $compiledPath).LastWriteTimeUtc -lt $started.AddSeconds(-2)) {
    Write-Output $log
    throw 'MT4 bridge compilation failed.'
}
Copy-Item -LiteralPath $compiledPath -Destination (Join-Path $logDirectory 'TradePetBridge.ex4') -Force
Write-Output $compiledPath
