param(
    [string]$PythonPath
)

$ErrorActionPreference = 'Stop'
$requirementsPath = Join-Path $PSScriptRoot 'python\requirements.txt'
if (-not (Test-Path -LiteralPath $requirementsPath)) {
    $requirementsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'python\requirements.txt'
}
if (-not $PythonPath) {
    $PythonPath = @(
        (Join-Path $env:LOCALAPPDATA 'TradePet\python\venv\Scripts\python.exe'),
        'C:\Program Files\Python313\python.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $PythonPath -or -not (Test-Path -LiteralPath $PythonPath)) {
    throw "Python 3.13 not found: $PythonPath"
}
if (-not (Test-Path -LiteralPath $requirementsPath)) {
    throw "TradePet requirements not found: $requirementsPath"
}

$environmentDirectory = Join-Path $env:LOCALAPPDATA 'TradePet\python\venv'
$environmentPython = Join-Path $environmentDirectory 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $environmentPython)) {
    & $PythonPath -m venv $environmentDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create the TradePet Python environment.' }
}
& $environmentPython -m pip install --disable-pip-version-check -r $requirementsPath
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the TradePet Python requirements.' }

Write-Output "TradePet Python environment is ready: $environmentPython"
