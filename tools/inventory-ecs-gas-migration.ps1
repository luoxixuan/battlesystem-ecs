<#
Read-only candidate inventory for the ECS/GAS migration.

This is deliberately a conservative text scanner, not a C# parser. It keeps
raw occurrences and uncertain classifications so that reviewers can inspect them
instead of presenting a misleadingly precise number. Line comments are
ignored; block-comment/preprocessor edge cases may still be reported and
must be reviewed manually.

Usage:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1 -OutputPath C:\temp\gas-migration-ledger.json
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1 -OutputPath C:\temp\gas-migration-ledger.json -Force

PowerShell 7 users may replace powershell.exe with pwsh.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputPath,

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$scanDirectories = @('Core', 'Systems')
$productionFiles = @()
$registrationSpecPath = Join-Path $repoRoot 'tools/system-registration-spec.json'
$registrationSpec = $null
if (Test-Path -LiteralPath $registrationSpecPath -PathType Leaf) {
    $registrationSpec = Get-Content -Raw -LiteralPath $registrationSpecPath -Encoding UTF8 | ConvertFrom-Json
}
$productionFilePaths = @()

function Get-OrdinalUnique {
    param([object[]]$Values)

    $items = [string[]]@($Values | ForEach-Object { [string]$_ })
    [Array]::Sort($items, [System.StringComparer]::Ordinal)
    $result = @()
    $previous = $null
    $hasPrevious = $false
    foreach ($item in $items) {
        if (-not $hasPrevious -or -not [string]::Equals($previous, $item, [System.StringComparison]::Ordinal)) {
            $result += $item
            $previous = $item
            $hasPrevious = $true
        }
    }
    return $result
}

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
    $productionFilePaths += @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName })
}
$productionFilePaths = [string[]]$productionFilePaths
[Array]::Sort($productionFilePaths, [System.StringComparer]::OrdinalIgnoreCase)
$productionFiles = @($productionFilePaths | ForEach-Object { Get-Item -LiteralPath $_ })

$surfaceFilePaths = @($productionFilePaths)
$programPath = Join-Path $repoRoot 'Program.cs'
if (Test-Path -LiteralPath $programPath -PathType Leaf) { $surfaceFilePaths += $programPath }
foreach ($configRootName in @('Data', 'game_config.json')) {
    $configRoot = Join-Path $repoRoot $configRootName
    if (Test-Path -LiteralPath $configRoot -PathType Container) {
        $surfaceFilePaths += @(Get-ChildItem -LiteralPath $configRoot -Recurse -File -Filter '*.json' |
            Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } |
            ForEach-Object { $_.FullName })
    }
    elseif (Test-Path -LiteralPath $configRoot -PathType Leaf) {
        $surfaceFilePaths += $configRoot
    }
}
$surfaceFilePaths = [string[]]@(Get-OrdinalUnique -Values @($surfaceFilePaths |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }))
$surfaceFiles = @($surfaceFilePaths | ForEach-Object { Get-Item -LiteralPath $_ })

$surfaceDefinitions = @(
    [ordered]@{ name = 'playerCurrentHealth'; pattern = '\bPlayerCurrentHealth\b'; scope = 'Core, Systems' },
    [ordered]@{ name = 'shield'; pattern = '\b(?:PlayerShield|EnemyShield|ShieldAmount|ShieldChanged|ApplyShield|SetShield)\b'; scope = 'Core, Systems' },
    [ordered]@{ name = 'mana'; pattern = '\b(?:PlayerMana|ManaSystem|ManaCost|CurrentMana|MaxMana|ConsumeMana|RestoreMana)\b'; scope = 'Core, Systems, configuration' },
    [ordered]@{ name = 'typedAttributeEffectTriggerRuntime'; pattern = '\b(?:AttributeKey|AttributeSet|AttributeAggregator|GameplayEffectDefinition|GameplayEffectRuntime|GameplayTriggerRuntime|TriggerDefinition|EffectRequest|ResourceRequest|DamageRequest)\b'; scope = 'Core, Systems' },
    [ordered]@{ name = 'damageResourceWriters'; pattern = '\b(?:ApplyDamageAuthority|ApplyEnemyDamage|DamageResolver\s*\.\s*(?:TryApply|Submit)|ResourceResolver\s*\.\s*(?:TryApply|Submit)|PlayerCurrentHealth\s*\[[^\]]+\]\s*[-+*/]?=|EnemyHealth\s*\[[^\]]+\]\s*[-+*/]?=|PlayerShield\s*\[[^\]]+\]\s*[-+*/]?=|PlayerMana\s*\[[^\]]+\]\s*[-+*/]?=)'; scope = 'Core, Systems' },
    [ordered]@{ name = 'gameplayAndLegacyEventBridge'; pattern = '\b(?:GameplayEvent|GameplayEventQueue|IBattleEventBus|EventBus|EventChannel|OnDamageDealt|OnEntityKilled|OnEnemyKilled|OnTowerKill)\b'; scope = 'Core, Systems' },
    [ordered]@{ name = 'phaseAndStateTransitions'; pattern = '\b(?:GameState|StateMachine|TransitionTo|OnEnter|OnExit|scheduler\s*\.\s*Phase|Phase\s*=)\b'; scope = 'Core, Systems, Program' },
    [ordered]@{ name = 'abilityConfigParserAndSource'; pattern = '\b(?:GameplayAbilityDef|GameplayAbility|SkillDefs|GameConfigLoader|TryGetSkillById|GetSkillIdByName|ExecuteAbility|CastSkill|AutoCast|GlobalSkillDef)\b|"(?:Skills|SkillDefs|AutoSkills|GlobalSkills)"\s*:'; scope = 'Core, Systems, Program, JSON configuration' },
    [ordered]@{ name = 'registrySchedulerComposition'; pattern = '\b(?:SystemRegistry|FrameScheduler|CreateAll|WireDependencies|AssignToGroups|RunWavePhase)\b'; scope = 'Core, Systems, Program' }
)
$surfaceOccurrences = @{}
foreach ($definition in $surfaceDefinitions) { $surfaceOccurrences[$definition.name] = @() }

