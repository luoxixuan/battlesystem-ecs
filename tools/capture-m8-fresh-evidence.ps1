param([string]$EvidenceRoot = '')

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'capture-m8-fresh-evidence.ps1 must run from a Git worktree.'
}
Set-Location -LiteralPath $repoRoot
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $EvidenceRoot = "C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m8-observation-$stamp"
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($EvidenceRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'M8 evidence must be outside the repository so capture cannot change its own inventory.'
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    $existing = @(Get-ChildItem -LiteralPath $EvidenceRoot -Force -ErrorAction Stop)
    if ($existing.Count -ne 0) { throw "Evidence directory is not empty: $EvidenceRoot" }
}

# M0 recovery: all repository identity is captured before the first evidence write.
$capturedAt = [DateTimeOffset]::Now
$capturedHead = (& git rev-parse HEAD).Trim()
$capturedBranch = (& git branch --show-current).Trim()
$capturedStatus = @(& git status --short)
$capturedIndex = @(& git ls-files -s)
$capturedCached = @(& git diff --cached --name-only)
$capturedTrackedPatch = (& git diff --binary | Out-String)
$capturedUntracked = @(& git ls-files --others --exclude-standard)
$untrackedInventory = @($capturedUntracked | ForEach-Object {
    [pscustomobject]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
})

New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$attemptFailures = Join-Path $EvidenceRoot 'attempt-failures'
New-Item -ItemType Directory -Force -Path $attemptFailures | Out-Null
$indexPath = Join-Path $EvidenceRoot 'index.txt'
$patchPath = Join-Path $EvidenceRoot 'tracked.patch'
$capturedIndex | Out-File -LiteralPath $indexPath -Encoding utf8
$capturedTrackedPatch | Out-File -LiteralPath $patchPath -Encoding utf8
$indexHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
$patchHash = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash
$inventoryLines = @(
    "CAPTURED_AT_UTC=$($capturedAt.UtcDateTime.ToString('o'))",
    "CAPTURED_AT_LOCAL=$($capturedAt.ToString('o'))",
    "HEAD=$capturedHead",
    "BRANCH=$capturedBranch",
    "INDEX_SHA256=$indexHash",
    "INDEX_STATUS=$(if ($capturedCached.Count -eq 0) { 'CLEAN' } else { 'DIRTY' })",
    "TRACKED_PATCH_SHA256=$patchHash",
    'STATUS_BEGIN'
)
$inventoryLines += $capturedStatus
$inventoryLines += 'STATUS_END'
$inventoryLines += @($untrackedInventory | ForEach-Object { "$($_.path)`tSHA256=$($_.sha256)" })
$inventoryLines | Out-File -LiteralPath (Join-Path $EvidenceRoot 'dirty-inventory.txt') -Encoding utf8
[pscustomobject]@{
    schemaVersion = 1
    capturedBeforeEvidenceWrite = $true
    capturedAtUtc = $capturedAt.UtcDateTime.ToString('o')
    capturedAtLocal = $capturedAt.ToString('o')
    head = $capturedHead
    branch = $capturedBranch
    indexSha256 = $indexHash
    indexStatus = if ($capturedCached.Count -eq 0) { 'CLEAN' } else { 'DIRTY' }
    trackedPatchSha256 = $patchHash
    status = $capturedStatus
    untracked = $untrackedInventory
} | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'initial-state.json') -Encoding utf8
$recoverySnapshot = Join-Path $EvidenceRoot 'recovery-snapshot'
New-Item -ItemType Directory -Force -Path $recoverySnapshot | Out-Null
Copy-Item -LiteralPath (Join-Path $EvidenceRoot 'initial-state.json') -Destination (Join-Path $recoverySnapshot 'initial-state.json')
Copy-Item -LiteralPath (Join-Path $EvidenceRoot 'dirty-inventory.txt') -Destination (Join-Path $recoverySnapshot 'dirty-inventory.txt')
Copy-Item -LiteralPath $indexPath -Destination (Join-Path $recoverySnapshot 'tracked-index.txt')
Copy-Item -LiteralPath $patchPath -Destination (Join-Path $recoverySnapshot 'tracked.patch')

