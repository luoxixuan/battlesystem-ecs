param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Core\SystemRegistry.cs'),
    [string]$Spec = (Join-Path $PSScriptRoot 'system-registration-spec.json'),
    [string]$Output = (Join-Path $PSScriptRoot '..\docs\ecs-gas-m7-nullable-ledger.md'),
    [string]$ManifestOutput = (Join-Path $PSScriptRoot '..\Core\SystemRegistrationManifest.generated.cs')
)
$registryText = Get-Content -Raw $Source
$document = Get-Content -Raw $Spec | ConvertFrom-Json -Depth 20
if ($document.schemaVersion -ne 3) { throw "Unsupported registration spec schema: $($document.schemaVersion)" }
$entries = @($document.registrations)
if ($entries.Count -eq 0) { throw 'Registration spec is empty.' }
$stageNames = @('Construction', 'Wiring', 'Binding')
$factoryStage = [string]$document.installationStages.factory
$wireStage = [string]$document.installationStages.wire
$bindStage = [string]$document.installationStages.bind
foreach ($stage in @($factoryStage, $wireStage, $bindStage)) {
    if ($stageNames -notcontains $stage) { throw "Unknown installation stage '$stage'." }
}
if ($stageNames.IndexOf($factoryStage) -ge $stageNames.IndexOf($wireStage) -or
    $stageNames.IndexOf($wireStage) -ge $stageNames.IndexOf($bindStage)) {
    throw 'Installation stages must be strictly ordered: factory < wire < bind.'
}
$ids = @{}
foreach ($entry in $entries) {
    if ([string]::IsNullOrWhiteSpace($entry.id) -or [string]::IsNullOrWhiteSpace($entry.property) -or [string]::IsNullOrWhiteSpace($entry.serviceType) -or [string]::IsNullOrWhiteSpace($entry.featurePolicy)) { throw "Registration has missing fields: $($entry.id)" }
    if ($ids.ContainsKey($entry.id)) { throw "Duplicate registration id: $($entry.id)" }
    $ids[$entry.id] = $true
    $recipeNames = @([string]$entry.recipe.factory, [string]$entry.recipe.wire, [string]$entry.recipe.bind)
    if ($entry.enabled -and ($recipeNames | Where-Object { $_ -notmatch '^[A-Z][A-Za-z0-9]*$' }).Count -ne 0) { throw "Enabled registration has invalid typed recipe identifiers: $($entry.id)" }
    if (-not $entry.enabled -and ($recipeNames | Where-Object { -not [string]::IsNullOrEmpty($_) }).Count -ne 0) { throw "Disabled registration has executable recipes: $($entry.id)" }
    foreach ($legacyField in 'factoryCode','wireCode','bindCode','factoryMethod','wireMethod','bindMethod') {
        if ($entry.PSObject.Properties.Name -contains $legacyField) { throw "Registration '$($entry.id)' uses forbidden free-form recipe field '$legacyField'." }
    }
    $bindings = @($entry.frameBindings)
    if ($entry.enabled -and [string]::IsNullOrWhiteSpace($entry.ownerToken)) { throw "Enabled registration has no owner token: $($entry.id)" }
    $providedTokens = @($entry.providedTokens)
    if ($entry.enabled -and ($providedTokens.Count -eq 0 -or $providedTokens -notcontains $entry.ownerToken)) { throw "Enabled registration does not provide owner token: $($entry.id)" }
    if (-not $entry.enabled -and (-not [string]::IsNullOrWhiteSpace($entry.ownerToken) -or $bindings.Count -ne 0 -or $providedTokens.Count -ne 0)) { throw "Disabled registration owns production bindings: $($entry.id)" }
    foreach ($binding in $bindings) {
        if ($binding.registrationId -ne $entry.id -or [string]::IsNullOrWhiteSpace($binding.nodeId)) { throw "Invalid frame binding owner/node for '$($entry.id)'" }
        if (@('All','Build','Wave','NonWave') -notcontains [string]$binding.phase) { throw "Unknown frame binding phase '$($binding.phase)' for '$($binding.nodeId)'" }
        if (@('SerialPrepare','SerialUpdate','ParallelDisjointWrite','InternalParallelCollectSerialCommit','SerialCommit','PresentationCommit') -notcontains [string]$binding.executionPolicy) { throw "Unknown execution policy '$($binding.executionPolicy)' for '$($binding.nodeId)'" }
        if (@($binding.requiredTokens).Count -eq 0 -or @($binding.requiredTokens) -notcontains $entry.ownerToken) { throw "Frame binding '$($binding.nodeId)' must require owner token '$($entry.ownerToken)'" }
    }
}
$bindingIds = @($entries | ForEach-Object { @($_.frameBindings) } | ForEach-Object nodeId)
if (($bindingIds | Sort-Object -Unique).Count -ne $bindingIds.Count) { throw 'Duplicate frame binding node id in registration spec.' }
foreach ($entry in $entries) {
    foreach ($dependency in @($entry.dependencies)) {
        if (-not $ids.ContainsKey($dependency)) { throw "Unknown dependency '$dependency' for '$($entry.id)'" }
        $target = $entries | Where-Object id -eq $dependency | Select-Object -First 1
        if ($entry.enabled -and -not $target.enabled) { throw "Enabled registration '$($entry.id)' depends on disabled '$dependency'" }
    }
}
$properties = @([regex]::Matches($registryText, 'public\s+(?<type>[A-Za-z0-9_.]+)\?\s+(?<name>[A-Za-z0-9_]+)\s*\{\s*get;\s*private\s+set;\s*\}') | Where-Object { $_.Groups['type'].Value -match '(System|EventBus|Handler)$' } | ForEach-Object { $_.Groups['name'].Value } | Sort-Object)
$enabledProperties = @($entries | Where-Object { $_.enabled -and $_.serviceType -ne 'composition' } |
    ForEach-Object property | Sort-Object)