$enemyHealthAccesses = @()
$enemyHealthAccessOccurrenceCount = 0
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
$registrationEntries = @()
$registrationBindings = @()
$registrationDependencyEdges = @()
$registrationWiring = @()

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
        $rawEnemyHealthOccurrenceCount = [int]([regex]::Matches(
            $rawLine,
            '\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?EnemyHealth\s*\[[^\]]+\]'
        ).Count)
        if ($rawEnemyHealthOccurrenceCount -gt 0) {
            $enemyHealthAccessOccurrenceCount += $rawEnemyHealthOccurrenceCount
            $enemyHealthAccess = [ordered]@{
                file = $relative
                line = $lineIndex + 1
                occurrenceCount = [int]([regex]::Matches($rawLine, '\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?EnemyHealth\s*\[[^\]]+\]').Count)
                isCommentOnly = $rawLine.TrimStart().StartsWith('//')
                text = $rawLine.Trim()
            }
            $enemyHealthAccesses += $enemyHealthAccess
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

$registrationSchemaVersion = 0
if ($null -ne $registrationSpec) {
    $registrationSchemaVersion = [int]$registrationSpec.schemaVersion
    foreach ($entry in @($registrationSpec.registrations)) {
        $recipe = $entry.recipe
        $registrationEntries += [ordered]@{
            id = [string]$entry.id
            property = [string]$entry.property
            serviceType = [string]$entry.serviceType
            enabled = [bool]$entry.enabled
            featurePolicy = [string]$entry.featurePolicy
            ownerToken = [string]$entry.ownerToken
            dependencyCount = @($entry.dependencies).Count
            frameBindingCount = @($entry.frameBindings).Count
            factory = if ($null -ne $recipe) { [string]$recipe.factory } else { $null }
            wire = if ($null -ne $recipe) { [string]$recipe.wire } else { $null }
            bind = if ($null -ne $recipe) { [string]$recipe.bind } else { $null }
            reason = [string]$entry.reason
        }
        foreach ($dependency in @($entry.dependencies)) {
            $registrationDependencyEdges += [ordered]@{
                registration = [string]$entry.id
                dependency = [string]$dependency
            }
        }
        foreach ($binding in @($entry.frameBindings)) {
            if ($null -eq $binding) { continue }
            $registrationBindings += [ordered]@{
                registration = [string]$entry.id
                property = [string]$entry.property
                nodeId = [string]$binding.nodeId
                phase = [string]$binding.phase
                executionPolicy = [string]$binding.executionPolicy
                requiredTokens = @($binding.requiredTokens)
                providedTokens = @($binding.providedTokens)
            }
        }
        if ([bool]$entry.enabled -and $null -ne $recipe) {
            $registrationWiring += [ordered]@{
                registration = [string]$entry.id
                factory = [string]$recipe.factory
                wire = [string]$recipe.wire
                bind = [string]$recipe.bind
            }
        }
    }
}

foreach ($file in $surfaceFiles) {
    $relative = Get-RelativePath -FullPath $file.FullName
    $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $rawLine = [string]$lines[$lineIndex]
        $code = Remove-LineComment -Text $rawLine
        if ([string]::IsNullOrWhiteSpace($code)) { continue }
        foreach ($definition in $surfaceDefinitions) {
            $matches = [regex]::Matches($code, $definition.pattern)
            if ($matches.Count -eq 0) { continue }
            $surfaceOccurrences[$definition.name] += [ordered]@{
                file = $relative
                line = $lineIndex + 1
                occurrenceCount = $matches.Count
                text = $code.Trim()
            }
        }
    }
}