$records = [System.Collections.Generic.List[object]]::new()
function Run-Gate([string]$Name, [string]$Command, [int]$SemanticExit = 0) {
    $start = [DateTimeOffset]::Now
    $stdout = Join-Path $EvidenceRoot ($Name + '.stdout.log')
    $stderr = Join-Path $EvidenceRoot ($Name + '.stderr.log')
    & pwsh -NoProfile -Command $Command 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
    $end = [DateTimeOffset]::Now
    $semanticStatus = if ($exitCode -eq $SemanticExit) { 'PASS' } else { 'FAIL' }
    $stdoutHash = (Get-FileHash -LiteralPath $stdout -Algorithm SHA256).Hash
    $stderrHash = (Get-FileHash -LiteralPath $stderr -Algorithm SHA256).Hash
    $hashInput = "$stdoutHash`n$stderrHash`n$semanticStatus"
    $hashBytes = [Text.Encoding]::UTF8.GetBytes($hashInput)
    $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($hashBytes)).Replace('-', '')
    $records.Add([pscustomobject]@{
        name = $Name
        command = $Command
        cwd = (Get-Location).Path
        startUtc = $start.UtcDateTime.ToString('o')
        endUtc = $end.UtcDateTime.ToString('o')
        startLocal = $start.ToString('o')
        endLocal = $end.ToString('o')
        exitCode = $exitCode
        semanticStatus = $semanticStatus
        stdout = $stdout
        stderr = $stderr
        stdoutSha256 = $stdoutHash
        stderrSha256 = $stderrHash
        hash = $hash
        fresh = $true
    })
    if ($semanticStatus -eq 'FAIL') {
        Copy-Item -LiteralPath $stdout -Destination $attemptFailures -Force
        Copy-Item -LiteralPath $stderr -Destination $attemptFailures -Force
    }
}

function Add-SyntheticGate([string]$Name, [string]$Status, [string[]]$OutputLines,
    [string[]]$ErrorLines, [string]$Command) {
    $stdout = Join-Path $EvidenceRoot ($Name + '.stdout.log')
    $stderr = Join-Path $EvidenceRoot ($Name + '.stderr.log')
    @($OutputLines) | Out-File -LiteralPath $stdout -Encoding utf8
    @($ErrorLines) | Out-File -LiteralPath $stderr -Encoding utf8
    $stdoutHash = (Get-FileHash -LiteralPath $stdout -Algorithm SHA256).Hash
    $stderrHash = (Get-FileHash -LiteralPath $stderr -Algorithm SHA256).Hash
    $hashInput = "$stdoutHash`n$stderrHash`n$Status"
    $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($hashInput))).Replace('-', '')
    $records.Add([pscustomobject]@{
        name = $Name
        command = $Command
        cwd = (Get-Location).Path
        startUtc = [DateTimeOffset]::Now.UtcDateTime.ToString('o')
        endUtc = [DateTimeOffset]::Now.UtcDateTime.ToString('o')
        startLocal = [DateTimeOffset]::Now.ToString('o')
        endLocal = [DateTimeOffset]::Now.ToString('o')
        exitCode = if ($Status -eq 'PASS') { 0 } else { 1 }
        semanticStatus = $Status
        stdout = $stdout
        stderr = $stderr
        stdoutSha256 = $stdoutHash
        stderrSha256 = $stderrHash
        hash = $hash
        fresh = $true
    })
}