if (($properties -join '|') -ne ($enabledProperties -join '|')) { throw "Registry/spec inventory mismatch. Registry=$($properties -join ',') Spec=$($enabledProperties -join ',')" }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# M7 SystemRegistry Nullable Ledger')
$lines.Add('')
$lines.Add('Generated from explicit spec `tools/system-registration-spec.json` and verified against `Core/SystemRegistry.cs`.')
$lines.Add('')
$lines.Add('| property | system kind | dependencies | policy | recipe |')
$lines.Add('|---|---|---|---|---|')
foreach ($entry in $entries) {
    $deps = @($entry.dependencies) -join ','
    $recipe = if ($entry.enabled) { "$($entry.recipe.factory) / $($entry.recipe.wire) / $($entry.recipe.bind)" } else { 'disabled: no executable recipe' }
    $lines.Add("| $($entry.property) | $($entry.serviceType) | $deps | $($entry.featurePolicy) | $recipe |")
}
$fullOutput = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force (Split-Path -Parent $fullOutput) | Out-Null
[IO.File]::WriteAllLines($fullOutput, $lines, [Text.Encoding]::UTF8)
function Quote([string]$value) { return '"' + $value.Replace('\','\\').Replace('"','\"') + '"' }
function StringArray($values) { $items=@($values); if($items.Count-eq 0){return 'Array.Empty<string>()'}; return 'new[]{'+(($items|ForEach-Object{Quote ([string]$_)})-join ',')+'}' }
function Dependencies($entry) { return StringArray @($entry.dependencies) }
function FrameBindings($entry) {
    $items = @($entry.frameBindings)
    if ($items.Count -eq 0) { return 'Array.Empty<FrameBindingRegistration>()' }
    $rendered = foreach ($binding in $items) {
        $phase = switch ([string]$binding.phase) {
            'NonWave' { '(FramePhaseMask.All&~(FramePhaseMask.Build|FramePhaseMask.Wave))' }
            default { 'FramePhaseMask.' + [string]$binding.phase }
        }
        'new FrameBindingRegistration({0},{1},{2},FrameExecutionSemantics.{3},{4},{5})' -f
            (Quote $binding.nodeId),(Quote $binding.registrationId),$phase,$binding.executionPolicy,
            (StringArray @($binding.requiredTokens)),(StringArray @($binding.providedTokens))
    }
    return 'new[]{'+($rendered -join ',')+'}'
}
$code=[System.Collections.Generic.List[string]]::new()
$code.Add('// 由 tools/generate-system-registry-ledger.ps1 根据 tools/system-registration-spec.json 生成，请勿手改。')
$code.Add('#nullable enable')
$code.Add('using System;')
$code.Add('using BattleSystemECS.Config;')
$code.Add('using BattleSystemECS.Systems;')
$code.Add('namespace BattleSystemECS.Core {')
$code.Add('internal enum RegistrationStage { Construction, Wiring, Binding }')
$code.Add('internal delegate void SystemFactory(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus);')
$code.Add('internal delegate void SystemWire(SystemRegistry registry, ComponentStore store, int playerId);')
$code.Add('internal delegate void SystemBind(SystemRegistry registry, FrameScheduler scheduler);')
$code.Add('internal static class FrameRuntimeBindingCatalog {')
$code.Add('internal static bool TryGet(string nodeId, out FrameBindingRegistration registration) {')
$code.Add('switch (nodeId) {')
foreach ($entry in $entries) {
    foreach ($binding in @($entry.frameBindings)) {
        $phase = switch ([string]$binding.phase) {
            'NonWave' { '(FramePhaseMask.All&~(FramePhaseMask.Build|FramePhaseMask.Wave))' }
            default { 'FramePhaseMask.' + [string]$binding.phase }
        }
        $required = StringArray @($binding.requiredTokens)
        $provided = StringArray @($binding.providedTokens)
        $code.Add('case ' + (Quote $binding.nodeId) + ': registration = new FrameBindingRegistration(' + (Quote $binding.nodeId) + ',' + (Quote $binding.registrationId) + ',' + $phase + ',FrameExecutionSemantics.' + [string]$binding.executionPolicy + ',' + $required + ',' + $provided + '); return true;')
    }
}
$code.Add('default: registration = default(FrameBindingRegistration); return false;')
$code.Add('}')
$code.Add('}')
$code.Add('}')
$code.Add('internal readonly struct FrameBindingRegistration {')
$code.Add('public readonly string NodeId, RegistrationId; public readonly FramePhaseMask Phase; public readonly FrameExecutionSemantics ExecutionPolicy; public readonly string[] RequiredTokens, ProvidedTokens;')
$code.Add('public FrameBindingRegistration(string nodeId,string registrationId,FramePhaseMask phase,FrameExecutionSemantics executionPolicy,string[] requiredTokens,string[] providedTokens) { NodeId=nodeId; RegistrationId=registrationId; Phase=phase; ExecutionPolicy=executionPolicy; RequiredTokens=requiredTokens; ProvidedTokens=providedTokens; }')
$code.Add('}')
$code.Add('internal static class SystemRegistrationManifest {')
$code.Add('internal static readonly SystemRegistrationEntry[] Entries = new SystemRegistrationEntry[] {')
foreach($entry in $entries){
    $deps=Dependencies $entry;$root=if(@($entry.dependencies).Count-eq 0){'true'}else{'false'};$enabled=if($entry.enabled){'true'}else{'false'};$bindings=FrameBindings $entry;$provided=StringArray @($entry.providedTokens)
    $factory=if($entry.enabled){'SystemRegistry.'+$entry.recipe.factory}else{'null'};$wire=if($entry.enabled){'SystemRegistry.'+$entry.recipe.wire}else{'null'};$bind=if($entry.enabled){'SystemRegistry.'+$entry.recipe.bind}else{'null'}
    $source='tools/system-registration-spec.json:'+$entry.id
    $code.Add(('new SystemRegistrationEntry({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},RegistrationStage.{11},RegistrationStage.{12},RegistrationStage.{13},{14},{15},{16}),' -f (Quote $entry.id),(Quote $entry.property),(Quote $entry.serviceType),$deps,(Quote $entry.featurePolicy),(Quote $source),(Quote ([string]$entry.ownerToken)),$provided,$bindings,$enabled,$root,$factoryStage,$wireStage,$bindStage,$factory,$wire,$bind))
}
$code.Add('}; }')
$code.Add('internal readonly struct SystemRegistrationEntry {')
$code.Add('public readonly string Id, Property, Type, Policy, Source, OwnerToken; public readonly string[] Dependencies, ProvidedTokens; public readonly FrameBindingRegistration[] FrameBindings; public readonly bool Enabled, IsRoot; public readonly RegistrationStage FactoryStage, WireStage, BindStage; public readonly SystemFactory? Factory; public readonly SystemWire? Wire; public readonly SystemBind? Bind;')
$code.Add('public bool IsDisabled => !Enabled; public string Lifecycle => Enabled ? "typed-recipe" : "disabled"; public string Group => Enabled ? "Installer" : "Disabled";')
$code.Add('public SystemRegistrationEntry(string id,string property,string type,string[] dependencies,string policy,string source,string ownerToken,string[] providedTokens,FrameBindingRegistration[] frameBindings,bool enabled,bool isRoot,RegistrationStage factoryStage,RegistrationStage wireStage,RegistrationStage bindStage,SystemFactory? factory,SystemWire? wire,SystemBind? bind) { Id=id; Property=property; Type=type; Dependencies=dependencies; Policy=policy; Source=source; OwnerToken=ownerToken; ProvidedTokens=providedTokens; FrameBindings=frameBindings; Enabled=enabled; IsRoot=isRoot; FactoryStage=factoryStage; WireStage=wireStage; BindStage=bindStage; Factory=factory; Wire=wire; Bind=bind; }')
$code.Add('}')
$code.Add('}')
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($ManifestOutput),$code,[Text.Encoding]::UTF8)