$surfaceCoverage = @()
foreach ($definition in $surfaceDefinitions) {
    $items = @($surfaceOccurrences[$definition.name])
    $occurrenceCount = 0
    foreach ($item in $items) { $occurrenceCount += [int]$item.occurrenceCount }
    $surfaceCoverage += [ordered]@{
        name = $definition.name
        status = if ($items.Count -gt 0) { 'known' } else { 'unknown' }
        scope = $definition.scope
        rawLineCount = $items.Count
        rawOccurrenceCount = $occurrenceCount
        items = $items
        limitation = 'Text candidates only; semantic ownership, aliases, reflection and generated code remain unknown until reviewed.'
    }
}

$directWriteFiles = @(Get-OrdinalUnique -Values @($directWrites | ForEach-Object { $_.file }))
$enemyHealthAccessFiles = @(Get-OrdinalUnique -Values @($enemyHealthAccesses | ForEach-Object { $_.file }))
$strictDamageWrites = @($directWrites | Where-Object { $_.operator -eq '-=' })
$damageCandidateWrites = @($directWrites | Where-Object { $_.classification -eq 'DamageCandidate' })
$productionApplyCalls = @($applyCalls | Where-Object { $_.isProductionCaller })
$uniqueQueueNames = @(Get-OrdinalUnique -Values @($queueDeclarations | ForEach-Object { $_.file + '|' + $_.name }))
$uniqueNullGroupSlots = @(Get-OrdinalUnique -Values @($nullableGroupSlots | ForEach-Object { $_.group + '.' + $_.slot }))
$uniqueNullSlotNames = @(Get-OrdinalUnique -Values @($nullableGroupSlots | ForEach-Object { $_.slot }))

$disabledDefinitions = @()
foreach ($typeName in $knownDefinitions) {
    $definitionFile = Join-Path $repoRoot ('Systems/' + $typeName + '.cs')
    $definitionExists = Test-Path -LiteralPath $definitionFile -PathType Leaf
    $constructed = 0
    if ($newTypeCounts.ContainsKey($typeName)) {
        $constructed = [int]$newTypeCounts[$typeName]
    }
    $registrationId = $typeName -replace 'System$', ''
    $manifestEntry = @($registrationEntries | Where-Object { $_.id -eq $registrationId } | Select-Object -First 1)
    $manifestStatus = if ($manifestEntry.Count -eq 0) { 'not-in-registration-spec' } elseif ($manifestEntry[0].enabled) { 'enabled' } else { 'disabled' }
    $disabledDefinitions += [ordered]@{
        name = $typeName
        definitionFile = if ($definitionExists) { Get-RelativePath -FullPath $definitionFile } else { $null }
        constructedCount = $constructed
        registrationStatus = $manifestStatus
        status = if ($manifestStatus -eq 'enabled') { 'registered-enabled' } elseif ($constructed -eq 0) { 'disabled-or-unregistered' } else { 'constructed-compatibility-only' }
    }
}

$commitLines = @(& git -C $repoRoot rev-parse HEAD 2>$null)
$commitExitCode = $LASTEXITCODE
$commit = if ($commitLines.Count -gt 0) { [string]$commitLines[0] } else { $null }
if ($commitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string]$commit)) {
    throw 'Unable to read commit from git; inventory generation requires auditable source identity.'
}
$sourceCommitLines = @(& git -C $repoRoot show -s --format=%cI HEAD 2>$null)
$sourceCommitExitCode = $LASTEXITCODE
$sourceCommitAt = if ($sourceCommitLines.Count -gt 0) { [string]$sourceCommitLines[0] } else { $null }
if ($sourceCommitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string]$sourceCommitAt)) {
    throw 'Unable to read commit timestamp from git; inventory generation requires auditable source identity.'
}
$canonicalGeneratedAt = [System.Convert]::ToString($sourceCommitAt)