Run-Gate 'dotnet-info' 'dotnet --info'
Run-Gate 'engine-build' 'dotnet build BattleSystemECS.Engine --nologo'
Run-Gate 'core-build' 'dotnet build BattleSystemECS.Core --nologo'
Run-Gate 'exe-build' 'dotnet build --nologo'
Run-Gate 'tests-build' 'dotnet build BattleSystemECS.Tests --nologo'
Run-Gate 'm0-focused' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~CombatGoldenReplayTests|FullyQualifiedName~SkillBuildBoundaryTests"'
Run-Gate 'm1-focused' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~GameplayContractTests|FullyQualifiedName~CatalogCompilerTests|FullyQualifiedName~EffectRuntimeStateTests|FullyQualifiedName~GameplayCommandBufferTests|FullyQualifiedName~GameplayCapacityProbeTests"'
Run-Gate 'm3-focused' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~DamageResolverGoldenTests|FullyQualifiedName~AttributeResourceContractTests|FullyQualifiedName~FrameGraphCombatBehaviorTests"'
Run-Gate 'm4-focused' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~GameplayRuntimeTests|FullyQualifiedName~EffectRuntimeStateTests|FullyQualifiedName~GameplayCatalogProductionFlowTests"'
Run-Gate 'm8-focused' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~GameplayObservationTests|FullyQualifiedName~GameplayStorageProfileTests|FullyQualifiedName~GameplayStabilitySoakTests|FullyQualifiedName~EffectPoolTests|FullyQualifiedName~GameplayCapacityProbeTests|FullyQualifiedName~GameplayEventQueueTests|FullyQualifiedName~ResourceLifecycleAtomicityTests"'

$escapedRoot = $EvidenceRoot.Replace("'", "''")
for ($sample = 1; $sample -le 3; $sample++) {
    $storagePath = (Join-Path $EvidenceRoot "storage-profile-$sample.json").Replace("'", "''")
    Run-Gate "storage-profile-$sample" "`$env:BATTLESYSTEM_STORAGE_REPORT='$storagePath'; dotnet test BattleSystemECS.Tests --no-build --nologo --filter 'FullyQualifiedName~GameplayStorageProfileTests.DenseSoaInventoryAndActiveListProfileAreReproducible' --logger 'console;verbosity=detailed'"
    $productionPath = (Join-Path $EvidenceRoot "production-soak-$sample.json").Replace("'", "''")
    $lifecyclePath = (Join-Path $EvidenceRoot "lifecycle-soak-$sample.json").Replace("'", "''")
    Run-Gate "soak-$sample" "`$env:BATTLESYSTEM_PRODUCTION_SOAK_REPORT='$productionPath'; `$env:BATTLESYSTEM_LIFECYCLE_SOAK_REPORT='$lifecyclePath'; dotnet test BattleSystemECS.Tests --no-build --nologo --filter 'FullyQualifiedName~GameplayStabilitySoakTests|FullyQualifiedName~FrameGraphProductionFlowTests.FixedPopulationProductionScenarioKeepsTenThousandAndSuppressesWaveStart' --logger 'console;verbosity=detailed'"
}

