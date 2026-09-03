#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace BattleSystemECS.Core
{
    internal static class FrameAccessReviewCatalog
    {
        internal const string EarlyArtifactSha256 = "F93D8AC736B346C93956E461985DF390EDA01DCD5D8D308712F4325D15F91289";
        internal const string CombatArtifactSha256 = "2F0DD14D21DB340B4E5A465A2B2E22FB1D272A158DCF9146CF239A0F16F49BBC";
        internal const string CommitArtifactSha256 = "3E38DB03DF92438283631D554E7D61B14B4624C129E67876522CB62D6D8421C1";
        internal const string SupplementalArtifactSha256 = "EA9FA8D9563DD0AEE189B6873573659C8C9CB3EE6E057873EDBD0932754A11FE";
        internal static int ReviewedNodeCount => Reviews.Count;
        internal const int ReportedEarlyNodeCount = 91;
        internal const int ReportedCombatNodeCount = 84;
        internal const int ReportedPostDeathNodeCount = 49;
        internal const int ReportedEarlyCombatOverlap = 0;
        internal const int ReportedEarlyPostDeathOverlap = 12;
        internal const int ReportedCombatPostDeathOverlap = 4;
        internal const int ReportedTripleOverlap = 0;
        internal static int ReportedUnionNodeCount => ReportedEarlyNodeCount+ReportedCombatNodeCount+
            ReportedPostDeathNodeCount-ReportedEarlyCombatOverlap-ReportedEarlyPostDeathOverlap-
            ReportedCombatPostDeathOverlap+ReportedTripleOverlap;
        internal static IReadOnlyList<string> SupplementalNodeIds { get; } = Array.AsReadOnly(new[]
        {
            "non-wave.ability.reject", "non-wave.damage.reject", "non-wave.frame.close", "wave.frame.close",
            "skill-buff.elemental.update", "skill-buff.elemental.resolve", "skill-buff.heal-aura.prepare",
            "skill-buff.heal-aura.update", "skill-buff.healing-zone.update", "skill-buff.mark.update",
            "skill-buff.rally.update", "skill-buff.thorns-aura.prepare", "skill-buff.thorns-aura.update",
            "skill-buff.wisp.update", "primary-death.callback-dispatch", "cascade-death.callback-dispatch",
            "pregame.random-event.callback-dispatch", "spawning.wave.callback-dispatch", "frame.input.publish"
        });
        private static readonly Dictionary<string, FrameAccessReviewId> Reviews =
            new Dictionary<string, FrameAccessReviewId>(StringComparer.Ordinal)
            {
                { "skill-buff.skill.update", "FG-ACCESS-20260901/skill-buff.skill.update" },
                { "ai.burrow.apply", "FG-ACCESS-20260901/ai.burrow.apply" },
                { "ai.burrow.prepare", "FG-ACCESS-20260901/ai.burrow.prepare" },
                { "ai.burrow.update", "FG-ACCESS-20260901/ai.burrow.update" },
                { "ai.enemy-ability.cast-timers", "FG-ACCESS-20260901/ai.enemy-ability.cast-timers" },
                { "ai.enemy-ability.cooldowns", "FG-ACCESS-20260901/ai.enemy-ability.cooldowns" },
                { "ai.enemy-ability.execute", "FG-ACCESS-20260901/ai.enemy-ability.execute" },
                { "ai.enemy-ability.prepare", "FG-ACCESS-20260901/ai.enemy-ability.prepare" },
                { "ai.enemy-ability.update", "FG-ACCESS-20260901/ai.enemy-ability.update" },
                { "ai.enemy-affix.update", "FG-ACCESS-20260901/ai.enemy-affix.update" },
                { "ai.enemy-strafe.prepare", "FG-ACCESS-20260901/ai.enemy-strafe.prepare" },
                { "ai.enemy-strafe.update", "FG-ACCESS-20260901/ai.enemy-strafe.update" },
                { "ai.enemy.prepare", "FG-ACCESS-20260901/ai.enemy.prepare" },
                { "ai.enemy.update", "FG-ACCESS-20260901/ai.enemy.update" },
                { "ai.fear.prepare", "FG-ACCESS-20260901/ai.fear.prepare" },
                { "ai.fear.update", "FG-ACCESS-20260901/ai.fear.update" },
                { "ai.life-link.cooldowns", "FG-ACCESS-20260901/ai.life-link.cooldowns" },
                { "ai.life-link.prepare", "FG-ACCESS-20260901/ai.life-link.prepare" },
                { "ai.life-link.update", "FG-ACCESS-20260901/ai.life-link.update" },
                { "ai.lifesteal.prepare", "FG-ACCESS-20260901/ai.lifesteal.prepare" },
                { "ai.lifesteal.update", "FG-ACCESS-20260901/ai.lifesteal.update" },
                { "ai.magnetize.update", "FG-ACCESS-20260901/ai.magnetize.update" },
                { "ai.mana-burn.prepare", "FG-ACCESS-20260901/ai.mana-burn.prepare" },
                { "ai.mana-burn.update", "FG-ACCESS-20260901/ai.mana-burn.update" },
                { "ai.necromancer.prepare", "FG-ACCESS-20260901/ai.necromancer.prepare" },
                { "ai.necromancer.update", "FG-ACCESS-20260901/ai.necromancer.update" },
                { "ai.phase.prepare", "FG-ACCESS-20260901/ai.phase.prepare" },
                { "ai.phase.update", "FG-ACCESS-20260901/ai.phase.update" },
                { "ai.sapper.prepare", "FG-ACCESS-20260901/ai.sapper.prepare" },
                { "ai.sapper.recompute", "FG-ACCESS-20260901/ai.sapper.recompute" },
                { "ai.sapper.update", "FG-ACCESS-20260901/ai.sapper.update" },
                { "ai.zone-control.update", "FG-ACCESS-20260901/ai.zone-control.update" },
                { "attribute.aggregate", "FG-ACCESS-20260901/attribute.aggregate" },
                { "build.ability.reject", "FG-ACCESS-20260901/build.ability.reject" },
                { "build.auto-skill.update", "FG-ACCESS-20260901/build.auto-skill.update" },
                { "build.damage.commit", "FG-ACCESS-20260901/build.damage.commit" },
                { "build.desperation.update", "FG-ACCESS-20260901/build.desperation.update" },
                { "build.effect.tick", "FG-ACCESS-20260901/build.effect.tick" },
                { "build.effect.tick.global", "FG-ACCESS-20260901/build.effect.tick.global" },
                { "build.effect.tick.real", "FG-ACCESS-20260901/build.effect.tick.real" },
                { "build.frame.close", "FG-ACCESS-20260901/build.frame.close" },
                { "build.gameplay-event.commit", "FG-ACCESS-20260901/build.gameplay-event.commit" },
                { "build.global-skill.update", "FG-ACCESS-20260901/build.global-skill.update" },
                { "build.gold.update", "FG-ACCESS-20260901/build.gold.update" },
                { "build.interest.update", "FG-ACCESS-20260901/build.interest.update" },
                { "build.mana-shield.update", "FG-ACCESS-20260901/build.mana-shield.update" },
                { "build.mana.update", "FG-ACCESS-20260901/build.mana.update" },
                { "build.objective.update", "FG-ACCESS-20260901/build.objective.update" },
                { "build.pre-fight-buff.update", "FG-ACCESS-20260901/build.pre-fight-buff.update" },
                { "build.resource-node.update", "FG-ACCESS-20260901/build.resource-node.update" },
                { "build.resource.commit", "FG-ACCESS-20260901/build.resource.commit" },
                { "build.shop-reroll.update", "FG-ACCESS-20260901/build.shop-reroll.update" },
                { "build.skill.reject-pending", "FG-ACCESS-20260901/build.skill.reject-pending" },
                { "build.skill.update", "FG-ACCESS-20260901/build.skill.update" },
                { "build.tower-income.update", "FG-ACCESS-20260901/build.tower-income.update" },
                { "build.tower-relocate.update", "FG-ACCESS-20260901/build.tower-relocate.update" },
                { "build.upgrade.update", "FG-ACCESS-20260901/build.upgrade.update" },
                { "cascade-death.callback-dispatch", "FG-ACCESS-20260901/cascade-death.callback-dispatch" },
                { "cascade-death.resolve", "FG-ACCESS-20260901/cascade-death.resolve" },
                { "cascade.damage.commit", "FG-ACCESS-20260901/cascade.damage.commit" },
                { "cascade.resource.commit", "FG-ACCESS-20260901/cascade.resource.commit" },
                { "combat-setup.aura.prepare", "FG-ACCESS-20260901/combat-setup.aura.prepare" },
                { "combat-setup.curse.prepare", "FG-ACCESS-20260901/combat-setup.curse.prepare" },
                { "combat-setup.fortress.prepare", "FG-ACCESS-20260901/combat-setup.fortress.prepare" },
                { "combat-setup.frost-zone.prepare", "FG-ACCESS-20260901/combat-setup.frost-zone.prepare" },
                { "combat-setup.frost-zone.update", "FG-ACCESS-20260901/combat-setup.frost-zone.update" },
                { "combat-setup.global-skill.prepare", "FG-ACCESS-20260901/combat-setup.global-skill.prepare" },
                { "combat-setup.heat.prepare", "FG-ACCESS-20260901/combat-setup.heat.prepare" },
                { "combat-setup.hero.prepare", "FG-ACCESS-20260901/combat-setup.hero.prepare" },
                { "combat-setup.hit-shield.prepare", "FG-ACCESS-20260901/combat-setup.hit-shield.prepare" },
                { "combat-setup.hot-zone.prepare", "FG-ACCESS-20260901/combat-setup.hot-zone.prepare" },
                { "combat-setup.link.prepare", "FG-ACCESS-20260901/combat-setup.link.prepare" },
                { "combat-setup.mana.prepare", "FG-ACCESS-20260901/combat-setup.mana.prepare" },
                { "combat-setup.overcharge.prepare", "FG-ACCESS-20260901/combat-setup.overcharge.prepare" },
                { "combat-setup.player-attack.prepare", "FG-ACCESS-20260901/combat-setup.player-attack.prepare" },
                { "combat-setup.pull-tower.prepare", "FG-ACCESS-20260901/combat-setup.pull-tower.prepare" },
                { "combat-setup.skill.prepare", "FG-ACCESS-20260901/combat-setup.skill.prepare" },
                { "combat-setup.synergy.prepare", "FG-ACCESS-20260901/combat-setup.synergy.prepare" },
                { "combat-setup.taunt.prepare", "FG-ACCESS-20260901/combat-setup.taunt.prepare" },
                { "combat-setup.terrain-zone.prepare", "FG-ACCESS-20260901/combat-setup.terrain-zone.prepare" },
                { "combat-setup.terrain-zone.update", "FG-ACCESS-20260901/combat-setup.terrain-zone.update" },
                { "combat-setup.tower-attack.prepare", "FG-ACCESS-20260901/combat-setup.tower-attack.prepare" },
                { "combat-setup.wander.prepare", "FG-ACCESS-20260901/combat-setup.wander.prepare" },
                { "combat-setup.wander.update", "FG-ACCESS-20260901/combat-setup.wander.update" },
                { "combat.adrenaline.update", "FG-ACCESS-20260901/combat.adrenaline.update" },
                { "combat.aggro.update", "FG-ACCESS-20260901/combat.aggro.update" },
                { "combat.aura.resolve", "FG-ACCESS-20260901/combat.aura.resolve" },
                { "combat.beacon.prepare", "FG-ACCESS-20260901/combat.beacon.prepare" },
                { "combat.beacon.resolve", "FG-ACCESS-20260901/combat.beacon.resolve" },
                { "combat.beam.update", "FG-ACCESS-20260901/combat.beam.update" },
                { "combat.bloodlust.update", "FG-ACCESS-20260901/combat.bloodlust.update" },
                { "combat.crest.update", "FG-ACCESS-20260901/combat.crest.update" },
                { "combat.culling.update", "FG-ACCESS-20260901/combat.culling.update" },
                { "combat.curse.resolve", "FG-ACCESS-20260901/combat.curse.resolve" },
                { "combat.demolish.update", "FG-ACCESS-20260901/combat.demolish.update" },
                { "combat.dispel.update", "FG-ACCESS-20260901/combat.dispel.update" },
                { "combat.echo-clone.update", "FG-ACCESS-20260901/combat.echo-clone.update" },
                { "combat.enemy-projectile.update", "FG-ACCESS-20260901/combat.enemy-projectile.update" },
                { "combat.energy.update", "FG-ACCESS-20260901/combat.energy.update" },
                { "combat.global-skill.update", "FG-ACCESS-20260901/combat.global-skill.update" },
                { "combat.heat.update", "FG-ACCESS-20260901/combat.heat.update" },
                { "combat.hero-skill.update", "FG-ACCESS-20260901/combat.hero-skill.update" },
                { "combat.hero.update", "FG-ACCESS-20260901/combat.hero.update" },
                { "combat.hit-shield.update", "FG-ACCESS-20260901/combat.hit-shield.update" },
                { "combat.link.update", "FG-ACCESS-20260901/combat.link.update" },
                { "combat.mana-shield.update", "FG-ACCESS-20260901/combat.mana-shield.update" },
                { "combat.mana.update", "FG-ACCESS-20260901/combat.mana.update" },
                { "combat.momentum.update", "FG-ACCESS-20260901/combat.momentum.update" },
                { "combat.overcharge.update", "FG-ACCESS-20260901/combat.overcharge.update" },
                { "combat.pickup.update", "FG-ACCESS-20260901/combat.pickup.update" },
                { "combat.player-attack.update", "FG-ACCESS-20260901/combat.player-attack.update" },
                { "combat.projectile.update", "FG-ACCESS-20260901/combat.projectile.update" },
                { "combat.pull-tower.update", "FG-ACCESS-20260901/combat.pull-tower.update" },
                { "combat.rally.consume", "FG-ACCESS-20260903/combat.rally.consume" },
                { "combat.reflect.apply", "FG-ACCESS-20260901/combat.reflect.apply" },
                { "combat.reflect.resolve", "FG-ACCESS-20260901/combat.reflect.resolve" },
                { "combat.sabotage.update", "FG-ACCESS-20260901/combat.sabotage.update" },
                { "combat.shrine.prepare", "FG-ACCESS-20260901/combat.shrine.prepare" },
                { "combat.shrine.resolve", "FG-ACCESS-20260901/combat.shrine.resolve" },
                { "combat.silence.update", "FG-ACCESS-20260901/combat.silence.update" },
                { "combat.stealth.update", "FG-ACCESS-20260901/combat.stealth.update" },
                { "combat.suicide-bomb.update", "FG-ACCESS-20260901/combat.suicide-bomb.update" },
                { "combat.synergy.resolve-buff-shares", "FG-ACCESS-20260901/combat.synergy.resolve-buff-shares" },
                { "combat.synergy.update", "FG-ACCESS-20260901/combat.synergy.update" },
                { "combat.taunt.resolve", "FG-ACCESS-20260901/combat.taunt.resolve" },
                { "combat.tower-active-skill.update", "FG-ACCESS-20260901/combat.tower-active-skill.update" },
                { "combat.tower-attack.update", "FG-ACCESS-20260901/combat.tower-attack.update" },
                { "combat.tower-morph.update", "FG-ACCESS-20260901/combat.tower-morph.update" },
                { "damage.commit", "FG-ACCESS-20260901/damage.commit" },
                { "early.damage.commit", "FG-ACCESS-20260901/early.damage.commit" },
                { "early.resource.commit", "FG-ACCESS-20260901/early.resource.commit" },
                { "effect.tick", "FG-ACCESS-20260901/effect.tick" },
                { "effect.tick.combat", "FG-ACCESS-20260901/effect.tick.combat" },
                { "effect.tick.enemy", "FG-ACCESS-20260901/effect.tick.enemy" },
                { "effect.tick.global", "FG-ACCESS-20260901/effect.tick.global" },
                { "effect.tick.real", "FG-ACCESS-20260901/effect.tick.real" },
                { "frame.begin", "FG-ACCESS-20260901/frame.begin" },
                { "frame.input.publish", "FG-ACCESS-20260901/frame.input.publish" },
                { "frame.blinker.update", "FG-ACCESS-20260901/frame.blinker.update" },
                { "frame.invulnerability.update", "FG-ACCESS-20260901/frame.invulnerability.update" },
                { "frame.phaser.update", "FG-ACCESS-20260901/frame.phaser.update" },
                { "frame.time.freeze", "FG-ACCESS-20260901/frame.time.freeze" },
                { "gameplay-event.commit", "FG-ACCESS-20260901/gameplay-event.commit" },
                { "movement.deployable-trap.update", "FG-ACCESS-20260901/movement.deployable-trap.update" },
                { "movement.enemy-healer.prepare", "FG-ACCESS-20260901/movement.enemy-healer.prepare" },
                { "movement.enemy-healer.update", "FG-ACCESS-20260901/movement.enemy-healer.update" },
                { "movement.enemy.prepare", "FG-ACCESS-20260901/movement.enemy.prepare" },
                { "movement.enemy.update", "FG-ACCESS-20260901/movement.enemy.update" },
                { "movement.path-block.update", "FG-ACCESS-20260901/movement.path-block.update" },
                { "movement.path-modifier.prepare", "FG-ACCESS-20260901/movement.path-modifier.prepare" },
                { "movement.path-modifier.update", "FG-ACCESS-20260901/movement.path-modifier.update" },
                { "movement.pathfinding.prepare", "FG-ACCESS-20260901/movement.pathfinding.prepare" },
                { "movement.presentation.commit", "FG-ACCESS-20260901/movement.presentation.commit" },
                { "movement.pull.prepare", "FG-ACCESS-20260901/movement.pull.prepare" },
                { "movement.pull.update", "FG-ACCESS-20260901/movement.pull.update" },
                { "movement.steal-gold.update", "FG-ACCESS-20260901/movement.steal-gold.update" },
                { "movement.summon.prepare", "FG-ACCESS-20260901/movement.summon.prepare" },
                { "movement.summon.update", "FG-ACCESS-20260901/movement.summon.update" },
                { "movement.wound.prepare", "FG-ACCESS-20260901/movement.wound.prepare" },
                { "movement.wound.update", "FG-ACCESS-20260901/movement.wound.update" },
                { "non-wave.ability.reject", "FG-ACCESS-20260901/non-wave.ability.reject" },
                { "non-wave.damage.reject", "FG-ACCESS-20260901/non-wave.damage.reject" },
                { "non-wave.frame.close", "FG-ACCESS-20260901/non-wave.frame.close" },
                { "post-death.combo.update", "FG-ACCESS-20260901/post-death.combo.update" },
                { "post-death.corpse.update", "FG-ACCESS-20260901/post-death.corpse.update" },
                { "post-death.doom-clock.update", "FG-ACCESS-20260901/post-death.doom-clock.update" },
                { "post-death.fission.update", "FG-ACCESS-20260901/post-death.fission.update" },
                { "post-death.gameplay-event.commit", "FG-ACCESS-20260901/post-death.gameplay-event.commit" },
                { "post-death.life-link.resolve", "FG-ACCESS-20260901/post-death.life-link.resolve" },
                { "post-death.objective.update", "FG-ACCESS-20260901/post-death.objective.update" },
                { "post-death.resource-node.update", "FG-ACCESS-20260901/post-death.resource-node.update" },
                { "post-death.soul-harvest.update", "FG-ACCESS-20260901/post-death.soul-harvest.update" },
                { "post-death.tower-income.update", "FG-ACCESS-20260901/post-death.tower-income.update" },
                { "pregame.adaptive-difficulty.update", "FG-ACCESS-20260901/pregame.adaptive-difficulty.update" },
                { "pregame.construction.update", "FG-ACCESS-20260901/pregame.construction.update" },
                { "pregame.day-night.update", "FG-ACCESS-20260901/pregame.day-night.update" },
                { "pregame.desperation.update", "FG-ACCESS-20260901/pregame.desperation.update" },
                { "pregame.random-event.update", "FG-ACCESS-20260901/pregame.random-event.update" },
                { "pregame.random-event.callback-dispatch", "FG-ACCESS-20260901/pregame.random-event.callback-dispatch" },
                { "pregame.time-rewind.update", "FG-ACCESS-20260901/pregame.time-rewind.update" },
                { "pregame.wave.read-current-level", "FG-ACCESS-20260901/pregame.wave.read-current-level" },
                { "pregame.wave.read-current-wave", "FG-ACCESS-20260901/pregame.wave.read-current-wave" },
                { "pregame.weather.update", "FG-ACCESS-20260901/pregame.weather.update" },
                { "primary-death.callback-dispatch", "FG-ACCESS-20260901/primary-death.callback-dispatch" },
                { "primary-death.resolve", "FG-ACCESS-20260901/primary-death.resolve" },
                { "resource.commit", "FG-ACCESS-20260901/resource.commit" },
                { "skill-buff.bleed.resolve", "FG-ACCESS-20260901/skill-buff.bleed.resolve" },
                { "skill-buff.bleed.update", "FG-ACCESS-20260901/skill-buff.bleed.update" },
                { "skill-buff.buff.resolve-dot", "FG-ACCESS-20260901/skill-buff.buff.resolve-dot" },
                { "skill-buff.buff.update", "FG-ACCESS-20260901/skill-buff.buff.update" },
                { "skill-buff.death-mark.update", "FG-ACCESS-20260901/skill-buff.death-mark.update" },
                { "skill-buff.elemental.resolve", "FG-ACCESS-20260901/skill-buff.elemental.resolve" },
                { "skill-buff.elemental.update", "FG-ACCESS-20260901/skill-buff.elemental.update" },
                { "skill-buff.frostbite.resolve", "FG-ACCESS-20260901/skill-buff.frostbite.resolve" },
                { "skill-buff.frostbite.update", "FG-ACCESS-20260901/skill-buff.frostbite.update" },
                { "skill-buff.heal-aura.prepare", "FG-ACCESS-20260901/skill-buff.heal-aura.prepare" },
                { "skill-buff.heal-aura.update", "FG-ACCESS-20260901/skill-buff.heal-aura.update" },
                { "skill-buff.healing-zone.update", "FG-ACCESS-20260901/skill-buff.healing-zone.update" },
                { "skill-buff.mark.update", "FG-ACCESS-20260901/skill-buff.mark.update" },
                { "skill-buff.rally.update", "FG-ACCESS-20260901/skill-buff.rally.update" },
                { "skill-buff.skill.resolve-damage", "FG-ACCESS-20260901/skill-buff.skill.resolve-damage" },
                { "skill-buff.thorns-aura.prepare", "FG-ACCESS-20260901/skill-buff.thorns-aura.prepare" },
                { "skill-buff.thorns-aura.update", "FG-ACCESS-20260901/skill-buff.thorns-aura.update" },
                { "skill-buff.wisp.update", "FG-ACCESS-20260901/skill-buff.wisp.update" },
                { "spatial.chrono.prepare", "FG-ACCESS-20260901/spatial.chrono.prepare" },
                { "spatial.chrono.update", "FG-ACCESS-20260901/spatial.chrono.update" },
                { "spatial.fog.prepare", "FG-ACCESS-20260901/spatial.fog.prepare" },
                { "spatial.fog.update", "FG-ACCESS-20260901/spatial.fog.update" },
                { "spatial.index.rebuild", "FG-ACCESS-20260901/spatial.index.rebuild" },
                { "spatial.mine.prepare", "FG-ACCESS-20260901/spatial.mine.prepare" },
                { "spatial.mine.update", "FG-ACCESS-20260901/spatial.mine.update" },
                { "spatial.patrol.prepare", "FG-ACCESS-20260901/spatial.patrol.prepare" },
                { "spatial.patrol.update", "FG-ACCESS-20260901/spatial.patrol.update" },
                { "spatial.point-defense.prepare", "FG-ACCESS-20260901/spatial.point-defense.prepare" },
                { "spatial.point-defense.update", "FG-ACCESS-20260901/spatial.point-defense.update" },
                { "spatial.telegraph.update", "FG-ACCESS-20260901/spatial.telegraph.update" },
                { "spawning.nest.prepare", "FG-ACCESS-20260901/spawning.nest.prepare" },
                { "spawning.nest.update", "FG-ACCESS-20260901/spawning.nest.update" },
                { "spawning.wave.update", "FG-ACCESS-20260901/spawning.wave.update" },
                { "spawning.wave.callback-dispatch", "FG-ACCESS-20260901/spawning.wave.callback-dispatch" },
                { "terrain.enemy-morph.update", "FG-ACCESS-20260901/terrain.enemy-morph.update" },
                { "terrain.prepare", "FG-ACCESS-20260901/terrain.prepare" },
                { "terrain.update", "FG-ACCESS-20260901/terrain.update" },
                { "terrain.wave-mutator.prepare", "FG-ACCESS-20260901/terrain.wave-mutator.prepare" },
                { "terrain.wave-mutator.update", "FG-ACCESS-20260901/terrain.wave-mutator.update" },
                { "threat.aggregate", "FG-ACCESS-20260901/threat.aggregate" },
                { "wave.frame.close", "FG-ACCESS-20260901/wave.frame.close" },
            };

        // 生产 profile 按 NodeId 排序后的完整根指纹；任何节点或元数据变化都必须重新审阅。
        internal const string ApprovedFingerprintRootGameplay = "df4d2c4ad3f01d04392a8c85eceb0fc25199846373c00ab717cb6399c0d09be1";
        internal const string ApprovedFingerprintRootFixedPopulation = "70cc12c38566f2b5ce377cdeef5d2dff9cdabe6ee7a60ea746ae226f36ea6e0b";

        public static bool TryGet(string nodeId, out FrameAccessReviewId reviewId) =>
            Reviews.TryGetValue(nodeId, out reviewId);

        public static FrameAccessEvidence EvidenceFor(string nodeId) => nodeId switch
        {
            "combat.beam.update" => FrameAccessEvidence.DisabledUnsafe,
            "post-death.life-link.resolve" => FrameAccessEvidence.DisabledUnsafe,
            _ => FrameAccessEvidence.SourceReviewed
        };

        public static bool TryCreate(string nodeId,FrameBindingId bindingId,FramePhaseMask phases,
            FrameTimeDomain timeDomain,FrameExecutionSemantics semantics,IReadOnlyList<FrameResource> reads,
            IReadOnlyList<FrameResource> writes,IReadOnlyList<FrameNodeId> before,
            IReadOnlyList<FrameNodeId> after,IReadOnlyList<string> requiredDependencies,
            IReadOnlyList<OptionalFrameDependency> optionalDependencies,
            FrameGraphCompositionKind compositionKind,out FrameAccessReviewRecord? review)
        {
            if(!Reviews.TryGetValue(nodeId,out FrameAccessReviewId reviewId))
            {
                review=null;
                return false;
            }
            string fingerprint=ComputeFingerprint(nodeId,bindingId,phases,timeDomain,semantics,reads,writes,
                before,after,requiredDependencies,optionalDependencies,compositionKind);
            string artifactId=ArtifactFor(nodeId,out string artifactSha);
            FrameAccessReviewDisposition disposition=DispositionFor(nodeId);
            bool approved=Reviews.ContainsKey(nodeId);
            review=new FrameAccessReviewRecord(reviewId,artifactId,artifactSha,
                artifactId+"@sha256="+artifactSha+"#NodeId="+nodeId,
                FrameAdapterBindingCatalog.ReviewClosure(nodeId),fingerprint,
                semantics.ToString(),disposition,approved);
            return true;
        }

        internal static string ValidateApprovedSnapshot(IReadOnlyList<FrameNodeAdapter> nodes,
            IReadOnlyList<FrameCompositionDiagnostic> diagnostics,HashSet<string> availableDependencies,
            FrameGraphCompositionKind compositionKind)
        {
            var entries=new string[nodes.Count+diagnostics.Count];
            int index=0;
            for(int i=0;i<nodes.Count;i++)
            {
                FrameNodeMetadata metadata=nodes[i].Metadata;
                ValidateReviewRecord(metadata,compositionKind);
                entries[index++]=metadata.Id.Value+"|"+metadata.AccessProfile.Review!.MetadataFingerprint;
            }
            for(int i=0;i<diagnostics.Count;i++)
            {
                FrameCompositionDiagnostic diagnostic=diagnostics[i];
                ValidateReviewRecord(diagnostic.Metadata,compositionKind);
                entries[index++]=diagnostic.NodeId.Value+"|"+diagnostic.Review!.MetadataFingerprint;
            }
            Array.Sort(entries,StringComparer.Ordinal);
            var text=new StringBuilder();
            text.Append("composition|").Append((int)compositionKind).Append('\n');
            var dependencies=new string[availableDependencies.Count];
            availableDependencies.CopyTo(dependencies);
            Array.Sort(dependencies,StringComparer.Ordinal);
            text.Append("available");
            for(int i=0;i<dependencies.Length;i++)text.Append('|').Append(dependencies[i]);
            text.Append('\n');
            for(int i=0;i<entries.Length;i++){if(i>0)text.Append('\n');text.Append(entries[i]);}
            using var sha=SHA256.Create();
            byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var actual=new StringBuilder(hash.Length*2);
            for(int i=0;i<hash.Length;i++)actual.Append(hash[i].ToString("x2"));
            string expected=availableDependencies.Contains("FrameScenario:"+FrameScenarioKind.FixedPopulationBenchmark)
                ?ApprovedFingerprintRootFixedPopulation:ApprovedFingerprintRootGameplay;
            if(!string.Equals(expected,actual.ToString(),StringComparison.Ordinal))
                throw new FrameGraphValidationException($"Production access review snapshot is stale: expected {expected}, actual {actual}, nodes={nodes.Count}, disabled={diagnostics.Count}.");
            return actual.ToString();
        }

        private static void ValidateReviewRecord(FrameNodeMetadata metadata,FrameGraphCompositionKind compositionKind)
        {
            FrameAccessReviewRecord? actual=metadata.AccessProfile.Review;
            if(actual==null)
                throw new FrameGraphValidationException($"Production node '{metadata.Id}' has no review record.");
            if(!TryCreate(metadata.Id.Value,metadata.AccessProfile.BindingId,metadata.ActivePhases,metadata.TimeDomain,
                metadata.ExecutionSemantics,metadata.Reads,metadata.Writes,metadata.Before,metadata.After,
                metadata.RequiredDependencies,metadata.OptionalDependencies,compositionKind,
                out FrameAccessReviewRecord? expected)||expected==null)
                throw new FrameGraphValidationException($"Production node '{metadata.Id}' is missing from the review catalog.");
            if(!actual.Id.Equals(expected.Id)||!string.Equals(actual.ArtifactId,expected.ArtifactId,StringComparison.Ordinal)||
                !string.Equals(actual.ArtifactSha256,expected.ArtifactSha256,StringComparison.Ordinal)||
                !string.Equals(actual.EvidenceLocator,expected.EvidenceLocator,StringComparison.Ordinal)||
                !string.Equals(actual.TransitiveCallees,expected.TransitiveCallees,StringComparison.Ordinal)||
                !string.Equals(actual.MetadataFingerprint,expected.MetadataFingerprint,StringComparison.Ordinal)||
                !string.Equals(actual.ParallelModel,expected.ParallelModel,StringComparison.Ordinal)||
                actual.Disposition!=expected.Disposition||metadata.AccessProfile.Evidence!=EvidenceFor(metadata.Id.Value))
                throw new FrameGraphValidationException($"Production node '{metadata.Id}' has stale or mismatched source-review evidence.");
        }

        internal static string ComputeFingerprint(string nodeId,FrameBindingId bindingId,FramePhaseMask phases,
            FrameTimeDomain timeDomain,FrameExecutionSemantics semantics,IReadOnlyList<FrameResource> reads,
            IReadOnlyList<FrameResource> writes,IReadOnlyList<FrameNodeId> before,
            IReadOnlyList<FrameNodeId> after,IReadOnlyList<string> requiredDependencies,
            IReadOnlyList<OptionalFrameDependency> optionalDependencies,
            FrameGraphCompositionKind compositionKind)
        {
            var text=new StringBuilder();
            text.Append(nodeId).Append('|').Append(bindingId).Append('|').Append((int)phases).Append('|')
                .Append((int)timeDomain).Append('|').Append((int)semantics).Append('|').Append((int)compositionKind);
            Append(text,reads);
            Append(text,writes);
            Append(text,before);
            Append(text,after);
            Append(text,requiredDependencies);
            text.Append('|');
            for(int i=0;i<optionalDependencies.Count;i++)
            {
                if(i>0)text.Append(',');
                text.Append(optionalDependencies[i].Name).Append(':').Append((int)optionalDependencies[i].MissingPolicy);
            }
            using var sha=SHA256.Create();
            byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var result=new StringBuilder(hash.Length*2);
            for(int i=0;i<hash.Length;i++)result.Append(hash[i].ToString("x2"));
            return result.ToString();
        }

        private static void Append(StringBuilder text,IReadOnlyList<FrameResource> resources)
        {
            text.Append('|');
            for(int i=0;i<resources.Count;i++){if(i>0)text.Append(',');text.Append((int)resources[i]);}
        }

        private static void Append<T>(StringBuilder text,IReadOnlyList<T> values)
        {
            text.Append('|');
            for(int i=0;i<values.Count;i++){if(i>0)text.Append(',');text.Append(values[i]);}
        }

        private static string ArtifactFor(string nodeId,out string sha256)
        {
            if(IsSupplementalNode(nodeId))
            {sha256=SupplementalArtifactSha256;return "supplemental-production-nodes.md";}
            if(nodeId.StartsWith("terrain.",StringComparison.Ordinal)||nodeId.StartsWith("combat-setup.",StringComparison.Ordinal)||
                nodeId.StartsWith("spatial.",StringComparison.Ordinal)||nodeId.StartsWith("combat.",StringComparison.Ordinal))
            {sha256=CombatArtifactSha256;return "combat-groups.md";}
            if(nodeId=="attribute.aggregate"||nodeId.StartsWith("frame.",StringComparison.Ordinal)||
                nodeId.StartsWith("build.",StringComparison.Ordinal)||nodeId.StartsWith("early.",StringComparison.Ordinal)||
                nodeId.StartsWith("pregame.",StringComparison.Ordinal)||nodeId.StartsWith("spawning.",StringComparison.Ordinal)||
                nodeId.StartsWith("ai.",StringComparison.Ordinal)||nodeId.StartsWith("movement.enemy",StringComparison.Ordinal)||
                nodeId.StartsWith("movement.path",StringComparison.Ordinal)||nodeId.StartsWith("movement.pull",StringComparison.Ordinal)||
                nodeId.StartsWith("movement.deployable",StringComparison.Ordinal)||nodeId.StartsWith("movement.steal-gold",StringComparison.Ordinal)||
                nodeId.StartsWith("movement.summon",StringComparison.Ordinal)||nodeId.StartsWith("movement.wound",StringComparison.Ordinal))
            {sha256=EarlyArtifactSha256;return "early-groups.md";}
            sha256=CommitArtifactSha256;
            return "commit-postdeath.md";
        }

        internal static int ExpectedArtifactNodeCount(string artifactId) => artifactId switch
        {
            "early-groups.md" => ReportedEarlyNodeCount,
            "combat-groups.md" => ReportedCombatNodeCount,
            "commit-postdeath.md" => 33,
            "supplemental-production-nodes.md" => SupplementalNodeIds.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(artifactId),artifactId,"Unknown review artifact.")
        };

        private static bool IsSupplementalNode(string nodeId)
        {
            for(int i=0;i<SupplementalNodeIds.Count;i++)
                if(string.Equals(SupplementalNodeIds[i],nodeId,StringComparison.Ordinal))return true;
            return false;
        }

        private static FrameAccessReviewDisposition DispositionFor(string nodeId) => nodeId switch
        {
            "combat.beam.update" or "post-death.life-link.resolve" => FrameAccessReviewDisposition.DisabledUnsafe,
            "primary-death.resolve" or "primary-death.callback-dispatch" or "cascade-death.resolve" or
                "cascade-death.callback-dispatch" or "pregame.random-event.update" or
                "pregame.random-event.callback-dispatch" or "spawning.wave.update" or
                "spawning.wave.callback-dispatch" => FrameAccessReviewDisposition.SplitNode,
            _ => FrameAccessReviewDisposition.AcceptedCorrection
        };
    }
}