$unityRoot = 'F:\AI\BattleSystem-ECS-Unity'
$unityVersionPath = Join-Path $unityRoot 'ProjectSettings\ProjectVersion.txt'
$battleDriverPath = Join-Path $unityRoot 'Assets\Scripts\BattleDriver.cs'
$unityCoreDllPath = Join-Path $unityRoot 'Assets\Plugins\BattleSystemECS.Core.dll'
$unityAvailable = Test-Path -LiteralPath $unityRoot -PathType Container
$unityVersion = $null
if (Test-Path -LiteralPath $unityVersionPath -PathType Leaf) {
    $versionLine = @(Get-Content -LiteralPath $unityVersionPath -Encoding UTF8 |
        Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1)
    if ($versionLine.Count -gt 0) { $unityVersion = ([string]$versionLine[0]).Split(':', 2)[1].Trim() }
}
$unityDllHash = $null
$unityDllVersion = $null
if (Test-Path -LiteralPath $unityCoreDllPath -PathType Leaf) {
    $unityDllHash = (Get-FileHash -LiteralPath $unityCoreDllPath -Algorithm SHA256).Hash
    $unityDllVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($unityCoreDllPath).FileVersion
}

$benchmarkPath = Join-Path $repoRoot 'Systems/BenchmarkSystem.cs'
$benchmarkSource = if (Test-Path -LiteralPath $benchmarkPath -PathType Leaf) {
    Get-Content -LiteralPath $benchmarkPath -Raw -Encoding UTF8
} else {
    ''
}
$benchmarkUsesRegistry = [regex]::IsMatch($benchmarkSource, '\bSystemRegistry\b')

$ledger = [ordered]@{
    schemaVersion = 2
    generatedAt = $canonicalGeneratedAt
    generationPolicy = 'canonical-source-commit-time'
    commit = [string]$commit
    filesScanned = $productionFiles.Count
    surfaceFilesScanned = $surfaceFiles.Count
    coverage = [ordered]@{
        status = 'known'
        scannedRoots = @('Core/**/*.cs', 'Systems/**/*.cs', 'Program.cs', 'Data/**/*.json', 'game_config.json')
        candidateSurfaces = $surfaceCoverage
        semanticCompleteness = [ordered]@{
            status = 'unknown'
            reason = 'The scanner is lexical. Dynamic dispatch, reflection, generated files, external packages and semantic writer ownership require follow-up review.'
        }
        testsAndGeneratedOutputs = [ordered]@{
            status = 'unknown'
            reason = 'Tests, bin/obj and generated outputs are intentionally outside the first production/config candidate scan.'
        }
    }
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
        strictMinusEqualsFiles = @(Get-OrdinalUnique -Values @($strictDamageWrites | ForEach-Object { $_.file })).Count
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
        towerAttackQueueCount = @(Get-OrdinalUnique -Values @($towerAttackQueueDeclarations | ForEach-Object { $_.name })).Count
    }
    abilityEntrypoints = $abilityEntrypoints
    effectTimerOwners = $effectTimerOwners
    registryProperties = [ordered]@{
        count = $registryProperties.Count
        systemPropertyCount = @($registryProperties | Where-Object { $_.type -match 'System$' }).Count
        items = $registryProperties
    }
    registrationModel = [ordered]@{
        source = if ($null -ne $registrationSpec) { 'tools/system-registration-spec.json' } else { 'unavailable' }
        schemaVersion = $registrationSchemaVersion
        registrationCount = $registrationEntries.Count
        enabledCount = @($registrationEntries | Where-Object enabled).Count
        disabledCount = @($registrationEntries | Where-Object { -not $_.enabled }).Count
        dependencyEdgeCount = $registrationDependencyEdges.Count
        frameBindingCount = $registrationBindings.Count
        typedRecipeCount = $registrationWiring.Count
        registrations = $registrationEntries
        dependencyEdges = $registrationDependencyEdges
        frameBindings = $registrationBindings
        typedRecipes = $registrationWiring
    }
    groupAssignments = [ordered]@{
        source = 'legacy Core/SystemRegistry.cs text scan; schema-v3 frame bindings are in registrationModel'
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
        source = 'legacy Core/SystemRegistry.cs text scan; schema-v3 typed recipes are in registrationModel'
        legacyTextCallCount = $registryInjectors.Count
        legacyTextCalls = $registryInjectors
        typedRecipeCount = $registrationWiring.Count
        typedRecipes = $registrationWiring
    }
    gameplayEffectUsages = $gameplayEffectUsages
    compatibilityFacade = [ordered]@{
        legacyTypes = @('GameplayEffectDef', 'AppliedEffect')
        runtimeFieldsOnDefinition = @('RemainingTime', 'TicksRemaining')
        derivedPolicyFieldsOnDefinition = @('RefreshDuration')
        runtimeOwner = 'Systems/BuffSystem.cs + Core/ComponentStore_World.cs'
        migrationStatus = 'legacy facade retained; typed runtime ownership migrated; removal requires public compatibility evidence'
    }
    benchmarkComposition = [ordered]@{
        benchmarkEntry = 'Systems/BenchmarkSystem.cs'
        productionEntry = 'Core/SystemRegistry.cs'
        usesProductionSystemRegistry = $benchmarkUsesRegistry
        status = if ($benchmarkUsesRegistry) { 'registry-backed' } else { 'manual-composition-gap' }
        evidence = if ($benchmarkUsesRegistry) { 'Benchmark source references SystemRegistry.' } else { 'Benchmark source does not reference SystemRegistry.' }
    }
    disabledDefinitions = $disabledDefinitions
    unityWiring = [ordered]@{
        root = $unityRoot
        status = if ($unityAvailable) { 'known' } else { 'unavailable' }
        projectVersion = [ordered]@{
            status = if ($null -ne $unityVersion) { 'known' } elseif ($unityAvailable) { 'unknown' } else { 'unavailable' }
            path = $unityVersionPath
            value = $unityVersion
        }
        battleDriver = [ordered]@{
            status = if (Test-Path -LiteralPath $battleDriverPath -PathType Leaf) { 'known' } elseif ($unityAvailable) { 'unknown' } else { 'unavailable' }
            path = $battleDriverPath
        }
        coreDll = [ordered]@{
            status = if ($null -ne $unityDllHash) { 'known' } elseif ($unityAvailable) { 'unknown' } else { 'unavailable' }
            path = $unityCoreDllPath
            sha256 = $unityDllHash
            fileVersion = $unityDllVersion
        }
        limitation = 'Availability and file identity are known when present; runtime-loaded assembly identity and scene binding remain unknown without running Unity.'
    }
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
Write-Output ("Registration schema v{0}: {1} registrations ({2} enabled / {3} disabled), {4} dependency edges, {5} frame bindings, {6} typed recipes" -f `
    $ledger.registrationModel.schemaVersion, $ledger.registrationModel.registrationCount, $ledger.registrationModel.enabledCount, $ledger.registrationModel.disabledCount, `
    $ledger.registrationModel.dependencyEdgeCount, $ledger.registrationModel.frameBindingCount, $ledger.registrationModel.typedRecipeCount)