function Read-EvidenceJson([string]$Path, [string]$ExpectedScenario, [switch]$RequireObservation,
    [switch]$RequireStorageFields) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing stability report: $Path" }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $report -or $null -eq $report.schemaVersion -or
        [int]$report.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$report.scenario) -or
        ([string]$report.scenario -cne $ExpectedScenario)) {
        throw "Malformed stability report: $Path"
    }
    if ($RequireObservation -and $null -eq $report.observation) {
        throw "Stability report has no observation: $Path"
    }
    if ($RequireObservation -and ($null -eq $report.stateDigest -or
            $null -eq $report.gameplayEventSequenceDigest -or
            $null -eq $report.gameplayEventPublishedCount -or
            $null -eq $report.observation.StateDigest -or
            $null -eq $report.observation.GameplayEventSequenceDigest -or
            $null -eq $report.observation.GameplayEventPublishedCount)) {
        throw "Stability report is missing digest fields: $Path"
    }
    if ($RequireObservation) {
        $pairs = @(
            @('stateDigest', 'StateDigest'),
            @('gameplayEventSequenceDigest', 'GameplayEventSequenceDigest'),
            @('gameplayEventPublishedCount', 'GameplayEventPublishedCount')
        )
        foreach ($pair in $pairs) {
            if ([string]$report.($pair[0]) -cne [string]$report.observation.($pair[1])) {
                throw "Top-level and observation fields disagree ($($pair[0])): $Path"
            }
        }
    }
    if ($RequireStorageFields -and ($null -eq $report.maxEntities -or $null -eq $report.population -or
            $null -eq $report.effectHandleAllocationComparison -or $null -eq $report.effectPool -or
            $null -eq $report.iteration -or $null -eq $report.categories -or $null -eq $report.arrays -or
            $null -eq $report.observation)) {
        throw "Storage report is missing required fields: $Path"
    }
    if ($RequireStorageFields) {
        $storagePairs = @(
            @('stateDigest', 'StateDigest'),
            @('gameplayEventSequenceDigest', 'GameplayEventSequenceDigest'),
            @('gameplayEventPublishedCount', 'GameplayEventPublishedCount')
        )
        foreach ($pair in $storagePairs) {
            if ($null -eq $report.($pair[0]) -or $null -eq $report.observation.($pair[1])) {
                throw "Storage report is missing observation field $($pair[0]): $Path"
            }
            if ([string]$report.($pair[0]) -cne [string]$report.observation.($pair[1])) {
                throw "Storage top-level and observation fields disagree ($($pair[0])): $Path"
            }
        }
        # Storage inventory reports must not fabricate gameplay facts. Digests
        # remain explicit contract fields; count is the semantic zero gate.
        if ([long]$report.gameplayEventPublishedCount -ne 0 -or
            [long]$report.observation.GameplayEventPublishedCount -ne 0) {
            throw "Storage profile published gameplay events: $Path"
        }
    }
    return $report
}

$storageReports = @(1..3 | ForEach-Object {
    Read-EvidenceJson (Join-Path $EvidenceRoot "storage-profile-$_.json") 'component-store-dense-soa-inventory' -RequireStorageFields
})
$storageSignatures = @($storageReports | ForEach-Object {
    [ordered]@{
        schemaVersion = $_.schemaVersion
        scenario = $_.scenario
        maxEntities = $_.maxEntities
        population = $_.population
        effectHandleAllocationComparison = $_.effectHandleAllocationComparison
        effectPool = $_.effectPool
        iteration = [ordered]@{
            repetitions = $_.iteration.repetitions
            sumsEqual = $_.iteration.sumsEqual
        }
        stateDigest = $_.stateDigest
        gameplayEventSequenceDigest = $_.gameplayEventSequenceDigest
        gameplayEventPublishedCount = $_.gameplayEventPublishedCount
        observation = [ordered]@{
            StateDigest = $_.observation.StateDigest
            GameplayEventSequenceDigest = $_.observation.GameplayEventSequenceDigest
            GameplayEventPublishedCount = $_.observation.GameplayEventPublishedCount
        }
        categories = $_.categories
        arrays = $_.arrays
    }
})
$storageBaseline = $storageSignatures[0] | ConvertTo-Json -Depth 30 -Compress
$storageStable = $true
for ($i = 1; $i -lt $storageSignatures.Count; $i++) {
    if (($storageSignatures[$i] | ConvertTo-Json -Depth 30 -Compress) -cne $storageBaseline) { $storageStable = $false }
}

