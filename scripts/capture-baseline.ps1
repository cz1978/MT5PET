param(
    [string]$PortableArtifact,
    [string]$DataBackupManifest,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PortableArtifact)) {
    $PortableArtifact = Join-Path $repoRoot 'artifacts\TradePet-win-x64'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'release\baseline'
}

function Get-RelativePath([string]$BasePath, [string]$Path) {
    [System.IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
}

function Get-CanonicalTree([System.IO.FileInfo[]]$Files, [string]$BasePath) {
    $records = foreach ($file in $Files | Sort-Object FullName) {
        [pscustomobject]@{
            Path = Get-RelativePath $BasePath $file.FullName
            Bytes = $file.Length
            Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }
    $lines = @($records | ForEach-Object { "$($_.Sha256)  $($_.Path)" })
    $text = if ($lines.Count -eq 0) { '' } else { ($lines -join "`n") + "`n" }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $digest = [System.Security.Cryptography.SHA256]::HashData($bytes)
    [pscustomobject]@{
        Records = @($records)
        Lines = $lines
        Sha256 = [Convert]::ToHexString($digest).ToLowerInvariant()
        Bytes = ($records | Measure-Object -Property Bytes -Sum).Sum
    }
}

function Read-RegexValue([string]$Path, [string]$Pattern, [string]$Label) {
    $match = [regex]::Match((Get-Content -Raw -LiteralPath $Path), $Pattern)
    if (-not $match.Success) {
        throw "Unable to read $Label from $(Get-RelativePath $repoRoot $Path)."
    }
    $match.Groups[1].Value
}

$versionPath = Join-Path $repoRoot 'VERSION.props'
[xml]$versionXml = Get-Content -Raw -LiteralPath $versionPath
$version = $versionXml.Project.PropertyGroup
$productVersion = if ([string]::IsNullOrWhiteSpace($version.VersionSuffix)) {
    [string]$version.VersionPrefix
} else {
    "$($version.VersionPrefix)-$($version.VersionSuffix)"
}

$schemaPath = Join-Path $repoRoot 'src\TradePet.Infrastructure\Persistence\SchemaMigrations.cs'
$schemaMatches = [regex]::Matches(
    (Get-Content -Raw -LiteralPath $schemaPath),
    'new SchemaMigration\((\d+),'
)
$schemaVersion = ($schemaMatches | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Maximum).Maximum
$protocolVersion = Read-RegexValue (Join-Path $repoRoot 'src\TradePet.Core\Protocol\ProtocolEnvelope.cs') 'CurrentVersion\s*=\s*"([^"]+)"' 'C# protocol version'
$workerProtocolVersion = Read-RegexValue (Join-Path $repoRoot 'python\tradepet_mt5_worker.py') 'PROTOCOL_VERSION\s*=\s*"([^"]+)"' 'worker protocol version'
$historyProtocolVersion = Read-RegexValue (Join-Path $repoRoot 'python\tradepet_mt5_history_worker.py') 'PROTOCOL_VERSION\s*=\s*"([^"]+)"' 'history-worker protocol version'
$workerVersion = Read-RegexValue (Join-Path $repoRoot 'python\tradepet_mt5_worker.py') 'WORKER_VERSION\s*=\s*"([^"]+)"' 'worker version'
$historyWorkerVersion = Read-RegexValue (Join-Path $repoRoot 'python\tradepet_mt5_history_worker.py') 'HISTORY_WORKER_VERSION\s*=\s*"([^"]+)"' 'history-worker version'
$exportVersion = [int](Read-RegexValue (Join-Path $repoRoot 'src\TradePet.Application\Review\ReviewServices.cs') 'formatVersion\s*=\s*(\d+)' 'review export format version')
$backupVersion = [int](Read-RegexValue (Join-Path $repoRoot 'src\TradePet.Infrastructure\Persistence\ReviewArtifactStore.cs') 'CurrentFormatVersion\s*=\s*(\d+)' 'backup format version')

$drift = @(
    if ([int]$version.TradePetSchemaVersion -ne $schemaVersion) { 'schema' }
    if ([string]$version.TradePetProtocolVersion -ne $protocolVersion) { 'C# protocol' }
    if ([string]$version.TradePetProtocolVersion -ne $workerProtocolVersion) { 'worker protocol' }
    if ([string]$version.TradePetProtocolVersion -ne $historyProtocolVersion) { 'history-worker protocol' }
    if ([string]$version.TradePetWorkerVersion -ne $workerVersion) { 'worker version' }
    if ([string]$version.TradePetHistoryWorkerVersion -ne $historyWorkerVersion) { 'history-worker version' }
    if ([int]$version.TradePetReviewExportFormatVersion -ne $exportVersion) { 'review export format' }
    if ([int]$version.TradePetBackupFormatVersion -ne $backupVersion) { 'backup format' }
)
if ($drift.Count -gt 0) {
    throw "VERSION.props drift detected: $($drift -join ', ')."
}

$sourcePaths = @(& git -C $repoRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate Git source files.'
}
$sourcePaths = @($sourcePaths | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and
    $_.Replace('\', '/') -notlike 'release/baseline/*'
} | Sort-Object -Unique)
$sourceFiles = @($sourcePaths | ForEach-Object { Get-Item -LiteralPath (Join-Path $repoRoot $_) })

$forbiddenExtensions = @('.db', '.sqlite', '.sqlite3', '.log', '.dmp', '.pfx', '.p12', '.pem', '.key', '.snk', '.ex5')
$forbiddenFiles = @($sourceFiles | Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() })
if ($forbiddenFiles.Count -gt 0) {
    throw "Forbidden files remain in the Git source set: $((@($forbiddenFiles | ForEach-Object { Get-RelativePath $repoRoot $_.FullName })) -join ', ')."
}

$textExtensions = @('.cs', '.csproj', '.json', '.manifest', '.md', '.mq5', '.props', '.ps1', '.py', '.sln', '.txt', '.xaml')
$userPathPattern = '[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s]+'
$repoPathPattern = [regex]::Escape($repoRoot).Replace('\\', '[\\/]')
$privateKeyPattern = '-----BEGIN ' + '[A-Z ]*PRIVATE KEY-----'
$assignedSecretPattern = '(?i)(password|passwd|api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token)\s*[:=]\s*["''][^"'']{8,}["'']'
$unsafeText = foreach ($file in $sourceFiles | Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() }) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -match $userPathPattern -or $content -match $repoPathPattern -or $content -match $privateKeyPattern -or $content -match $assignedSecretPattern) {
        Get-RelativePath $repoRoot $file.FullName
    }
}
if (@($unsafeText).Count -gt 0) {
    throw "Machine path or likely secret found in: $((@($unsafeText | Sort-Object -Unique)) -join ', ')."
}

$codeExtensions = @('.cs', '.py', '.mq5')
$forbiddenTradingTokens = @('order' + '_send', 'Order' + 'Send', 'TRADE_ACTION_' + 'DEAL', 'TRADE_ACTION_' + 'PENDING')
$tradingWriteHits = foreach ($file in $sourceFiles | Where-Object { $codeExtensions -contains $_.Extension.ToLowerInvariant() }) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($token in $forbiddenTradingTokens) {
        if ($content.Contains($token, [System.StringComparison]::OrdinalIgnoreCase)) {
            "$(Get-RelativePath $repoRoot $file.FullName):$token"
        }
    }
}
if (@($tradingWriteHits).Count -gt 0) {
    throw "Trading write capability token found: $((@($tradingWriteHits)) -join ', ')."
}

$sourceTree = Get-CanonicalTree $sourceFiles $repoRoot
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sourceListPath = Join-Path $OutputDirectory 'source-files.sha256'
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
$sourceListText = if ($sourceTree.Lines.Count -eq 0) { '' } else { ($sourceTree.Lines -join "`n") + "`n" }
[System.IO.File]::WriteAllText($sourceListPath, $sourceListText, $utf8WithoutBom)

$dependencies = foreach ($project in Get-ChildItem -Recurse -File -Filter '*.csproj' -LiteralPath $repoRoot | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|\.venv)[\\/]' }) {
    [xml]$projectXml = Get-Content -Raw -LiteralPath $project.FullName
    foreach ($reference in @($projectXml.Project.ItemGroup.PackageReference)) {
        if ($null -ne $reference -and -not [string]::IsNullOrWhiteSpace($reference.Include)) {
            [pscustomobject]@{
                Ecosystem = 'NuGet'
                Name = [string]$reference.Include
                Version = [string]$reference.Version
                DeclaredIn = Get-RelativePath $repoRoot $project.FullName
            }
        }
    }
}
$requirementsPath = Join-Path $repoRoot 'python\requirements.txt'
foreach ($line in Get-Content -LiteralPath $requirementsPath) {
    $trimmed = $line.Trim()
    if ($trimmed -and -not $trimmed.StartsWith('#')) {
        $parts = $trimmed -split '==', 2
        $dependencies += [pscustomobject]@{
            Ecosystem = 'Python'
            Name = $parts[0]
            Version = if ($parts.Count -eq 2) { $parts[1] } else { 'unpinned' }
            DeclaredIn = 'python/requirements.txt'
        }
    }
}

$portable = [ordered]@{ Available = $false }
if (Test-Path -LiteralPath $PortableArtifact -PathType Container) {
    $portableRoot = (Resolve-Path -LiteralPath $PortableArtifact).Path
    $portableFiles = @(Get-ChildItem -Recurse -File -LiteralPath $portableRoot)
    $portableTree = Get-CanonicalTree $portableFiles $portableRoot
    $executable = Join-Path $portableRoot 'TradePet.exe'
    $portable = [ordered]@{
        Available = $true
        Kind = 'directory-tree'
        FileCount = $portableTree.Records.Count
        Bytes = $portableTree.Bytes
        TreeSha256 = $portableTree.Sha256
        ExecutableSha256 = if (Test-Path -LiteralPath $executable) { (Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash.ToLowerInvariant() } else { $null }
    }
}

$privateBackup = [ordered]@{ Available = $false }
if (-not [string]::IsNullOrWhiteSpace($DataBackupManifest)) {
    $resolvedBackupManifest = (Resolve-Path -LiteralPath $DataBackupManifest).Path
    $backup = Get-Content -Raw -LiteralPath $resolvedBackupManifest | ConvertFrom-Json
    $database = @($backup.files | Where-Object { $_.path -eq 'tradepet.db' })
    if ($backup.backupQuickCheck -ne 'ok' -or $database.Count -ne 1) {
        throw 'The private data backup manifest is not a verified TradePet baseline.'
    }
    $privateBackup = [ordered]@{
        Available = $true
        BackupId = $backup.backupId
        SchemaVersion = [int]$backup.schemaVersion
        QuickCheck = $backup.backupQuickCheck
        ForeignKeyViolations = [int]$backup.foreignKeyViolations
        DatabaseSha256 = $database[0].sha256
        ManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedBackupManifest).Hash.ToLowerInvariant()
        Location = 'private-local-backup-outside-repository'
    }
}

$head = (& git -C $repoRoot rev-parse --verify HEAD 2>$null)
$hasHead = $LASTEXITCODE -eq 0
$authorName = (& git -C $repoRoot config --get user.name)
$hasAuthorName = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($authorName)
$authorEmail = (& git -C $repoRoot config --get user.email)
$hasAuthorEmail = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($authorEmail)
$branch = (& git -C $repoRoot branch --show-current).Trim()

$manifest = [ordered]@{
    FormatVersion = 1
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Product = [ordered]@{
        Name = 'TradePet'
        Version = $productVersion
        ReleaseChannel = [string]$version.TradePetReleaseChannel
    }
    Contracts = [ordered]@{
        SchemaVersion = $schemaVersion
        ProtocolVersion = $protocolVersion
        WorkerVersion = $workerVersion
        HistoryWorkerVersion = $historyWorkerVersion
        ReviewExportFormatVersion = $exportVersion
        BackupFormatVersion = $backupVersion
    }
    Repository = [ordered]@{
        Branch = $branch
        Head = if ($hasHead) { $head.Trim() } else { $null }
        State = if ($hasHead) { 'tracked' } else { 'unborn'
        }
        HasConfiguredAuthorIdentity = $hasAuthorName -and $hasAuthorEmail
        SourceFileCount = $sourceTree.Records.Count
        SourceTreeSha256 = $sourceTree.Sha256
        SourceFileList = 'source-files.sha256'
    }
    Toolchain = [ordered]@{
        DotNetSdk = (& dotnet --version).Trim()
        PowerShell = $PSVersionTable.PSVersion.ToString()
        OsBuild = [Environment]::OSVersion.Version.ToString()
    }
    Dependencies = @($dependencies | Sort-Object Ecosystem, Name, Version, DeclaredIn)
    ExistingPortableArtifact = $portable
    PrivateDataBackup = $privateBackup
    Samples = @(
        [ordered]@{ Id = 'current-private-workspace'; Kind = 'private'; SchemaVersion = $privateBackup.SchemaVersion; Availability = if ($privateBackup.Available) { 'verified-external' } else { 'missing' } },
        [ordered]@{ Id = 'synthetic-automated-tests'; Kind = 'synthetic'; SchemaVersion = $schemaVersion; Availability = 'in-repository' },
        [ordered]@{ Id = 'legacy-schema-1-through-8'; Kind = 'private-redacted-fixtures'; SchemaVersion = '1-8'; Availability = 'not-yet-curated'; PlannedWorkPackage = 'WP08' }
    )
    DefectLedger = @('F01','F02','F03','F04','F05','F06','F07','F08','F09','F10')
    Safety = [ordered]@{
        ForbiddenSensitiveFileTypesPresent = $false
        LikelySecretsOrUserPathsPresent = $false
        TradingWriteCapabilityTokensPresent = $false
    }
    ExternalConditions = [ordered]@{
        E01PublisherAndAssetRights = 'unverified'
        E02ProductionSigning = 'unavailable'
        E03DistributionEndpoint = 'unavailable'
        E04NativeAcceptanceEnvironment = 'unavailable'
        E05RepositoryAndBuildIdentity = if ($hasAuthorName -and $hasAuthorEmail) { 'author-configured' } else { 'git-author-missing' }
    }
}

$manifestPath = Join-Path $OutputDirectory 'baseline-manifest.json'
$manifestJson = ($manifest | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8WithoutBom)
Write-Output $manifestPath