Write-Output ("Legacy registry text scan retained for compatibility: {0} nullable properties, {1} assignments, {2} wiring calls" -f `
    $ledger.registryProperties.count, $ledger.groupAssignments.count, $ledger.registryInjectors.legacyTextCallCount)
Write-Output ("Ability entrypoint candidates: {0}; effect timer owner candidates: {1}" -f `
    $ledger.abilityEntrypoints.Count, $ledger.effectTimerOwners.Count)
Write-Output ("Benchmark composition: {0} (uses production registry: {1})" -f `
    $ledger.benchmarkComposition.status, $ledger.benchmarkComposition.usesProductionSystemRegistry)
Write-Output ("Expanded surfaces: {0} files; semantic completeness: {1}; Unity wiring: {2}" -f `
    $ledger.surfaceFilesScanned, $ledger.coverage.semanticCompleteness.status, $ledger.unityWiring.status)

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $destination = $OutputPath
    if (-not [System.IO.Path]::IsPathRooted($destination)) {
        $destination = Join-Path $repoRoot $destination
    }
    $destination = [System.IO.Path]::GetFullPath($destination)
    if (Test-Path -LiteralPath $destination -PathType Container) {
        throw ("OutputPath must be a file, but a directory was provided: {0}" -f $destination)
    }

    $rootPrefix = $repoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($destination.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relativeDestination = $destination.Substring($rootPrefix.Length).Replace('\', '/')
        & git -C $repoRoot ls-files --error-unmatch -- $relativeDestination 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) {
            throw ("OutputPath targets a tracked repository file and is refused: {0}" -f $relativeDestination)
        }
    }
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and -not $Force) {
        throw ("OutputPath already exists; pass -Force to replace this untracked output: {0}" -f $destination)
    }

    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json = $ledger | ConvertTo-Json -Depth 12 -Compress
    $json = $json.Replace('\u003c', '<').Replace('\u003e', '>').Replace('\u0026', '&').Replace('\u0027', "'")
    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($destination, $json, $utf8NoBom)
    Write-Output ("Ledger written to: {0}" -f (Get-RelativePath -FullPath $destination))
}

exit 0