$storageMissingPath = Join-Path $EvidenceRoot 'missing-storage-observation-report.json'
$missingStorage = $storageReports[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json
$missingStorage.PSObject.Properties.Remove('observation')
$missingStorage | ConvertTo-Json -Depth 50 | Out-File -LiteralPath $storageMissingPath -Encoding utf8
$storageMissingRejected = $false
try { Read-EvidenceJson $storageMissingPath 'component-store-dense-soa-inventory' -RequireStorageFields | Out-Null }
catch { $storageMissingRejected = $true }
Add-SyntheticGate 'storage-missing-field-negative' $(if ($storageMissingRejected) { 'PASS' } else { 'FAIL' }) @(
    "REPORT=$storageMissingPath", "REJECTED=$storageMissingRejected"
) @() 'storage report without nested observation must fail'

$storageMismatchPath = Join-Path $EvidenceRoot 'mismatch-storage-observation-report.json'
$mismatchStorage = $storageReports[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json
$mismatchStorage.stateDigest = [UInt64]$mismatchStorage.stateDigest + 1
$mismatchStorage | ConvertTo-Json -Depth 50 | Out-File -LiteralPath $storageMismatchPath -Encoding utf8
$storageMismatchRejected = $false
try { Read-EvidenceJson $storageMismatchPath 'component-store-dense-soa-inventory' -RequireStorageFields | Out-Null }
catch { $storageMismatchRejected = $true }
Add-SyntheticGate 'storage-mismatch-negative' $(if ($storageMismatchRejected) { 'PASS' } else { 'FAIL' }) @(
    "REPORT=$storageMismatchPath", "REJECTED=$storageMismatchRejected"
) @() 'storage top-level and nested digest mismatch must fail'

$storageTamperedPath = Join-Path $EvidenceRoot 'tampered-storage-report.json'
$tamperedStorage = $storageReports[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json
$tamperedStorage.observation.GameplayEventPublishedCount = [long]$tamperedStorage.observation.GameplayEventPublishedCount + 1
$tamperedStorage | ConvertTo-Json -Depth 50 | Out-File -LiteralPath $storageTamperedPath -Encoding utf8
$storageTamperRejected = $false
try { Read-EvidenceJson $storageTamperedPath 'component-store-dense-soa-inventory' -RequireStorageFields | Out-Null }
catch { $storageTamperRejected = $true }
Add-SyntheticGate 'storage-tampered-negative' $(if ($storageTamperRejected) { 'PASS' } else { 'FAIL' }) @(
    "REPORT=$storageTamperedPath", "REJECTED=$storageTamperRejected"
) @() 'tampered nested storage publication count must fail'

$productionReports = @(1..3 | ForEach-Object {
    Read-EvidenceJson (Join-Path $EvidenceRoot "production-soak-$_.json") 'sealed-production-fixed-population' -RequireObservation
})
$lifecycleReports = @(1..3 | ForEach-Object {
    Read-EvidenceJson (Join-Path $EvidenceRoot "lifecycle-soak-$_.json") 'periodic-death-entity-recycle' -RequireObservation
})
$tamperedPath = Join-Path $EvidenceRoot 'tampered-observation-report.json'
$tampered = $productionReports[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json
$tampered.gameplayEventPublishedCount = [long]$tampered.gameplayEventPublishedCount + 1
$tampered | ConvertTo-Json -Depth 50 | Out-File -LiteralPath $tamperedPath -Encoding utf8
$tamperRejected = $false
try { Read-EvidenceJson $tamperedPath 'sealed-production-fixed-population' -RequireObservation | Out-Null }
catch { $tamperRejected = $true }
Add-SyntheticGate 'evidence-schema-negative' $(if ($tamperRejected) { 'PASS' } else { 'FAIL' }) @(
    "TAMPERED_REPORT=$tamperedPath",
    "MISMATCH_REJECTED=$tamperRejected"
) @() 'tampered top-level publication count must disagree with nested observation and be rejected'
$productionSignatures = @($productionReports | ForEach-Object { $_.observation | ConvertTo-Json -Depth 30 -Compress })
$lifecycleSignatures = @($lifecycleReports | ForEach-Object { $_.observation | ConvertTo-Json -Depth 30 -Compress })
$productionStable = ($productionSignatures | Where-Object { $_ -cne $productionSignatures[0] }).Count -eq 0
$lifecycleStable = ($lifecycleSignatures | Where-Object { $_ -cne $lifecycleSignatures[0] }).Count -eq 0
$productionTopLevelSignatures = @($productionReports | ForEach-Object {
    [ordered]@{ stateDigest = $_.stateDigest; gameplayEventSequenceDigest = $_.gameplayEventSequenceDigest; gameplayEventPublishedCount = $_.gameplayEventPublishedCount } | ConvertTo-Json -Compress
})
$lifecycleTopLevelSignatures = @($lifecycleReports | ForEach-Object {
    [ordered]@{ stateDigest = $_.stateDigest; gameplayEventSequenceDigest = $_.gameplayEventSequenceDigest; gameplayEventPublishedCount = $_.gameplayEventPublishedCount } | ConvertTo-Json -Compress
})
$productionTopLevelStable = ($productionTopLevelSignatures | Where-Object { $_ -cne $productionTopLevelSignatures[0] }).Count -eq 0
$lifecycleTopLevelStable = ($lifecycleTopLevelSignatures | Where-Object { $_ -cne $lifecycleTopLevelSignatures[0] }).Count -eq 0
$stabilityStatus = if ($storageStable -and $productionStable -and $lifecycleStable -and $productionTopLevelStable -and $lifecycleTopLevelStable -and $tamperRejected -and $storageMissingRejected -and $storageMismatchRejected -and $storageTamperRejected) { 'PASS' } else { 'FAIL' }
[pscustomobject]@{
    schemaVersion = 1
    storageStable = $storageStable
    productionStable = $productionStable
    lifecycleStable = $lifecycleStable
    storageSignatures = $storageSignatures
    productionObservationSignatures = $productionSignatures
    lifecycleObservationSignatures = $lifecycleSignatures
} | ConvertTo-Json -Depth 30 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'stability-comparison.json') -Encoding utf8
Add-SyntheticGate 'stability-determinism' $stabilityStatus @(
    "STORAGE_STABLE=$storageStable",
    "PRODUCTION_STABLE=$productionStable",
    "LIFECYCLE_STABLE=$lifecycleStable",
    "PRODUCTION_TOP_LEVEL_STABLE=$productionTopLevelStable",
    "LIFECYCLE_TOP_LEVEL_STABLE=$lifecycleTopLevelStable",
    "TAMPER_REJECTED=$tamperRejected",
    "STORAGE_MISSING_REJECTED=$storageMissingRejected",
    "STORAGE_MISMATCH_REJECTED=$storageMismatchRejected",
    "STORAGE_TAMPER_REJECTED=$storageTamperRejected"
) @() 'compare stable storage, production soak, lifecycle soak signatures across three fresh runs'

$ledgerPath = (Join-Path $EvidenceRoot 'migration-ledger.json').Replace("'", "''")
Run-Gate 'migration-inventory' "pwsh -File tools/inventory-ecs-gas-migration.ps1 -OutputPath '$ledgerPath'"
Run-Gate 'request-contract-drift' 'git diff --exit-code 4bebc43024a74fd52462d6cb31a19ed0aa34efa3 -- Core/GAS/GameplayIdsAndHandles.cs Core/GAS/GameplayDefinitions.cs Core/GAS/GameplayAbilityRuntime.cs Core/GAS/GameplayEffect.cs'
Run-Gate 'bufftype-drift' 'git diff --exit-code 4bebc43024a74fd52462d6cb31a19ed0aa34efa3 -- Core/BuffType.cs'
Run-Gate 'legacy-inventory' 'rg -n "ApplyLegacy|LegacyApplyCount|GameplayEffectDef|AppliedEffect|HitTriggerSystem" Core Systems BattleSystemECS.Tests'
Run-Gate 'full-tests' 'dotnet test BattleSystemECS.Tests --no-build --nologo'
Run-Gate 'test-rules' 'pwsh -File tools/check-test-rules.ps1'
Run-Gate 'diff-check' 'git diff --check'

$finalHead = (& git rev-parse HEAD).Trim()
$finalBranch = (& git branch --show-current).Trim()
$finalStatus = @(& git status --short)
$finalIndex = @(& git ls-files -s)
$finalTrackedPatch = (& git diff --binary | Out-String)
$finalUntracked = @(& git ls-files --others --exclude-standard)
$finalIndexPath = Join-Path $EvidenceRoot 'final-index.txt'
$finalPatchPath = Join-Path $EvidenceRoot 'final-tracked.patch'
$finalUntrackedPath = Join-Path $EvidenceRoot 'final-untracked-inventory.txt'
$finalIndex | Out-File -LiteralPath $finalIndexPath -Encoding utf8
$finalTrackedPatch | Out-File -LiteralPath $finalPatchPath -Encoding utf8
$finalUntrackedInventory = @($finalUntracked | ForEach-Object {
    [pscustomobject]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
})
@($finalUntrackedInventory | ForEach-Object { "$($_.path)`tSHA256=$($_.sha256)" }) |
    Out-File -LiteralPath $finalUntrackedPath -Encoding utf8
$finalIndexHash = (Get-FileHash -LiteralPath $finalIndexPath -Algorithm SHA256).Hash
$finalTrackedPatchHash = (Get-FileHash -LiteralPath $finalPatchPath -Algorithm SHA256).Hash
$capturedUntrackedLines = @($untrackedInventory | ForEach-Object { "$($_.path)`tSHA256=$($_.sha256)" })
$finalUntrackedLines = @($finalUntrackedInventory | ForEach-Object { "$($_.path)`tSHA256=$($_.sha256)" })
$inventoryStable = $finalHead -eq $capturedHead -and $finalBranch -eq $capturedBranch -and
    (($finalStatus -join "`n") -ceq ($capturedStatus -join "`n")) -and
    $finalIndexHash -ceq $indexHash -and $finalTrackedPatchHash -ceq $patchHash -and
    (($finalIndex -join "`n") -ceq ($capturedIndex -join "`n")) -and
    (($finalTrackedPatch) -ceq ($capturedTrackedPatch)) -and
    (($finalUntrackedLines -join "`n") -ceq ($capturedUntrackedLines -join "`n"))
$inventoryCheckOut = Join-Path $EvidenceRoot 'inventory-stability.stdout.log'
$inventoryCheckErr = Join-Path $EvidenceRoot 'inventory-stability.stderr.log'
if ($inventoryStable) {
    'INITIAL_AND_FINAL_REPOSITORY_STATE_AND_HASHES_MATCH' | Out-File -LiteralPath $inventoryCheckOut -Encoding utf8
    '' | Out-File -LiteralPath $inventoryCheckErr -Encoding utf8
    $inventoryExit = 0
} else {
    "Initial repository identity or content hashes changed during capture.`nINITIAL HEAD=$capturedHead BRANCH=$capturedBranch INDEX_SHA256=$indexHash TRACKED_PATCH_SHA256=$patchHash`nFINAL HEAD=$finalHead BRANCH=$finalBranch INDEX_SHA256=$finalIndexHash TRACKED_PATCH_SHA256=$finalTrackedPatchHash`nINITIAL STATUS:`n$($capturedStatus -join "`n")`nFINAL STATUS:`n$($finalStatus -join "`n")`nINITIAL UNTRACKED:`n$($capturedUntrackedLines -join "`n")`nFINAL UNTRACKED:`n$($finalUntrackedLines -join "`n")" |
        Out-File -LiteralPath $inventoryCheckErr -Encoding utf8
    '' | Out-File -LiteralPath $inventoryCheckOut -Encoding utf8
    $inventoryExit = 1
}
$now = [DateTimeOffset]::Now
$inventoryStdoutHash = (Get-FileHash -LiteralPath $inventoryCheckOut -Algorithm SHA256).Hash
$inventoryStderrHash = (Get-FileHash -LiteralPath $inventoryCheckErr -Algorithm SHA256).Hash
$inventoryStatus = if ($inventoryExit -eq 0) { 'PASS' } else { 'FAIL' }
$inventoryHashInput = "$inventoryStdoutHash`n$inventoryStderrHash`n$inventoryStatus"
$inventoryHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(
    [Text.Encoding]::UTF8.GetBytes($inventoryHashInput))).Replace('-', '')
$records.Add([pscustomobject]@{
    name = 'inventory-stability'
    command = 'compare final HEAD/branch/status/index/tracked-patch/untracked-content hashes with pre-write capture'
    cwd = (Get-Location).Path
    startUtc = $now.UtcDateTime.ToString('o')
    endUtc = $now.UtcDateTime.ToString('o')
    startLocal = $now.ToString('o')
    endLocal = $now.ToString('o')
    exitCode = $inventoryExit
    semanticStatus = $inventoryStatus
    stdout = $inventoryCheckOut
    stderr = $inventoryCheckErr
    stdoutSha256 = $inventoryStdoutHash
    stderrSha256 = $inventoryStderrHash
    hash = $inventoryHash
    fresh = $true
})

[pscustomobject]@{
    schemaVersion = 1
    freeze = [ordered]@{
        initial = [ordered]@{
            head = $capturedHead
            branch = $capturedBranch
            status = $capturedStatus
            indexSha256 = $indexHash
            trackedPatchSha256 = $patchHash
            untracked = $untrackedInventory
        }
        final = [ordered]@{
            head = $finalHead
            branch = $finalBranch
            status = $finalStatus
            indexSha256 = $finalIndexHash
            trackedPatchSha256 = $finalTrackedPatchHash
            untracked = $finalUntrackedInventory
        }
        consistent = $inventoryStable
        captureComplete = $true
    }
} | ConvertTo-Json -Depth 10 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'repository-state-manifest.json') -Encoding utf8

$deferred = @(
    [pscustomobject]@{ name = 'mode-2'; status = 'DEFERRED'; reason = 'User deferred mode 2/4/5 until the architecture migration is complete.' },
    [pscustomobject]@{ name = 'mode-4'; status = 'DEFERRED'; reason = 'User deferred mode 2/4/5 until the architecture migration is complete.' },
    [pscustomobject]@{ name = 'mode-5'; status = 'DEFERRED'; reason = 'User deferred mode 2/4/5 until the architecture migration is complete.' },
    [pscustomobject]@{ name = 'unity-battle-driver-smoke'; status = 'UNAVAILABLE/BLOCKED'; reason = 'Unity smoke was not run; dotnet evidence is not a Unity substitute.' }
)
$deferred | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'deferred-and-blocked.json') -Encoding utf8

