#nullable enable
using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    internal static class FrameRegistrationContractCatalog
    {
        internal const string DisabledOwnerToken = "registration.disabled-frame-slot";

        internal static FrameBindingRegistration Require(string nodeId, FramePhaseMask phase,
            FrameExecutionSemantics executionPolicy)
        {
            FrameBindingRegistration? found = null;
            foreach (var entry in SystemRegistrationManifest.Entries)
            {
                foreach (var binding in entry.FrameBindings)
                {
                    if (!string.Equals(binding.NodeId, nodeId, StringComparison.Ordinal)) continue;
                    if (found.HasValue)
                        throw new FrameGraphValidationException("Duplicate manifest frame binding: " + nodeId);
                    found = binding;
                }
            }
            if (!found.HasValue)
                throw new FrameGraphValidationException("Production frame node has no manifest binding: " + nodeId);
            FrameBindingRegistration contract = found.Value;
            if (contract.Phase != phase)
                throw new FrameGraphValidationException($"Frame binding phase mismatch for '{nodeId}': manifest={contract.Phase}, graph={phase}.");
            if (contract.ExecutionPolicy != executionPolicy)
                throw new FrameGraphValidationException($"Frame binding execution policy mismatch for '{nodeId}': manifest={contract.ExecutionPolicy}, graph={executionPolicy}.");
            return contract;
        }

        internal static string OwnerToken(FrameBindingRegistration contract)
        {
            foreach (var entry in SystemRegistrationManifest.Entries)
                if (string.Equals(entry.Id, contract.RegistrationId, StringComparison.Ordinal))
                {
                    if (entry.IsDisabled)
                        throw new FrameGraphValidationException("Frame binding owner is disabled: " + contract.RegistrationId);
                    return entry.OwnerToken;
                }
            throw new FrameGraphValidationException("Frame binding has unknown owner: " + contract.RegistrationId);
        }

        internal static void ValidateProductionGraph(FrameGraph graph, FrameScheduler scheduler)
            => ValidateContractSet(SystemRegistrationManifest.Entries, graph.Nodes,
                graph.AvailableDependencies, scheduler.IsRegistrationBindingComplete, scheduler.RuntimeFrameDeclarations);

        internal static void ValidateContractSet(IReadOnlyList<SystemRegistrationEntry> entries,
            IReadOnlyList<FrameNodeAdapter> nodes, IReadOnlyList<string> availableDependencies,
            Func<string, bool> isBindingComplete,
            IReadOnlyDictionary<string, FrameNodeRuntimeDeclaration>? runtimeDeclarations = null)
        {
            var actual = new Dictionary<string, FrameNodeAdapter>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (!actual.TryAdd(node.Metadata.Id.Value, node))
                    throw new FrameGraphValidationException("Duplicate production frame node: " + node.Metadata.Id.Value);
            }

            var expected = new Dictionary<string, FrameBindingRegistration>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.IsDisabled && entry.FrameBindings.Length != 0)
                    throw new FrameGraphValidationException("Disabled manifest owner declares frame bindings: " + entry.Id);
                foreach (var binding in entry.FrameBindings)
                {
                    if (!entry.Enabled)
                        throw new FrameGraphValidationException("Frame binding owner is disabled: " + entry.Id);
                    if (!string.Equals(binding.RegistrationId, entry.Id, StringComparison.Ordinal))
                        throw new FrameGraphValidationException($"Frame binding '{binding.NodeId}' owner mismatch: entry={entry.Id}, binding={binding.RegistrationId}.");
                    if (!expected.TryAdd(binding.NodeId, binding))
                        throw new FrameGraphValidationException("Duplicate manifest frame binding: " + binding.NodeId);
                    if (!isBindingComplete(entry.Id))
                        throw new FrameGraphValidationException($"Frame binding '{binding.NodeId}' owner binder did not execute: {entry.Id}.");
                }
            }

            foreach (var pair in actual)
            {
                if (!expected.TryGetValue(pair.Key, out FrameBindingRegistration binding))
                    throw new FrameGraphValidationException("Orphan production frame node: " + pair.Key);
                FrameNodeMetadata metadata = pair.Value.Metadata;
                if (runtimeDeclarations != null)
                {
                    if (!runtimeDeclarations.TryGetValue(pair.Key, out var declaration))
                        throw new FrameGraphValidationException("Production frame node has no runtime declaration: " + pair.Key);
                    if (!string.Equals(declaration.RegistrationId, binding.RegistrationId, StringComparison.Ordinal) ||
                        declaration.Phase != binding.Phase || declaration.ExecutionPolicy != binding.ExecutionPolicy ||
                        !SequenceEqual(declaration.RequiredTokens, binding.RequiredTokens))
                        throw new FrameGraphValidationException("Runtime frame node declaration drift: " + pair.Key);
                }
                string owner = OwnerToken(entries, binding);
                if (!string.Equals(metadata.AccessProfile.Owner.Value, owner, StringComparison.Ordinal))
                    throw new FrameGraphValidationException($"Frame binding owner mismatch for '{pair.Key}': manifest={owner}, graph={metadata.AccessProfile.Owner.Value}.");
                if (metadata.ActivePhases != binding.Phase)
                    throw new FrameGraphValidationException($"Frame binding phase mismatch for '{pair.Key}': manifest={binding.Phase}, graph={metadata.ActivePhases}.");
                if (metadata.ExecutionSemantics != binding.ExecutionPolicy)
                    throw new FrameGraphValidationException($"Frame binding execution policy mismatch for '{pair.Key}': manifest={binding.ExecutionPolicy}, graph={metadata.ExecutionSemantics}.");
                if (!SequenceEqual(metadata.RequiredDependencies, binding.RequiredTokens))
                    throw new FrameGraphValidationException("Frame binding required-token mismatch: " + pair.Key);
                foreach (string token in binding.ProvidedTokens)
                    if (!Contains(availableDependencies, token))
                        throw new FrameGraphValidationException($"Frame binding '{pair.Key}' did not provide token '{token}'.");
            }
            foreach (string nodeId in expected.Keys)
                if (!actual.ContainsKey(nodeId))
                    throw new FrameGraphValidationException("Manifest frame binding has no real production node: " + nodeId);
            if (runtimeDeclarations != null)
                foreach (string nodeId in runtimeDeclarations.Keys)
                    if (!expected.ContainsKey(nodeId))
                        throw new FrameGraphValidationException("Orphan runtime frame node declaration: " + nodeId);
        }

        private static string OwnerToken(IReadOnlyList<SystemRegistrationEntry> entries,
            FrameBindingRegistration contract)
        {
            foreach (var entry in entries)
                if (string.Equals(entry.Id, contract.RegistrationId, StringComparison.Ordinal))
                {
                    if (entry.IsDisabled)
                        throw new FrameGraphValidationException("Frame binding owner is disabled: " + contract.RegistrationId);
                    return entry.OwnerToken;
                }
            throw new FrameGraphValidationException("Frame binding has unknown owner: " + contract.RegistrationId);
        }

        private static bool SequenceEqual(IReadOnlyList<string> left, string[] right)
        {
            if (left.Count != right.Length) return false;
            for (int i = 0; i < right.Length; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool Contains(IReadOnlyList<string> values, string expected)
        {
            for (int i = 0; i < values.Count; i++)
                if (string.Equals(values[i], expected, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    internal static class FrameAdapterBindingCatalog
    {
        private static readonly Dictionary<string, string> Semantics =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "skill-buff.skill.update", "Update(delta)/skill-buff.skill.update" },
                { "ability.commit", "GraphCommitQueuedAbilities/ability.commit" },
                { "ai.burrow.apply", "ApplyBurrowEffects()/ai.burrow.apply" },
                { "ai.burrow.prepare", "SetTurn(turn)/ai.burrow.prepare" },
                { "ai.burrow.update", "Update(turn-step;legacy-delta-ignored)/ai.burrow.update" },
                { "ai.enemy-ability.cast-timers", "TickCastTimers(turn-step;legacy-delta-ignored)/ai.enemy-ability.cast-timers" },
                { "ai.enemy-ability.cooldowns", "UpdateCooldowns(delta)/ai.enemy-ability.cooldowns" },
                { "ai.enemy-ability.execute", "ExecuteAbilities()/ai.enemy-ability.execute" },
                { "ai.enemy-ability.prepare", "SetTurn(turn)/ai.enemy-ability.prepare" },
                { "ai.enemy-ability.update", "Update()/ai.enemy-ability.update" },
                { "ai.enemy-affix.update", "Update(delta)/ai.enemy-affix.update" },
                { "ai.enemy-strafe.prepare", "SetTurn()/ai.enemy-strafe.prepare" },
                { "ai.enemy-strafe.update", "Update()/ai.enemy-strafe.update" },
                { "ai.enemy.prepare", "SetTurn(turn,delta)/ai.enemy.prepare" },
                { "ai.enemy.update", "EnemyAISystem.Update+batch-local collect+serial death/attack/lifesteal commit/ai.enemy.update" },
                { "ai.fear.prepare", "SetTurn(turn)/ai.fear.prepare" },
                { "ai.fear.update", "Update(turn-step;legacy-delta-ignored)/ai.fear.update" },
                { "ai.life-link.cooldowns", "DecrementCooldowns(delta)/ai.life-link.cooldowns" },
                { "ai.life-link.prepare", "SetTurn(turn)/ai.life-link.prepare" },
                { "ai.life-link.update", "Update()/ai.life-link.update" },
                { "ai.lifesteal.prepare", "SetTurn(turn)/ai.lifesteal.prepare" },
                { "ai.lifesteal.update", "Update()/ai.lifesteal.update" },
                { "ai.magnetize.update", "Update(delta)/ai.magnetize.update" },
                { "ai.mana-burn.prepare", "SetTurn(turn)/ai.mana-burn.prepare" },
                { "ai.mana-burn.update", "ManaBurnSystem.Update+reusable active-index buffer+serial commit/ai.mana-burn.update" },
                { "ai.necromancer.prepare", "SetTurn(turn,turn)/ai.necromancer.prepare" },
                { "ai.necromancer.update", "Update(delta)/ai.necromancer.update" },
                { "ai.phase.prepare", "SetTurn(turn)/ai.phase.prepare" },
                { "ai.phase.update", "Update(delta)/ai.phase.update" },
                { "ai.sapper.prepare", "SetTurn(turn,delta)/ai.sapper.prepare" },
                { "ai.sapper.recompute", "RecomputeTowerSlows()/ai.sapper.recompute" },
                { "ai.sapper.update", "Update(delta)/ai.sapper.update" },
                { "ai.zone-control.update", "Update(delta)/ai.zone-control.update" },
                { "attribute.aggregate", "GraphAggregateAttributes/attribute.aggregate" },
                { "build.ability.commit", "GraphCommitQueuedAbilities/build.ability.commit" },
                { "build.ability.reject", "GraphRejectNonWaveAbilities/build.ability.reject" },
                { "build.auto-skill.update", "Update(false)/build.auto-skill.update" },
                { "build.damage.commit", "GraphCommitBuildDamage/build.damage.commit" },
                { "build.desperation.update", "Update()/build.desperation.update" },
                { "build.effect.tick", "GraphTickEffects(clock=Build)/build.effect.tick" },
                { "build.effect.tick.global", "GraphTickEffects(clock=Global)/build.effect.tick.global" },
                { "build.effect.tick.real", "GraphTickEffects(clock=RealTime)/build.effect.tick.real" },
                { "build.frame.close", "GraphCloseDeferredResolvers/build.frame.close" },
                { "build.gameplay-event.commit", "GraphCommitGameplayEvents/build.gameplay-event.commit" },
                { "build.global-skill.update", "Update(delta,true)/build.global-skill.update" },
                { "build.gold.update", "Update()/build.gold.update" },
                { "build.interest.update", "Update()/build.interest.update" },
                { "build.mana-shield.update", "Update(delta)/build.mana-shield.update" },
                { "build.mana.update", "Update(delta,true)/build.mana.update" },
                { "build.objective.update", "Update(delta,GameState.BuildPhase)/build.objective.update" },
                { "build.pre-fight-buff.update", "Update(delta)/build.pre-fight-buff.update" },
                { "build.resource-node.update", "Update(delta,GameState.BuildPhase)/build.resource-node.update" },
                { "build.resource.commit", "GraphCommitBuildResources/build.resource.commit" },
                { "build.shop-reroll.update", "Update()/build.shop-reroll.update" },
                { "build.skill.reject-pending", "RejectPendingSkillDamage()/build.skill.reject-pending" },
                { "build.skill.update", "Update(delta,false)/build.skill.update" },
                { "build.tower-income.update", "Update(delta)/build.tower-income.update" },
                { "build.tower-relocate.update", "Update()/build.tower-relocate.update" },
                { "build.upgrade.update", "Update()/build.upgrade.update" },
                { "cascade-death.callback-dispatch", "GraphDispatchDeathCallbacks/cascade-death.callback-dispatch" },
                { "cascade-death.resolve", "GraphPrepareDeaths/cascade-death.resolve" },
                { "cascade.damage.commit", "GraphCommitGameplayDamage/cascade.damage.commit" },
                { "cascade.resource.commit", "GraphCommitGameplayResources/cascade.resource.commit" },
                { "combat-setup.aura.prepare", "SetTurn()/combat-setup.aura.prepare" },
                { "combat-setup.curse.prepare", "SetTurn()/combat-setup.curse.prepare" },
                { "combat-setup.fortress.prepare", "SetTurn()/combat-setup.fortress.prepare" },
                { "combat-setup.frost-zone.prepare", "SetTurn(turn)/combat-setup.frost-zone.prepare" },
                { "combat-setup.frost-zone.update", "Update()/combat-setup.frost-zone.update" },
                { "combat-setup.global-skill.prepare", "SetTurn(turn)/combat-setup.global-skill.prepare" },
                { "combat-setup.heat.prepare", "SetTurn(turn)/combat-setup.heat.prepare" },
                { "combat-setup.hero.prepare", "SetTurn(turn)/combat-setup.hero.prepare" },
                { "combat-setup.hit-shield.prepare", "SetTurn(turn)/combat-setup.hit-shield.prepare" },
                { "combat-setup.hot-zone.prepare", "SetTurn(turn)/combat-setup.hot-zone.prepare" },
                { "combat-setup.link.prepare", "SetTurn()/combat-setup.link.prepare" },
                { "combat-setup.mana.prepare", "SetTurn()/combat-setup.mana.prepare" },
                { "combat-setup.overcharge.prepare", "SetTurn(turn)/combat-setup.overcharge.prepare" },
                { "combat-setup.player-attack.prepare", "SetTurn(turn)/combat-setup.player-attack.prepare" },
                { "combat-setup.pull-tower.prepare", "SetTurn()/combat-setup.pull-tower.prepare" },
                { "combat-setup.skill.prepare", "SetTurn(turn)/combat-setup.skill.prepare" },
                { "combat-setup.synergy.prepare", "SetTurn()/combat-setup.synergy.prepare" },
                { "combat-setup.taunt.prepare", "SetTurn()/combat-setup.taunt.prepare" },
                { "combat-setup.terrain-zone.prepare", "SetTurn(turn)/combat-setup.terrain-zone.prepare" },
                { "combat-setup.terrain-zone.update", "Update(delta)/combat-setup.terrain-zone.update" },
                { "combat-setup.tower-attack.prepare", "SetTurn(turn)/combat-setup.tower-attack.prepare" },
                { "combat-setup.wander.prepare", "SetTurn(turn)/combat-setup.wander.prepare" },
                { "combat-setup.wander.update", "Update()/combat-setup.wander.update" },
                { "combat.adrenaline.update", "Update(delta)/combat.adrenaline.update" },
                { "combat.aggro.update", "Update(delta)/combat.aggro.update" },
                { "combat.aura.resolve", "ResolveAuraBuffs()/combat.aura.resolve" },
                { "combat.beacon.prepare", "SetTurn()/combat.beacon.prepare" },
                { "combat.beacon.resolve", "ResolveBeaconBuffs()/combat.beacon.resolve" },
                { "combat.beam.update", "Update(delta)/combat.beam.update" },
                { "combat.bloodlust.update", "Update(turn)/combat.bloodlust.update" },
                { "combat.crest.update", "Update(delta)/combat.crest.update" },
                { "combat.culling.update", "Update(delta)/combat.culling.update" },
                { "combat.curse.resolve", "ResolveCurseDebuffs()/combat.curse.resolve" },
                { "combat.demolish.update", "Update()/combat.demolish.update" },
                { "combat.dispel.update", "Update(delta)/combat.dispel.update" },
                { "combat.echo-clone.update", "Update(delta)/combat.echo-clone.update" },
                { "combat.enemy-projectile.update", "Update(delta)/combat.enemy-projectile.update" },
                { "combat.energy.update", "Update(delta)/combat.energy.update" },
                { "combat.global-skill.update", "Update(delta,false)/combat.global-skill.update" },
                { "combat.heat.update", "Update(delta)/combat.heat.update" },
                { "combat.hero-skill.update", "Update(delta)/combat.hero-skill.update" },
                { "combat.hero.update", "Update(delta)/combat.hero.update" },
                { "combat.hit-shield.update", "Update(delta)/combat.hit-shield.update" },
                { "combat.link.update", "Update()/combat.link.update" },
                { "combat.mana-shield.update", "Update(delta)/combat.mana-shield.update" },
                { "combat.mana.update", "Update(delta,false)/combat.mana.update" },
                { "combat.momentum.update", "Update(delta)/combat.momentum.update" },
                { "combat.overcharge.update", "Update(delta)/combat.overcharge.update" },
                { "combat.pickup.update", "Update(delta)/combat.pickup.update" },
                { "combat.player-attack.update", "Update()/combat.player-attack.update" },
                { "combat.projectile.update", "Update(delta)/combat.projectile.update" },
                { "combat.pull-tower.update", "Update(delta)/combat.pull-tower.update" },
                { "combat.rally.consume", "GraphConsumeRally/combat.rally.consume" },
                { "combat.reflect.apply", "ApplyReflectDamage()/combat.reflect.apply" },
                { "combat.reflect.resolve", "ResolveReflect()/combat.reflect.resolve" },
                { "combat.sabotage.update", "Update(delta)/combat.sabotage.update" },
                { "combat.shrine.prepare", "SetTurn()/combat.shrine.prepare" },
                { "combat.shrine.resolve", "ResolveShrineBuffs()/combat.shrine.resolve" },
                { "combat.silence.update", "Update(delta)/combat.silence.update" },
                { "combat.stealth.update", "Update(delta)/combat.stealth.update" },
                { "combat.suicide-bomb.update", "Update()/combat.suicide-bomb.update" },
                { "combat.synergy.resolve-buff-shares", "ResolveBuffShares()/combat.synergy.resolve-buff-shares" },
                { "combat.synergy.update", "Update()/combat.synergy.update" },
                { "combat.taunt.resolve", "ResolveTauntAssignments()/combat.taunt.resolve" },
                { "combat.tower-active-skill.update", "Update(delta)/combat.tower-active-skill.update" },
                { "combat.tower-attack.update", "Update(delta)/combat.tower-attack.update" },
                { "combat.tower-morph.update", "Update(delta)/combat.tower-morph.update" },
                { "damage.commit", "GraphCommitGameplayDamage/damage.commit" },
                { "early.damage.commit", "GraphCommitEarlyDamage/early.damage.commit" },
                { "early.resource.commit", "GraphCommitEarlyResources/early.resource.commit" },
                { "effect.commit", "GraphCommitQueuedEffects/effect.commit" },
                { "effect.tick", "GraphTickConfiguredEffect/effect.tick" },
                { "effect.tick.combat", "GraphTickSupplementalEffect(clock=Combat)/effect.tick.combat" },
                { "effect.tick.enemy", "GraphTickSupplementalEffect(clock=Enemy)/effect.tick.enemy" },
                { "effect.tick.global", "GraphTickSupplementalEffect(clock=Global)/effect.tick.global" },
                { "effect.tick.real", "GraphTickSupplementalEffect(clock=RealTime)/effect.tick.real" },
                { "frame.begin", "GraphBeginFrame/frame.begin" },
                { "frame.input.publish", "GraphPublishPersistentFrameState/frame.input.publish" },
                { "frame.blinker.update", "GraphTickBlinker/frame.blinker.update" },
                { "frame.invulnerability.update", "GraphDecrementInvulnerability/frame.invulnerability.update" },
                { "frame.phaser.update", "GraphTickPhaser/frame.phaser.update" },
                { "frame.time.freeze", "GraphFreezeTime/frame.time.freeze" },
                { "gameplay-event.commit", "GraphCommitGameplayEvents/gameplay-event.commit" },
                { "movement.deployable-trap.update", "Update()/movement.deployable-trap.update" },
                { "movement.enemy-healer.prepare", "SetTurn(turn)/movement.enemy-healer.prepare" },
                { "movement.enemy-healer.update", "Update(delta)/movement.enemy-healer.update" },
                { "movement.enemy.prepare", "SetTurn(turn)/movement.enemy.prepare" },
                { "movement.enemy.update", "EnemyMovementSystem.Update+owner-local movement/trail buffers+stable commit/movement.enemy.update" },
                { "movement.path-block.update", "Update()/movement.path-block.update" },
                { "movement.path-modifier.prepare", "SetTurn()/movement.path-modifier.prepare" },
                { "movement.path-modifier.update", "Update(delta)/movement.path-modifier.update" },
                { "movement.pathfinding.prepare", "SetTurn(turn)/movement.pathfinding.prepare" },
                { "movement.presentation.commit", "GraphEmitPositions/movement.presentation.commit" },
                { "movement.pull.prepare", "SetTurn(turn)/movement.pull.prepare" },
                { "movement.pull.update", "Update(delta)/movement.pull.update" },
                { "movement.steal-gold.update", "Update()/movement.steal-gold.update" },
                { "movement.summon.prepare", "SetTurn(turn)/movement.summon.prepare" },
                { "movement.summon.update", "Update(delta)/movement.summon.update" },
                { "movement.wound.prepare", "SetTurn(turn)/movement.wound.prepare" },
                { "movement.wound.update", "Update()/movement.wound.update" },
                { "non-wave.ability.reject", "GraphRejectNonWaveAbilities/non-wave.ability.reject" },
                { "non-wave.damage.reject", "GraphRejectNonWaveDamage/non-wave.damage.reject" },
                { "non-wave.frame.close", "GraphCloseDeferredResolvers/non-wave.frame.close" },
                { "post-death.combo.update", "Update(delta)/post-death.combo.update" },
                { "post-death.corpse.update", "Update(delta)/post-death.corpse.update" },
                { "post-death.doom-clock.update", "Update(delta,GameState.WavePhase)/post-death.doom-clock.update" },
                { "post-death.effect.commit", "GraphCommitQueuedEffects/post-death.effect.commit" },
                { "post-death.fission.update", "Update()/post-death.fission.update" },
                { "post-death.gameplay-event.commit", "GraphCommitPostDeathGameplayEvents/post-death.gameplay-event.commit" },
                { "post-death.life-link.resolve", "ResolveBreakPenalties()/post-death.life-link.resolve" },
                { "post-death.objective.update", "Update(delta,GameState.WavePhase)/post-death.objective.update" },
                { "post-death.resource-node.update", "Update(delta,GameState.WavePhase)/post-death.resource-node.update" },
                { "post-death.soul-harvest.update", "Update(delta)/post-death.soul-harvest.update" },
                { "post-death.tower-income.update", "Update(delta)/post-death.tower-income.update" },
                { "pregame.adaptive-difficulty.update", "Update(delta)/pregame.adaptive-difficulty.update" },
                { "pregame.construction.update", "Update(delta)/pregame.construction.update" },
                { "pregame.day-night.update", "Update(delta)/pregame.day-night.update" },
                { "pregame.desperation.update", "Update()/pregame.desperation.update" },
                { "pregame.random-event.update", "Update(delta,s.GraphCurrentWave,s.GraphCurrentLevel)/pregame.random-event.update" },
                { "pregame.random-event.callback-dispatch", "DispatchPendingCallbacks()/pregame.random-event.callback-dispatch" },
                { "pregame.time-rewind.update", "Update(delta)/pregame.time-rewind.update" },
                { "pregame.wave.read-current-level", "s.GraphCurrentLevel=x.GetCurrentLevel()/pregame.wave.read-current-level" },
                { "pregame.wave.read-current-wave", "s.GraphCurrentWave=x.GetCurrentWave()/pregame.wave.read-current-wave" },
                { "pregame.weather.update", "Update(delta)/pregame.weather.update" },
                { "primary-death.callback-dispatch", "GraphDispatchDeathCallbacks/primary-death.callback-dispatch" },
                { "primary-death.resolve", "GraphPrepareDeaths/primary-death.resolve" },
                { "resource.commit", "GraphCommitGameplayResources/resource.commit" },
                { "skill-buff.bleed.resolve", "ResolveBleedDamage()/skill-buff.bleed.resolve" },
                { "skill-buff.bleed.update", "BleedSystem.Update+active-index-owned BleedPrepared buffer/skill-buff.bleed.update" },
                { "skill-buff.buff.resolve-dot", "ResolveDotDamage()/skill-buff.buff.resolve-dot" },
                { "skill-buff.buff.update", "Update(delta)/skill-buff.buff.update" },
                { "skill-buff.death-mark.update", "Update(delta)/skill-buff.death-mark.update" },
                { "skill-buff.elemental.resolve", "ResolveReactionDamage()/skill-buff.elemental.resolve" },
                { "skill-buff.elemental.update", "Update(delta)/skill-buff.elemental.update" },
                { "skill-buff.frostbite.resolve", "ResolveFrostbiteDamage()/skill-buff.frostbite.resolve" },
                { "skill-buff.frostbite.update", "Update(delta)/skill-buff.frostbite.update" },
                { "skill-buff.heal-aura.prepare", "SetTurn()/skill-buff.heal-aura.prepare" },
                { "skill-buff.heal-aura.update", "Update(delta)/skill-buff.heal-aura.update" },
                { "skill-buff.healing-zone.update", "Update(delta)/skill-buff.healing-zone.update" },
                { "skill-buff.mark.update", "Update(delta)/skill-buff.mark.update" },
                { "skill-buff.rally.update", "Update(delta)/skill-buff.rally.update" },
                { "skill-buff.skill.resolve-damage", "ResolveSkillDamage()/skill-buff.skill.resolve-damage" },
                { "skill-buff.thorns-aura.prepare", "SetTurn()/skill-buff.thorns-aura.prepare" },
                { "skill-buff.thorns-aura.update", "Update(delta,g.ThornsAuraPlayerId)/skill-buff.thorns-aura.update" },
                { "skill-buff.wisp.update", "Update(delta)/skill-buff.wisp.update" },
                { "spatial.chrono.prepare", "SetTurn()/spatial.chrono.prepare" },
                { "spatial.chrono.update", "Update()/spatial.chrono.update" },
                { "spatial.fog.prepare", "SetTurn()/spatial.fog.prepare" },
                { "spatial.fog.update", "Update()/spatial.fog.update" },
                { "spatial.index.rebuild", "GraphRebuildSpatialIndex/spatial.index.rebuild" },
                { "spatial.mine.prepare", "SetTurn(turn)/spatial.mine.prepare" },
                { "spatial.mine.update", "Update(delta)/spatial.mine.update" },
                { "spatial.patrol.prepare", "SetTurn(turn)/spatial.patrol.prepare" },
                { "spatial.patrol.update", "Update(delta)/spatial.patrol.update" },
                { "spatial.point-defense.prepare", "SetTurn(turn)/spatial.point-defense.prepare" },
                { "spatial.point-defense.update", "Update(delta)/spatial.point-defense.update" },
                { "spatial.telegraph.update", "Update(delta)/spatial.telegraph.update" },
                { "spawning.nest.prepare", "SetTurn(turn)/spawning.nest.prepare" },
                { "spawning.nest.update", "Update(delta)/spawning.nest.update" },
                { "spawning.wave.update", "FrameScheduler.GraphUpdateWaveSpawning(WaveSpawningSystem)/spawning.wave.update" },
                { "spawning.wave.callback-dispatch", "DispatchPendingCallbacks()/spawning.wave.callback-dispatch" },
                { "terrain.enemy-morph.update", "Update(delta)/terrain.enemy-morph.update" },
                { "terrain.prepare", "SetTurn()/terrain.prepare" },
                { "terrain.update", "Update(delta)/terrain.update" },
                { "terrain.wave-mutator.prepare", "SetTurn(turn)/terrain.wave-mutator.prepare" },
                { "terrain.wave-mutator.update", "Update(delta)/terrain.wave-mutator.update" },
                { "threat.aggregate", "GraphAggregateThreat/threat.aggregate" },
                { "wave.frame.close", "GraphCloseDeferredResolvers/wave.frame.close" },
            };

        public static string Require(string nodeId)
        {
            if (!Semantics.TryGetValue(nodeId, out string? semantic))
                throw new FrameGraphValidationException($"Frame node '{nodeId}' has no reviewed binding semantic id.");
            return semantic;
        }

        public static string ReviewClosure(string nodeId) => nodeId switch
        {
            "ability.commit" => "FrameScheduler.GraphCommitQueuedAbilities+GameplayAbilityRuntime.CommitQueuedAbilities",
            "build.ability.commit" => "FrameScheduler.GraphCommitQueuedAbilities+GameplayAbilityRuntime.CommitQueuedAbilities",
            "effect.commit" => "FrameScheduler.GraphCommitQueuedEffects+GameplayEffectRuntime.CommitQueuedEffects",
            "post-death.effect.commit" => "FrameScheduler.GraphCommitQueuedEffects+GameplayEffectRuntime.CommitQueuedEffects",
            "frame.begin" => "FrameScheduler.GraphBeginFrame+ComponentStore.BeginFrame+ComponentStore.SetTurnCCFlags",
            "frame.input.publish" => "FrameScheduler.GraphPublishPersistentFrameState+Thread.MemoryBarrier+FrameSystemGraph.PersistentFrameState",
            "frame.blinker.update" => "FrameScheduler.GraphTickBlinker+PathfindingSystem.GetPathWaypointCount",
            "attribute.aggregate" => "FrameScheduler.GraphAggregateAttributes+ComponentStore.SyncComputedAttributeBases+AttributeAggregator.AggregateDirty",
            "pregame.random-event.update" => "RandomEventSystem.Update+RandomEventSystem.ApplyEvent+RandomEventSystem.QueueCallback",
            "pregame.random-event.callback-dispatch" => "RandomEventSystem.DispatchPendingCallbacks+ordered fixed callback batch+OnEventTriggered+OnEventEnded",
            "spawning.wave.update" => "FrameScheduler.GraphUpdateWaveSpawning+FrameScheduler.ScenarioKind+WaveSpawningSystem.SetLevel+WaveSpawningSystem.Update+WaveSpawningSystem.QueueWaveStart(single owner)",
            "ai.enemy.update" => "EnemyAISystem.Update+EnemyAISystem.TryCollectExpiredDecoy+EnemyAISystem.MergeCollectBuffers+stable death queue drain+EnemyAISystem.ApplyAttackEvents+EnemyAISystem.ApplyLifestealEvents+ComponentStore.ApplyPlayerDamageAuthority",
            "ai.burrow.update" => "EnemyBurrowSystem.Update+active-index-owned emerge buffer",
            "ai.enemy-ability.cast-timers" => "EnemyAbilitySystem.TickCastTimers+turn-step channel timer",
            "ai.fear.update" => "FearSystem.Update+turn-step fear duration+parallel disjoint enemy slots",
            "ai.burrow.apply" => "EnemyBurrowSystem.ApplyBurrowEffects+stable active-index drain",
            "ai.life-link.update" => "EnemyLifeLinkSystem.Update+active-index-owned link candidates+stable serial establishment",
            "combat.suicide-bomb.update" => "SuicideBombSystem.Update+active-index-owned explosion buffer+stable serial drain+deterministic retaliate roll",
            "combat.hero.update" => "HeroSystem.Update+hero-slot-owned candidate and attack buffers+stable hero-slot drain",
            "skill-buff.bleed.update" => "BleedSystem.Update+active-index-owned bleed buffer",
            "skill-buff.bleed.resolve" => "BleedSystem.ResolveBleedDamage+stable active-index drain",
            "skill-buff.frostbite.update" => "FrostbiteSystem.Update+active-index-owned frostbite buffer",
            "skill-buff.frostbite.resolve" => "FrostbiteSystem.ResolveFrostbiteDamage+stable active-index drain",
            "spawning.wave.callback-dispatch" => "WaveSpawningSystem.DispatchPendingCallbacks+AdaptiveDifficultySystem.OnWaveComplete+OnBreatherWaveComplete+OnWaveComplete+OnWaveStart+IBattleEventBus.OnWaveStarted",
            "movement.enemy.update" => "EnemyMovementSystem.Update+EnemyMovementSystem.CollectRange+EnemyMovementSystem.CommitPalisadeContacts+BossTrailAoeSystem.BeginCollect+BossTrailAoeSystem.TryQueueTrail(active-index)+BossTrailAoeSystem.ResolveTrailEvents(stable active-index)",
            "ai.mana-burn.update" => "ManaBurnSystem.Update+active-index-owned events+previous/current span clear+stable serial drain",
            "combat.tower-attack.update" => "TowerAttackSystem.Update+owner-local DodgeFacts+TowerAttackSystem.DrainTowerCollectBuffers+serial HitShield consume+serial RecentDamage merge+serial ElementStatus/Timer commit+serial VanguardTransfer resolve+TowerAttackSystem.CommitOnHitState",
            "combat.rally.consume" => "FrameScheduler.GraphConsumeRally+SkillBuffGroup.ConsumeRally+RallySystem.ConsumePlayerDamageFacts+RallySystem.ApplyActiveBonuses",
            "ai.enemy-strafe.update" => "EnemyStrafeSystem.Update+deterministic roll+serial position/cooldown commit",
            "spatial.telegraph.update" => "TelegraphSystem.Update+EventBus.PlayerDamaged",
            "non-wave.ability.reject" => "FrameScheduler.GraphRejectNonWaveAbilities+SkillSystem.RejectPendingSkillDamage+GlobalSkillSystem.RejectPendingActivation",
            "non-wave.damage.reject" => "FrameScheduler.GraphRejectNonWaveDamage+DamageResolver.RejectPending+ResourceResolver.RejectPendingEnemyDamage",
            "non-wave.frame.close" or "wave.frame.close" => "FrameScheduler.GraphCloseDeferredResolvers+DamageResolver.EnableDeferred+ResourceResolver.EnableDeferred",
            "skill-buff.elemental.update" => "ElementalReactionSystem.Update+ElementalReactionSystem.OnShieldBroken+ElementalReactionSystem.ApplyElement",
            "skill-buff.healing-zone.update" => "HealingZoneSystem.Update+ApplyHealingTick+HealPlayer+HealSummonedUnit+ResolveSkillDamage",
            "skill-buff.rally.update" => "RallySystem.Update+RallySystem.ConsumePlayerDamageFacts+RallySystem.ApplyRallyBonusesForPlayer",
            "skill-buff.wisp.update" => "WispSystem.Update+ApplyHealAura+ApplySlowAura+ClearStaleSlowForPlayer+ApplyCurseAura",
            "primary-death.callback-dispatch" or "cascade-death.callback-dispatch" =>
                "FrameScheduler.GraphDispatchDeathCallbacks+ComponentStore.DispatchPreparedEnemyDeaths+OnEnemyKilled+OnTowerKill+IBattleEventBus.EntityKilled",
            _ => Require(nodeId)
        };
    }
}
