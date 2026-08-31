<#
Read-only candidate inventory for the ECS/GAS migration.

This is deliberately a conservative text scanner, not a C# parser. It keeps
raw occurrences and uncertain classifications so that M0 can review them
instead of presenting a misleadingly precise number. Line comments are
ignored; block-comment/preprocessor edge cases may still be reported and
must be reviewed manually.

Usage:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1 -OutputPath artifacts/gas-migration-ledger.json

PowerShell 7 users may replace powershell.exe with pwsh.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$scanDirectories = @('Core', 'Systems')
$productionFiles = @()

function Get-RelativePath {
    param([string]$FullPath)
    $rootPrefix = $repoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($FullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($rootPrefix.Length)
    }
    return $FullPath
}

function Remove-LineComment {
    param([string]$Text)

    # Remove // only outside quoted strings. This avoids turning a URL/string
    # into a false candidate while keeping the scanner compatible with
    # Windows PowerShell 5.1.
    $chars = $Text.ToCharArray()
    $inString = $false
    $inVerbatim = $false
    $escaped = $false
    for ($i = 0; $i -lt $chars.Length; $i++) {
        $c = $chars[$i]
        if ($inString) {
            if ($inVerbatim) {
                if ($c -eq '"') {
                    if (($i + 1) -lt $chars.Length -and $chars[$i + 1] -eq '"') {
                        $i++
                    }
                    else {
                        $inString = $false
                        $inVerbatim = $false
                    }
                }
            }
            else {
                if ($escaped) {
                    $escaped = $false
                }
                elseif ($c -eq '\') {
                    $escaped = $true
                }
                elseif ($c -eq '"') {
                    $inString = $false
                }
            }
            continue
        }

        if ($c -eq '"') {
            if ($i -gt 0 -and $chars[$i - 1] -eq '@') {
                $inVerbatim = $true
            }
            $inString = $true
            continue
        }

        if ($c -eq '/' -and ($i + 1) -lt $chars.Length -and $chars[$i + 1] -eq '/') {
            return $Text.Substring(0, $i)
        }
    }
    return $Text
}

function Get-MethodName {
    param([string]$Code)

    # This intentionally ignores local functions and expression-bodied edge
    # cases. The method name is context only; line/file remain authoritative.
    $match = [regex]::Match(
        $Code,
        '\b(?:public|private|protected|internal)\s+(?:(?:static|readonly|virtual|override|async|sealed|new)\s+)*(?:[A-Za-z_][A-Za-z0-9_<>,.?\[\]]*\s+)+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\('
    )
    if ($match.Success) {
        $name = $match.Groups['name'].Value
        if ($name -notmatch '^(if|for|foreach|while|switch|catch|using|lock)$') {
            return $name
        }
    }
    return $null
}

function Get-WriteClassification {
    param(
        [string]$File,
        [string]$Method,
        [string]$Code
    )

    $context = ($File + ' ' + $Method + ' ' + $Code)
    if ($context -match '(?i)(AddEnemy|AddTower|CreateEntity|DestroyEntity|Spawn|Summon|Initialize|Initiali[sz]|Reset|SetEnemyHealth|SetMaxHealth|Morph|Burrow|Clone|Fission|Load|Checkpoint|Benchmark|Migration)') {
        return 'Init'
    }
    # Use word boundaries: "EnemyHealth" must not be mistaken for "Heal".
    if ($context -match '(?i)(\bHeal(?:ing)?\b|\bShield\b|\bMana\b|\bGold\b|\bResource\b|\bRegen\b|\bRestore\b|\bEmergencyHeal\b|CurrentHealth\s*\+|MaxHealth\s*\+)') {
        return 'Resource'
    }
    if ($Code -match '(?i)(-\s*=|\bDamage\b|damage|DoT|Bleed|Frost|Thorn|Projectile|Attack|Meteor|Reaction|Explosion|Culling|DeathMark|Wound|Reflect|Splash|Chain|Bounce)') {
        return 'DamageCandidate'
    }
    return 'Unknown'
}

foreach ($directory in $scanDirectories) {
    $root = Join-Path $repoRoot $directory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        continue
    }
    $productionFiles += @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
}

$enemyHealthAccesses = @()
$directWrites = @()
$applyCalls = @()
$queueDeclarations = @()
$towerAttackQueueDeclarations = @()
$damageLoops = @()
$abilityEntrypoints = @()
$effectTimerOwners = @()
$registryProperties = @()
$groupAssignments = @()
$nullableGroupSlots = @()
$registryInjectors = @()
$gameplayEffectUsages = @()
$newTypeCounts = @{}

$knownDefinitions = @(
    'MarkSystem',
    'DeathMarkSystem',
    'HitTriggerSystem'
)

