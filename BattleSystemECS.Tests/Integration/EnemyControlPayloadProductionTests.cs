using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class EnemyControlPayloadProductionTests : BattleTestBase
    {
        [Fact]
        public void BehaviorTreeBuffFlowsThroughSchedulerEffectAttributeTagAndAttack()
        {
            var ability = Buff("war-cry", 0.3f, 3f, 5f);
            var config = StrictConfig(ability);
            int player = Player(p => { p.Health = 1000f; p.AttackDamage = 0f; p.X = 1f; p.Y = 0f; });
            config.ManaShield.Enabled = false;
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.Damage = 1f; e.MoveSpeed = 0f; });
            int ally = Enemy(e => { e.X = 1f; e.Y = 0f; e.Damage = 10f; e.MoveSpeed = 0f; });
            Store.SetEnemyAttackInterval(source, 0f);
            Store.SetEnemyAttackInterval(ally, 0f);
            Store.EnemyBehaviorTree[source] = ActionTree("enemy_cast_buff", EnemyActionType.BuffAllies, ability.Id);
            Store.EnemyBehaviorTree[ally] = ActionTree("attack_melee", EnemyActionType.AttackMelee, null);
            var (registry, scheduler) = Production(config, player);
            scheduler.Combat.ManaShield = null;
            Store.PlayerManaShield[player] = 0f;
            Store.PlayerManaShieldCap[player] = 0f;
            Store.PlayerManaShieldAbsorbRatio[player] = 0f;
            var damageFacts = new List<PlayerDamagedEvent>();
            registry.EventBus!.PlayerDamaged.Subscribe(damageFacts.Add);

            scheduler.Tick(0.1f, 0);
            Assert.True(GameplayTagRuntime.HasTag(Store, ally, CatalogRegistries.EnemyBuffTag));
            Assert.Equal(13f, Store.GetEnemyAttackDamageProjection(ally), 3);
            Assert.True(Renderer.HasLogContaining("typed 'war-cry'"));
            var firstAttack = Assert.Single(damageFacts, fact => fact.AttackerId == ally);
            // 同帧 AI 先开火再由 ability 系统落 buff：首击是未加成 raw=10 的 applied。
            float armor = Store.GetPlayerArmorProjection(player);
            Assert.Equal(10f * (1f - armor), firstAttack.Damage, 3);

            damageFacts.Clear();
            Store.SetEnemyAttackInterval(ally, 0f);
            Store.EnemyAttackCooldownLeft[ally] = 0f;
            registry.EnemyAI!.InvokeExecuteActionEnum(ally, EnemyActionType.AttackMelee);
            var buffedAttack = Assert.Single(damageFacts);
            Assert.Equal(ally, buffedAttack.AttackerId);
            // 二次近战吃到投影 13；PlayerDamaged 发护甲后的 applied。
            Assert.Equal(13f * (1f - armor), buffedAttack.Damage, 3);
            Assert.Equal(3f * (1f - armor), buffedAttack.Damage - firstAttack.Damage, 3);
        }

        [Fact]
        public void SilenceBlocksProductionTowerAttackAndActiveThenExpires()
        {
            var ability = Silence("emp", 5f, 1.5f);
            var config = StrictConfig(ability);
            int player = Player(p => { p.Health = 1000f; p.AttackDamage = 0f; p.X = 50f; p.Y = 50f; });
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.Damage = 0f; e.MoveSpeed = 0f; });
            int victim = Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; e.Damage = 0f; e.MoveSpeed = 0f; });
            int tower = RawTower(0, 0, damage: 20f, range: 5, speed: 10f);
            var (registry, scheduler) = Production(config, player);
            scheduler.AI.EnemyAI = null;
            registry.EnemyAbility!.EnqueueAbility(source, ability.Id);

            scheduler.Tick(1f, 0);
            Assert.True(Store.TowerIsSilenced[tower]);
            Assert.True(GameplayTagRuntime.HasTag(Store, tower, CatalogRegistries.TowerSilencedTag));
            Assert.Equal(100f, Store.EnemyHealth[victim]);
            var blocked = registry.TowerActiveSkill!.ActivateTower(tower);
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, blocked.Reason);
            Assert.Equal(0f, Store.TowerActiveCooldown[tower]);

            scheduler.Tick(0.6f, 1);
            Assert.Equal(100f, Store.EnemyHealth[victim]);
            float sourceHealth = Store.EnemyHealth[source];
            scheduler.Tick(0.2f, 2);
            Assert.False(Store.TowerIsSilenced[tower]);
            Assert.False(GameplayTagRuntime.HasTag(Store, tower, CatalogRegistries.TowerSilencedTag));
            Assert.True(Store.EnemyHealth[victim] < 100f || Store.EnemyHealth[source] < sourceHealth);
        }

        [Fact]
        public void DispelRemovesOnlyConfiguredDispellableEffectsAndPublishesRemoval()
        {
            var ability = Dispel("purge", 5f);
            var config = StrictConfig(ability);
            int player = Player(p => { p.Health = 1000f; p.AttackDamage = 0f; p.X = 50f; p.Y = 50f; });
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.Damage = 0f; e.MoveSpeed = 0f; });
            int tower = RawTower(1, 0);
            var sourceHandle = Store.GetEntityHandle(source);
            var targetHandle = Store.GetEntityHandle(tower);
            var removable = DurationEffect(700, CatalogRegistries.DispellableTag);
            var durable = DurationEffect(701, CatalogRegistries.EnemyBuffTag);
            Assert.True(Store.GameplayEffectsRuntime.TryApply(removable.Id, removable, sourceHandle, targetHandle, out _));
            Assert.True(Store.GameplayEffectsRuntime.TryApply(durable.Id, durable, sourceHandle, targetHandle, out _));
            var (registry, _) = Production(config, player);
            registry.EnemyAbility!.EnqueueAbility(source, ability.Id);
            registry.EnemyAbility.ExecuteAbilities();

            Assert.Equal(1, Store.GetEffectCount(tower));
            Assert.False(GameplayTagRuntime.HasTag(Store, tower, CatalogRegistries.DispellableTag));
            Assert.True(GameplayTagRuntime.HasTag(Store, tower, CatalogRegistries.EnemyBuffTag));
            Assert.Contains(Enumerable.Range(0, Store.GameplayEffectsRuntime.Events.Count),
                i => Store.GameplayEffectsRuntime.Events.Get(i).Type == GameplayEventType.EffectRemoved);
            Assert.False(Store.TowerIsDispelled[tower]);
        }

        [Fact]
        public void MultiTargetCapacityFailureRejectsAtomicallyWithoutCooldown()
        {
            var ability = Buff("capacity-cry", 0.2f, 3f, 5f);
            var config = StrictConfig(ability);
            int player = Player(p => { p.Health = 1000f; p.AttackDamage = 0f; p.X = 50f; p.Y = 50f; });
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.MoveSpeed = 0f; });
            int first = Enemy(e => { e.X = 1f; e.Y = 0f; e.Damage = 10f; e.MoveSpeed = 0f; });
            int full = Enemy(e => { e.X = 2f; e.Y = 0f; e.Damage = 10f; e.MoveSpeed = 0f; });
            EffectHandle last = default(EffectHandle);
            for (int i = 0; i < ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY; i++)
            {
                var filler = DurationEffect(800 + i, CatalogRegistries.SkillTag);
                Assert.True(Store.GameplayEffectsRuntime.TryApply(filler.Id, filler,
                    Store.GetEntityHandle(source), Store.GetEntityHandle(full), out last));
            }
            var (registry, scheduler) = Production(config, player);
            scheduler.AI.EnemyAI = null;
            registry.EnemyAbility!.EnqueueAbility(source, ability.Id);

            scheduler.Tick(0.1f, 0);

            Assert.False(GameplayTagRuntime.HasTag(Store, first, CatalogRegistries.EnemyBuffTag));
            Assert.Equal(ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY, Store.GetEffectCount(full));
            Assert.True(Store.GameplayEffectsRuntime.Remove(Store.GetEntityHandle(full), last));
            registry.EnemyAbility.EnqueueAbility(source, ability.Id);
            scheduler.Tick(0.1f, 1);
            Assert.True(GameplayTagRuntime.HasTag(Store, first, CatalogRegistries.EnemyBuffTag));
            Assert.True(GameplayTagRuntime.HasTag(Store, full, CatalogRegistries.EnemyBuffTag));
        }

        [Fact]
        public void DispelWithoutEligibleTargetRejectsWithoutCooldownOrSideEffects()
        {
            var ability = Dispel("empty-purge", 5f);
            var config = StrictConfig(ability);
            int player = Player(p => { p.Health = 1000f; p.AttackDamage = 0f; p.X = 50f; p.Y = 50f; });
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.MoveSpeed = 0f; });
            int tower = RawTower(1, 0);
            var (registry, scheduler) = Production(config, player);
            scheduler.AI.EnemyAI = null;
            registry.EnemyAbility!.EnqueueAbility(source, ability.Id);
            scheduler.Tick(0.1f, 0);
            Assert.Equal(0, Store.GetEffectCount(tower));

            var removable = DurationEffect(950, CatalogRegistries.DispellableTag);
            Assert.True(Store.GameplayEffectsRuntime.TryApply(removable.Id, removable,
                Store.GetEntityHandle(source), Store.GetEntityHandle(tower), out _));
            registry.EnemyAbility.EnqueueAbility(source, ability.Id);
            scheduler.Tick(0.1f, 1);
            Assert.Equal(0, Store.GetEffectCount(tower));
        }

        private (SystemRegistry Registry, FrameScheduler Scheduler) Production(GameConfig config, int player)
        {
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, player, new StateMachine());
            registry.WireDependencies(Store, player);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            return (registry, scheduler);
        }

        private static GameConfig StrictConfig(params EnemyAbilityDef[] abilities)
        {
            var config = new GameConfig { StrictCatalogReferences = true, EnemyAbilities = abilities.ToList() };
            config.CompiledCatalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(), config.EnemyAbilities);
            return config;
        }

        private static EnemyAbilityDef Buff(string id, float multiplier, float duration, float radius) =>
            new EnemyAbilityDef { Id = id, Name = id, AbilityType = "buff_allies", DamageMultiplier = multiplier,
                BuffDuration = (int)duration, AoeRadius = (int)radius, Cooldown = 5f };
        private static EnemyAbilityDef Silence(string id, float radius, float duration) =>
            new EnemyAbilityDef { Id = id, Name = id, AbilityType = "silence_tower", SilenceRadius = radius,
                SilenceDuration = duration, Cooldown = 5f };
        private static EnemyAbilityDef Dispel(string id, float radius) =>
            new EnemyAbilityDef { Id = id, Name = id, AbilityType = "dispel_tower", DispelRadius = radius, Cooldown = 5f };

        private static GameplayEffectDefinition DurationEffect(int id, TagId granted) =>
            new GameplayEffectDefinition(new EffectId(id), EffectType.Duration, Array.Empty<ModifierDefinition>(),
                20f, 0f, ClockId.Enemy, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.Status, CatalogRegistries.SkillTag,
                Array.Empty<ExecutionId>(), grantedTags: new[] { granted });

        private static BTCachedTree ActionTree(string action, EnemyActionType actionType, string? abilityId)
        {
            var node = new BTCachedNode { Id = action, Type = BTNodeType.Action, Action = action,
                PrecomputedActionEnum = actionType, AbilityId = abilityId, Children = Array.Empty<int>() };
            return new BTCachedTree { MonsterType = action, Root = node, Nodes = new[] { node } };
        }
    }
}