foreach ($record in $records) {
    $checkInput = "$($record.stdoutSha256)`n$($record.stderrSha256)`n$($record.semanticStatus)"
    $checkHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($checkInput))).Replace('-', '')
    if ($checkHash -ne $record.hash) { throw "Evidence record '$($record.name)' has an invalid hash." }
}
$records | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'command-manifest.json') -Encoding utf8
$failed = @($records | Where-Object semanticStatus -eq 'FAIL')
[pscustomobject]@{
    schemaVersion = 1
    evidenceRoot = $EvidenceRoot
    commandCount = $records.Count
    passCount = @($records | Where-Object semanticStatus -eq 'PASS').Count
    failCount = $failed.Count
    deferred = $deferred
} | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $EvidenceRoot 'capture-summary.json') -Encoding utf8

$hashFile = Join-Path $EvidenceRoot 'evidence-sha256'
Get-ChildItem -LiteralPath $EvidenceRoot -File -Recurse |
    Where-Object { $_.FullName -ne $hashFile } |
    Get-FileHash -Algorithm SHA256 |
    Sort-Object Path |
    ForEach-Object { $_.Hash + '  ' + $_.Path } |
    Out-File -LiteralPath $hashFile -Encoding ascii

Write-Output "M8_EVIDENCE_ROOT=$EvidenceRoot"
if ($failed.Count -ne 0) {
    $failed | ForEach-Object { Write-Error "FAILED: $($_.name) (exit $($_.exitCode))" }
    exit 1
}