foreach ($file in $productionFiles) {
    $relative = Get-RelativePath -FullPath $file.FullName
    $relativeNormalized = $relative.Replace('\', '/')
    $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
    $currentMethod = '<file-scope>'
    $seenDamageLoopKeys = @{}
    $seenTimerKeys = @{}

    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $rawLine = [string]$lines[$lineIndex]

        # Preserve the broad grep-equivalent metric separately. It deliberately
        # includes reads and comments so a review cannot relabel it as a writer
        # count. Executable write candidates are collected from comment-stripped
        # code below.
        $rawEnemyHealthMatches = [regex]::Matches(
            $rawLine,
            '\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?EnemyHealth\s*\[[^\]]+\]'
        )
        if ($rawEnemyHealthMatches.Count -gt 0) {
            $enemyHealthAccesses += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                occurrenceCount = $rawEnemyHealthMatches.Count
                isCommentOnly = $rawLine.TrimStart().StartsWith('//')
                text = $rawLine.Trim()
            }
        }

        $code = Remove-LineComment -Text $rawLine
        $trimmed = $code.Trim()
        if ($trimmed.Length -eq 0) {
            continue
        }

        $methodCandidate = Get-MethodName -Code $code
        if ($null -ne $methodCandidate) {
            $currentMethod = $methodCandidate
        }

        # Count constructors for a small set of definitions whose production
        # wiring is called out in the migration plan.
        foreach ($typeName in $knownDefinitions) {
            if ($code -match ('\bnew\s+' + [regex]::Escape($typeName) + '\s*\(')) {
                if (-not $newTypeCounts.ContainsKey($typeName)) {
                    $newTypeCounts[$typeName] = 0
                }
                $newTypeCounts[$typeName]++
            }
        }

        if ($code -match '\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?EnemyHealth\s*\[[^\]]+\]\s*(?<op>[-+*/]?=)') {
            $operator = $Matches['op']
            $directWrites += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                method = $currentMethod
                operator = $operator
                classification = Get-WriteClassification -File $relative -Method $currentMethod -Code $code
                text = $trimmed
            }
        }

        if ($code -match '\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?ApplyEnemyDamage\s*\(') {
            $isDeclaration = $code -match '\b(?:public|private|protected|internal)\b[^;]*\bApplyEnemyDamage\s*\('
            $isOverloadForward = (
                $relativeNormalized -eq 'Core/ComponentStore_Enemy.cs' -and
                $currentMethod -eq 'ApplyEnemyDamage' -and
                -not $isDeclaration
            )
            $applyCalls += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                method = $currentMethod
                isDefinition = [bool]$isDeclaration
                isOverloadForward = [bool]$isOverloadForward
                isProductionCaller = (-not $isDeclaration -and -not $isOverloadForward)
                text = $trimmed
            }
        }

        if ($code -match '(?i)\b(?:List|ConcurrentBag|Queue|IReadOnlyList)<[^;>]+>\s*\[\]\s*(?<name>_?[A-Za-z_][A-Za-z0-9_]*Queue)\b') {
            $queueItem = [ordered]@{
                file = $relative
                line = $lineIndex + 1
                method = $currentMethod
                name = $Matches['name']
                text = $trimmed
            }
            $queueDeclarations += $queueItem
            if ($relativeNormalized -eq 'Systems/TowerAttackSystem.cs') {
                $towerAttackQueueDeclarations += $queueItem
            }
        }

        if ($null -ne $methodCandidate -and $methodCandidate -match '(?i)(Resolve|Drain|Collect|Apply|Queue).*Damage') {
            $loopKey = $relative + ':' + ($lineIndex + 1).ToString()
            if (-not $seenDamageLoopKeys.ContainsKey($loopKey)) {
                $seenDamageLoopKeys[$loopKey] = $true
                $damageLoops += [ordered]@{
                    file = $relative
                    line = $lineIndex + 1
                    method = $methodCandidate
                    text = $trimmed
                }
            }
        }

        if ($null -ne $methodCandidate -and $methodCandidate -match '^(Cast|Activate|TryActivate|Trigger|ExecuteAbility|AutoCast|UseSkill|DispatchUse)') {
            $abilityEntrypoints += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                method = $methodCandidate
                text = $trimmed
            }
        }

        foreach ($timerField in @('RemainingTime', 'TicksRemaining', 'TimeSinceLastTick', 'TickAccumulator', 'EnemyElementTimer')) {
            if ($code -match ('\b' + [regex]::Escape($timerField) + '\b')) {
                $timerKey = $relative + '|' + $currentMethod + '|' + $timerField
                if (-not $seenTimerKeys.ContainsKey($timerKey)) {
                    $seenTimerKeys[$timerKey] = $true
                    $effectTimerOwners += [ordered]@{
                        file = $relative
                        line = $lineIndex + 1
                        method = $currentMethod
                        field = $timerField
                    }
                }
            }
        }

        if ($code -match '\b(GameplayEffectDef|AppliedEffect)\b') {
            $effectType = [string]$Matches[1]
            $gameplayEffectUsages += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                method = $currentMethod
                type = $effectType
                access = if ($code -match ('\bnew\s+' + [regex]::Escape($effectType))) { 'construct' } elseif ($code -match '\b(SetEffect|AddEffect|GetEffect)\b') { 'store-api' } elseif ($code -match '(RemainingTime|TicksRemaining|TimeSinceLastTick|StackCount)') { 'runtime-field' } else { 'reference' }
                text = $trimmed
            }
        }

        if ($relativeNormalized -eq 'Core/SystemRegistry.cs') {
            if ($code -match '\bpublic\s+(?<type>[A-Za-z_][A-Za-z0-9_<>.]*)\?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;\s*private\s+set;\s*\}') {
                $registryProperties += [ordered]@{
                    file = $relative
                    line = $lineIndex + 1
                    type = $Matches['type']
                    name = $Matches['name']
                    text = $trimmed
                }
            }

            if ($code -match '\bscheduler\.(?<group>[A-Za-z_][A-Za-z0-9_]*)\.(?<slot>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^;]+);') {
                $assignment = [ordered]@{
                    file = $relative
                    line = $lineIndex + 1
                    group = $Matches['group']
                    slot = $Matches['slot']
                    value = $Matches['value'].Trim()
                    text = $trimmed
                }
                $groupAssignments += $assignment
                if ($assignment.value -eq 'null') {
                    $nullableGroupSlots += $assignment
                }
            }

            if ($code -match '\b(?<owner>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\?\.|\.)\s*(?<method>(?:Set|Inject|Wire)[A-Za-z0-9_]*)\s*\(') {
                $registryInjectors += [ordered]@{
                    file = $relative
                    line = $lineIndex + 1
                    enclosingMethod = $currentMethod
                    owner = $Matches['owner']
                    method = $Matches['method']
                    text = $trimmed
                }
            }
        }
    }
}

$directWriteFiles = @($directWrites | ForEach-Object { $_.file } | Sort-Object -Unique)
$enemyHealthAccessFiles = @($enemyHealthAccesses | ForEach-Object { $_.file } | Sort-Object -Unique)
$enemyHealthAccessOccurrenceCount = 0
foreach ($access in $enemyHealthAccesses) {
    $enemyHealthAccessOccurrenceCount += [int]$access.occurrenceCount
}
$strictDamageWrites = @($directWrites | Where-Object { $_.operator -eq '-=' })
$damageCandidateWrites = @($directWrites | Where-Object { $_.classification -eq 'DamageCandidate' })
$productionApplyCalls = @($applyCalls | Where-Object { $_.isProductionCaller })
$uniqueQueueNames = @($queueDeclarations | ForEach-Object { $_.file + '|' + $_.name } | Sort-Object -Unique)
$uniqueNullGroupSlots = @($nullableGroupSlots | ForEach-Object { $_.group + '.' + $_.slot } | Sort-Object -Unique)
$uniqueNullSlotNames = @($nullableGroupSlots | ForEach-Object { $_.slot } | Sort-Object -Unique)

$disabledDefinitions = @()
foreach ($typeName in $knownDefinitions) {
    $definitionFile = Join-Path $repoRoot ('Systems/' + $typeName + '.cs')
    $definitionExists = Test-Path -LiteralPath $definitionFile -PathType Leaf
    $constructed = 0
    if ($newTypeCounts.ContainsKey($typeName)) {
        $constructed = [int]$newTypeCounts[$typeName]
    }
    $disabledDefinitions += [ordered]@{
        name = $typeName
        definitionFile = if ($definitionExists) { Get-RelativePath -FullPath $definitionFile } else { $null }
        constructedCount = $constructed
        status = if ($constructed -eq 0) { 'disabled-or-unregistered' } else { 'constructed' }
    }
}

$commit = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace([string]$commit)) {
    $commit = 'unknown'
}

$ledger = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    commit = [string]$commit
    filesScanned = $productionFiles.Count
    enemyHealthAccesses = [ordered]@{
        rawLineCount = $enemyHealthAccesses.Count
        rawOccurrenceCount = [int]$enemyHealthAccessOccurrenceCount
        uniqueFiles = $enemyHealthAccessFiles.Count
        items = $enemyHealthAccesses
    }
    directWrites = [ordered]@{
        rawOccurrences = $directWrites.Count
        uniqueFiles = $directWriteFiles.Count
        strictMinusEqualsOccurrences = $strictDamageWrites.Count
        strictMinusEqualsFiles = @($strictDamageWrites | ForEach-Object { $_.file } | Sort-Object -Unique).Count
        damageCandidateOccurrences = $damageCandidateWrites.Count
        items = $directWrites
    }
    applyEnemyDamage = [ordered]@{
        allOccurrences = $applyCalls.Count
        productionCallerCount = $productionApplyCalls.Count
        productionCallers = $productionApplyCalls
        all = $applyCalls
    }
    damageLoops = [ordered]@{
        candidateMethods = $damageLoops
        queueDeclarations = $queueDeclarations
        uniqueQueueCount = $uniqueQueueNames.Count
        towerAttackQueueDeclarations = $towerAttackQueueDeclarations
        towerAttackQueueCount = @($towerAttackQueueDeclarations | ForEach-Object { $_.name } | Sort-Object -Unique).Count
    }
    abilityEntrypoints = $abilityEntrypoints
    effectTimerOwners = $effectTimerOwners
    registryProperties = [ordered]@{
        count = $registryProperties.Count
        systemPropertyCount = @($registryProperties | Where-Object { $_.type -match 'System$' }).Count
        items = $registryProperties
    }
    groupAssignments = [ordered]@{
        count = $groupAssignments.Count
        items = $groupAssignments
    }
    nullableGroupSlots = [ordered]@{
        assignments = $nullableGroupSlots
        assignmentCount = $nullableGroupSlots.Count
        uniqueGroupSlots = $uniqueNullGroupSlots
        uniqueGroupSlotCount = $uniqueNullGroupSlots.Count
        uniqueSlotNames = $uniqueNullSlotNames
        uniqueSlotNameCount = $uniqueNullSlotNames.Count
    }
    registryInjectors = [ordered]@{
        callCount = $registryInjectors.Count
        calls = $registryInjectors
    }
    gameplayEffectUsages = $gameplayEffectUsages
    compatibilityFacade = [ordered]@{
        legacyTypes = @('GameplayEffectDef', 'AppliedEffect')
        runtimeFieldsOnDefinition = @('RemainingTime', 'TicksRemaining')
        derivedPolicyFieldsOnDefinition = @('RefreshDuration')
        runtimeOwner = 'Systems/BuffSystem.cs + Core/ComponentStore_World.cs'
        migrationStatus = 'legacy-facade-required; M1a inventory only'
    }
    disabledDefinitions = $disabledDefinitions
}

Write-Output '=== ECS/GAS migration candidate inventory ==='
Write-Output ("Files scanned: {0}" -f $ledger.filesScanned)
Write-Output ("EnemyHealth indexed accesses: {0} raw lines / {1} occurrences in {2} files (reads, writes and comments)" -f `
    $ledger.enemyHealthAccesses.rawLineCount, $ledger.enemyHealthAccesses.rawOccurrenceCount, $ledger.enemyHealthAccesses.uniqueFiles)
Write-Output ("EnemyHealth executable write candidates: {0} occurrences in {1} files; strict -=: {2} occurrences in {3} files; DamageCandidate: {4}" -f `
    $ledger.directWrites.rawOccurrences, $ledger.directWrites.uniqueFiles, $ledger.directWrites.strictMinusEqualsOccurrences, $ledger.directWrites.strictMinusEqualsFiles, $ledger.directWrites.damageCandidateOccurrences)
Write-Output ("ApplyEnemyDamage production callers: {0}" -f $productionApplyCalls.Count)
Write-Output ("Queue declarations: {0} total ({1} in TowerAttackSystem); nullable group assignments: {2} ({3} unique group slots / {4} unique slot names)" -f `
    $ledger.damageLoops.uniqueQueueCount, $ledger.damageLoops.towerAttackQueueCount, $ledger.nullableGroupSlots.assignmentCount, $ledger.nullableGroupSlots.uniqueGroupSlotCount, $ledger.nullableGroupSlots.uniqueSlotNameCount)
Write-Output ("Registry nullable properties: {0} ({1} system properties); group assignments: {2}; wiring calls: {3}" -f `
    $ledger.registryProperties.count, $ledger.registryProperties.systemPropertyCount, $ledger.groupAssignments.count, $ledger.registryInjectors.callCount)
Write-Output ("Ability entrypoint candidates: {0}; effect timer owner candidates: {1}" -f `
    $ledger.abilityEntrypoints.Count, $ledger.effectTimerOwners.Count)

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $destination = $OutputPath
    if (-not [System.IO.Path]::IsPathRooted($destination)) {
        $destination = Join-Path $repoRoot $destination
    }
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json = $ledger | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $destination -Value $json -Encoding UTF8
    Write-Output ("Ledger written to: {0}" -f (Get-RelativePath -FullPath ([System.IO.Path]::GetFullPath($destination))))
}

exit 0
