// 类型化注册配方；结构化 spec 通过标识选择这些方法，生成器只引用受控方法。
#nullable enable
using System;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
namespace BattleSystemECS.Core {public sealed partial class SystemRegistry {
internal static void CreateMap(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Map = new MapSystem(logger, store);
}
internal static void WireMap(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindMap(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateWaveSpawning(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var battleEb = battleEventBus ?? NullEventBus.Instance;
registry.WaveSpawning = new WaveSpawningSystem(store, logger, config, eventBus: battleEb);
}
internal static void WireWaveSpawning(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWaveSpawning(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.WaveSpawning = registry.WaveSpawning;
scheduler.Spawning.WaveSpawning = registry.WaveSpawning;
}
internal static void CreateNest(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Nest = new NestSystem(store, config, logger, playerId);
registry.Nest.Initialize();
}
internal static void WireNest(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindNest(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Spawning.Nest = registry.Nest;
}
internal static void CreateGold(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Gold = new GoldSystem(store, logger, registry.TechTree);
}
internal static void WireGold(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindGold(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Gold = registry.Gold;
}
internal static void CreateUpgrade(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Upgrade = new UpgradeSystem(store, logger, playerId, config, registry.TowerUpgrade!);
}
internal static void WireUpgrade(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindUpgrade(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Upgrade = registry.Upgrade;
}
internal static void CreateInterest(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Interest = new InterestSystem(store, logger, config, playerId);
}
internal static void WireInterest(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindInterest(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Interest = registry.Interest;
}
internal static void CreateSkill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Skill = new SkillSystem(store, logger, playerId, config, registry.TechTree);
registry.Skill.InitializePlayerSkills();
}
internal static void WireSkill(SystemRegistry registry, ComponentStore store, int playerId) {
registry.Skill!.InjectDotSystem(registry.Buff);
registry.Skill.InjectHealingZoneSystem(registry.HealingZone);
registry.Skill.InjectTimeRewindSystem(registry.TimeRewind);
registry.Skill.InjectNecromancerSystem(registry.Necromancer);
registry.Skill.InjectManaSystem(registry.Mana);
}
internal static void BindSkill(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Skill = registry.Skill;
scheduler.CombatSetup.Skill = registry.Skill;
scheduler.SkillBuff.Skill = registry.Skill;
var skill = registry.Skill!;
scheduler.RegisterAbilityPhaseConsumer(skill.SetPhaseContext,
    () => skill.RejectPendingSkillDamage(global::BattleSystemECS.Content.Contracts.SkillDamageRejectReason.PhaseNotAllowed));
}
internal static void CreateBuff(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Buff = new BuffSystem(store, playerId);
 registry.Buff.SetCatalog(config.CompiledCatalog);
}
internal static void WireBuff(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindBuff(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Buff = registry.Buff;
}
internal static void CreateElementalReaction(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.ElementalReaction = new ElementalReactionSystem(store, playerId, logger);
}
internal static void WireElementalReaction(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindElementalReaction(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.ElementalReaction = registry.ElementalReaction;
}
internal static void CreateCombo(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Combo = new ComboSystem(store, config.Combo);
}
internal static void WireCombo(SystemRegistry registry, ComponentStore store, int playerId) {
store.OnEnemyKilled += (enemyId, pid) => registry.Combo!.HandleComboIncrement(pid);
}
internal static void BindCombo(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.Combo = registry.Combo;
scheduler.ConfigureGameplayRuntime(registry._runtimeTriggers);
scheduler.Store.GameplayTriggersRuntime.RegisterEffect(registry._runtimeComboEffect);
}
internal static void CreateAutoSkill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.AutoSkill = new AutoSkillSystem(store, logger, playerId, registry.Skill, config.AutoSkill);
}
internal static void WireAutoSkill(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindAutoSkill(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.AutoSkill = registry.AutoSkill;
}
internal static void CreateMana(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.Mana = new ManaSystem(store, logger, config, playerId, registry.TechTree);
registry.Mana.Initialize();
}
internal static void WireMana(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindMana(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Mana = registry.Mana;
scheduler.CombatSetup.Mana = registry.Mana;
scheduler.Combat.Mana = registry.Mana;
}
internal static void CreateManaShield(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
 registry.ManaShield = new ManaShieldSystem(store, config, playerId);
registry.ManaShield.Initialize();
}
internal static void WireManaShield(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindManaShield(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.ManaShield = registry.ManaShield;
scheduler.Combat.ManaShield = registry.ManaShield;
}
internal static void CreateGlobalSkill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.GlobalSkill = new GlobalSkillSystem(store, config, logger, playerId);
}
internal static void WireGlobalSkill(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindGlobalSkill(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.GlobalSkill = registry.GlobalSkill;
scheduler.CombatSetup.GlobalSkill = registry.GlobalSkill;
scheduler.Combat.GlobalSkill = registry.GlobalSkill;
var globalSkill = registry.GlobalSkill!;
scheduler.RegisterAbilityPhaseConsumer(globalSkill.SetPhaseContext,
    () => { globalSkill.RejectPendingActivation(); });
}
internal static void CreateAbilityPayloads(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.AbilityPayloads = new ProductionAbilityPayloadHandler(store, registry.Necromancer ?? throw new InvalidOperationException("registry.Necromancer dependency is missing"), registry.TimeRewind ?? throw new InvalidOperationException("registry.TimeRewind dependency is missing"));
}
internal static void WireAbilityPayloads(SystemRegistry registry, ComponentStore store, int playerId) {
registry.Skill!.SetPayloadHandler(registry.AbilityPayloads!);
registry.HeroSkill!.SetPayloadHandler(registry.AbilityPayloads!);
registry.TowerActiveSkill!.SetPayloadHandler(registry.AbilityPayloads!);
registry.GlobalSkill!.SetPayloadHandler(registry.AbilityPayloads!);
registry.EnemyAbility!.SetPayloadHandler(registry.AbilityPayloads!);
}
internal static void BindAbilityPayloads(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateMark(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Mark = new MarkSystem(store, playerId);
}
internal static void WireMark(SystemRegistry registry, ComponentStore store, int playerId) {
store.OnEnemyKilled += (enemyId, pid) => registry.Mark!.OnEnemyDestroyed(enemyId);
}
internal static void BindMark(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Mark = registry.Mark;
}
internal static void CreateDeathMark(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.DeathMark = new DeathMarkSystem(store, playerId);
}
internal static void WireDeathMark(SystemRegistry registry, ComponentStore store, int playerId) {
store.OnEnemyKilled += (enemyId, pid) => registry.DeathMark!.OnEnemyDestroyed(enemyId);
}
internal static void BindDeathMark(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.DeathMark = registry.DeathMark;
}
internal static void CreateCulling(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Culling = new CullingSystem(store, playerId);
}
internal static void WireCulling(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindCulling(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Culling = registry.Culling;
}
internal static void CreateTimeRewind(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TimeRewind = new TimeRewindSnapshotSystem(store);
}
internal static void WireTimeRewind(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTimeRewind(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.TimeRewind = registry.TimeRewind;
}
internal static void CreateTowerPlacement(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var battleEb = battleEventBus ?? NullEventBus.Instance;
registry.TowerPlacement = new TowerPlacementSystem(store, logger, config, battleEb);
}
internal static void WireTowerPlacement(SystemRegistry registry, ComponentStore store, int playerId) {
registry.TowerPlacement!.SetTowerModifierSystem(registry.TowerModifier);
}
internal static void BindTowerPlacement(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateTowerAttack(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var battleEb = battleEventBus ?? NullEventBus.Instance;
registry.TowerAttack = new TowerAttackSystem(store, logger, registry.TechTree, 10, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"), battleEb);
registry.TowerAttack.SetGameConfig(config);
}
internal static void WireTowerAttack(SystemRegistry registry, ComponentStore store, int playerId) {
registry.TowerAttack!.SetBuffSystem(registry.Buff);
registry.TowerAttack.SetBleedSystem(registry.Bleed);
registry.TowerAttack.SetProjectileSystem(registry.Projectile);
registry.TowerAttack.SetLifeLinkSystem(registry.LifeLink);
registry.TowerAttack.SetHitShieldSystem(registry.HitShield);
registry.TowerAttack.SetDesperationSystem(registry.Desperation);
registry.TowerAttack.SetFireTrailSystem(registry.FireTrail);
registry.TowerAttack.SetCullingSystem(registry.Culling);
}
internal static void BindTowerAttack(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.TowerAttack = registry.TowerAttack;
scheduler.Combat.TowerAttack = registry.TowerAttack;
}
internal static void CreateTowerUpgrade(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerUpgrade = new TowerUpgradeSystem(store, logger, config);
}
internal static void WireTowerUpgrade(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerUpgrade(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateTowerExperience(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerExperience = new TowerExperienceSystem(store, config);
}
internal static void WireTowerExperience(SystemRegistry registry, ComponentStore store, int playerId) {
store.OnTowerKill += (enemyId, pid, towerId) =>
    registry.TowerExperience!.HandleEnemyKilled(enemyId, pid, towerId);
}
internal static void BindTowerExperience(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateTowerSynergy(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerSynergy = new TowerSynergySystem(store, logger);
registry.TowerSynergy.LoadSynergyConfig();
}
internal static void WireTowerSynergy(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerSynergy(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.TowerSynergy = registry.TowerSynergy;
scheduler.Combat.TowerSynergy = registry.TowerSynergy;
}
internal static void CreateTowerFortress(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerFortress = new TowerFortressSystem(store, logger);
}
internal static void WireTowerFortress(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerFortress(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.TowerFortress = registry.TowerFortress;
scheduler.Combat.TowerFortress = registry.TowerFortress;
}
internal static void CreateKillCooldownReset(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.KillCooldownReset = new KillCooldownResetSystem(store, config, playerId);
}
internal static void WireKillCooldownReset(SystemRegistry registry, ComponentStore store, int playerId) {
registry.KillCooldownReset!.SubscribeToEvents();
}
internal static void BindKillCooldownReset(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateHealOnKill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HealOnKill = new HealOnKillSystem(store);
}
internal static void WireHealOnKill(SystemRegistry registry, ComponentStore store, int playerId) {
registry.HealOnKill!.SubscribeToEvents();
}
internal static void BindHealOnKill(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateBloodlust(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Bloodlust = new BloodlustSystem(store, config);
}
internal static void WireBloodlust(SystemRegistry registry, ComponentStore store, int playerId) {
registry.Bloodlust!.SubscribeToEvents();
}
internal static void BindBloodlust(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Bloodlust = registry.Bloodlust;
}
internal static void CreatePreFightBuff(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.PreFightBuff = new PreFightBuffSystem(store, config);
}
internal static void WirePreFightBuff(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPreFightBuff(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.PreFightBuff = registry.PreFightBuff;
}
internal static void CreateMomentum(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Momentum = new MomentumSystem(store, config);
}
internal static void WireMomentum(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindMomentum(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Momentum = registry.Momentum;
}
internal static void CreateAdrenaline(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Adrenaline = new AdrenalineSystem(store, config);
}
internal static void WireAdrenaline(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindAdrenaline(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Adrenaline = registry.Adrenaline;
}
internal static void CreateCrest(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Crest = new CrestSystem(store, config);
}
internal static void WireCrest(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindCrest(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Crest = registry.Crest;
}
internal static void CreateTowerMorph(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerMorph = new TowerMorphSystem(store);
}
internal static void WireTowerMorph(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerMorph(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerMorph = registry.TowerMorph;
}
internal static void CreateAuraTower(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.AuraTower = new AuraTowerSystem(store);
}
internal static void WireAuraTower(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindAuraTower(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.AuraTower = registry.AuraTower;
scheduler.Combat.AuraTower = registry.AuraTower;
}
internal static void CreateCurse(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Curse = new CurseAuraSystem(store);
}
internal static void WireCurse(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindCurse(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.Curse = registry.Curse;
scheduler.Combat.Curse = registry.Curse;
}
internal static void CreatePullTower(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.PullTower = new PullTowerSystem(store);
}
internal static void WirePullTower(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPullTower(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.PullTower = registry.PullTower;
scheduler.Combat.PullTower = registry.PullTower;
}
internal static void CreateTaunt(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Taunt = new TauntSystem(store);
}
internal static void WireTaunt(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTaunt(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.Taunt = registry.Taunt;
scheduler.Combat.Taunt = registry.Taunt;
}
internal static void CreateBleed(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Bleed = new BleedSystem(store, playerId);
}
internal static void WireBleed(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindBleed(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Bleed = registry.Bleed;
}
internal static void CreateFrostbite(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Frostbite = new FrostbiteSystem(store, playerId);
}
internal static void WireFrostbite(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindFrostbite(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Frostbite = registry.Frostbite;
}
internal static void CreateHealAura(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HealAura = new HealAuraSystem(store);
}
internal static void WireHealAura(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHealAura(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.HealAura = registry.HealAura;
}
internal static void CreateThornsAura(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ThornsAura = new ThornsAuraSystem(store);
}
internal static void WireThornsAura(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindThornsAura(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.ThornsAura = registry.ThornsAura;
scheduler.SkillBuff.ThornsAuraPlayerId = registry._thornsAuraPlayerId;
}
internal static void CreateRally(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Rally = new RallySystem(store, logger, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"));
}
internal static void WireRally(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindRally(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Rally = registry.Rally;
}
internal static void CreateTowerActiveSkill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerActiveSkill = new TowerActiveSkillSystem(store, config);
}
internal static void WireTowerActiveSkill(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerActiveSkill(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerActiveSkill = registry.TowerActiveSkill;
scheduler.RegisterAbilityPhaseConsumer(registry.TowerActiveSkill!.SetPhaseContext);
}
internal static void CreateAggro(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Aggro = new AggroSystem(store);
}
internal static void WireAggro(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindAggro(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Aggro = registry.Aggro;
}
internal static void CreateEchoClone(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EchoClone = new EchoCloneSystem(store);
}
internal static void WireEchoClone(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindEchoClone(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.EchoClone = registry.EchoClone;
}
internal static void CreateTowerModifier(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerModifier = new TowerModifierSystem(store, config);
}
internal static void WireTowerModifier(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerModifier(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateFireTrail(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.FireTrail = new FireTrailSystem(store);
}
internal static void WireFireTrail(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindFireTrail(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateProjectile(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var battleEb = battleEventBus ?? NullEventBus.Instance;
registry.Projectile = new ProjectileSystem(store, logger, battleEb);
}
internal static void WireProjectile(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindProjectile(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Projectile = registry.Projectile;
}
internal static void CreateChronoTower(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ChronoTower = new ChronoTowerSystem(store);
}
internal static void WireChronoTower(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindChronoTower(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Spatial.ChronoTower = registry.ChronoTower;
}
internal static void CreateMine(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Mine = new MineSystem(store, logger, config, playerId);
}
internal static void WireMine(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindMine(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Spatial.Mine = registry.Mine;
}
internal static void CreateTowerShrine(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerShrine = new TowerShrineSystem(store);
}
internal static void WireTowerShrine(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerShrine(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerShrine = registry.TowerShrine;
}
internal static void CreateTowerBeacon(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerBeacon = new TowerBeaconSystem(store);
}
internal static void WireTowerBeacon(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerBeacon(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerBeacon = registry.TowerBeacon;
}
internal static void CreatePlayerTowerAttack(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var battleEb = battleEventBus ?? NullEventBus.Instance;
registry.PlayerTowerAttack = new PlayerTowerAttackSystem(store, logger, playerId, config, registry.TechTree, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"), battleEb);
}
internal static void WirePlayerTowerAttack(SystemRegistry registry, ComponentStore store, int playerId) {
registry.PlayerTowerAttack!.SetLifeLinkSystem(registry.LifeLink);
registry.PlayerTowerAttack.SetHitShieldSystem(registry.HitShield);
}
internal static void BindPlayerTowerAttack(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.PlayerTowerAttack = registry.PlayerTowerAttack;
scheduler.Combat.PlayerTowerAttack = registry.PlayerTowerAttack;
}
internal static void CreateHero(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Hero = new HeroSystem(store, playerId);
}
internal static void WireHero(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHero(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.Hero = registry.Hero;
scheduler.Combat.Hero = registry.Hero;
}
internal static void CreateHeroSkill(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HeroSkill = new HeroSkillSystem(store, playerId, config: config);
registry.HeroSkill.SetConfig(config);
registry.HeroSkill.Initialize();
}
internal static void WireHeroSkill(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHeroSkill(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.HeroSkill = registry.HeroSkill;
scheduler.RegisterAbilityPhaseConsumer(registry.HeroSkill!.SetPhaseContext);
}
internal static void CreateEnemyMovement(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyMovement = new EnemyMovementSystem(store, playerId, config.MapWidth, config);
}
internal static void WireEnemyMovement(SystemRegistry registry, ComponentStore store, int playerId) {
registry.EnemyMovement!.SetPathfindingSystem(registry.Pathfinding!);
registry.EnemyMovement.SetBossTrailSystem(registry.BossTrailAoe!);
registry.EnemyMovement.SetWeatherSystem(registry.Weather!);
registry.EnemyMovement.SetDayNightSystem(registry.DayNight!);
}
internal static void BindEnemyMovement(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.EnemyMovement = registry.EnemyMovement;
}
internal static void CreateEnemyAI(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyAI = new EnemyAISystem(store, logger, playerId, config, registry.EnemyAbility, registry.TechTree, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"));
}
internal static void WireEnemyAI(SystemRegistry registry, ComponentStore store, int playerId) {
registry.EnemyAI!.SetWaveSpawningSystem(registry.WaveSpawning);
}
internal static void BindEnemyAI(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.EnemyAI = registry.EnemyAI;
}
internal static void CreateEnemyAbility(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyAbility = new EnemyAbilitySystem(store, logger, playerId, config, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"));
}
internal static void WireEnemyAbility(SystemRegistry registry, ComponentStore store, int playerId) {
registry.EnemyAbility!.SetTelegraphSystem(registry.Telegraph);
}
internal static void BindEnemyAbility(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.EnemyAbility = registry.EnemyAbility;
scheduler.RegisterAbilityPhaseConsumer(registry.EnemyAbility!.SetPhaseContext);
registry.EnemyAbility!.SetPhaseContext(PhaseContext.FromGameState(scheduler.Phase));
}
internal static void CreateEnemyFission(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyFission = new EnemyFissionSystem(store, config, logger);
}
internal static void WireEnemyFission(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindEnemyFission(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.EnemyFission = registry.EnemyFission;
}
internal static void CreateEnemyMorph(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyMorph = new EnemyMorphSystem(store, config, logger);
}
internal static void WireEnemyMorph(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindEnemyMorph(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Terrain.EnemyMorph = registry.EnemyMorph;
}
internal static void CreateEnemyBurrow(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyBurrow = new EnemyBurrowSystem(store, playerId);
}
internal static void WireEnemyBurrow(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindEnemyBurrow(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Burrow = registry.EnemyBurrow;
}
internal static void CreateNecromancer(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Necromancer = new NecromancerSystem(store, config, logger);
}
internal static void WireNecromancer(SystemRegistry registry, ComponentStore store, int playerId) {
store.OnEnemyKilled += (enemyId, pid) => registry.Necromancer!.OnEnemyKilled(enemyId, pid);
}
internal static void BindNecromancer(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Necromancer = registry.Necromancer;
}
internal static void CreateLifeLink(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.LifeLink = new EnemyLifeLinkSystem(store, config, logger);
}
internal static void WireLifeLink(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindLifeLink(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.LifeLink = registry.LifeLink;
registry.LifeLink!.SetBreakPenaltyDispatchEnabled(false);
}
internal static void CreateHitShield(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HitShield = new HitShieldSystem(store, logger);
}
internal static void WireHitShield(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHitShield(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.HitShield = registry.HitShield;
scheduler.Combat.HitShield = registry.HitShield;
}
internal static void CreateTowerSabotage(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerSabotage = new TowerSabotageSystem(store);
}
internal static void WireTowerSabotage(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerSabotage(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerSabotage = registry.TowerSabotage;
}
internal static void CreateManaBurn(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ManaBurn = new ManaBurnSystem(store, playerId);
}
internal static void WireManaBurn(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindManaBurn(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.ManaBurn = registry.ManaBurn;
}
internal static void CreatePhase(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Phase = new PhaseSystem(store, playerId);
}
internal static void WirePhase(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPhase(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Phase = registry.Phase;
}
internal static void CreateFear(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Fear = new FearSystem(store, playerId);
}
internal static void WireFear(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindFear(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Fear = registry.Fear;
}
internal static void CreateEnemyStrafe(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EnemyStrafe = new EnemyStrafeSystem(store, logger);
}
internal static void WireEnemyStrafe(SystemRegistry registry, ComponentStore store, int playerId) {
registry.TowerAttack!.SetEnemyStrafeSystem(registry.EnemyStrafe);
}
internal static void BindEnemyStrafe(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.EnemyStrafe = registry.EnemyStrafe;
}
internal static void CreateSuicideBomb(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.SuicideBomb = new SuicideBombSystem(store, playerId, registry.ReflectTower, registry.TowerStealth);
}
internal static void WireSuicideBomb(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindSuicideBomb(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.SuicideBomb = registry.SuicideBomb;
}
internal static void CreateReflectTower(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ReflectTower = new ReflectTowerSystem(store, playerId);
}
internal static void WireReflectTower(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindReflectTower(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.ReflectTower = registry.ReflectTower;
scheduler.Combat.ReflectTower = registry.ReflectTower;
}
internal static void CreateTowerStealth(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TowerStealth = new TowerStealthSystem(store, playerId);
}
internal static void WireTowerStealth(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTowerStealth(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.TowerStealth = registry.TowerStealth;
}
internal static void CreatePathBlock(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.PathBlock = new PathBlockSystem(store);
}
internal static void WirePathBlock(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPathBlock(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.PathBlock = registry.PathBlock;
}
internal static void CreateDesperation(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Desperation = new DesperationSystem(store);
}
internal static void WireDesperation(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindDesperation(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Desperation = registry.Desperation;
scheduler.PreGame.Desperation = registry.Desperation;
}
internal static void CreateShopReroll(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ShopReroll = new ShopRerollSystem(store, logger, config, playerId);
}
internal static void WireShopReroll(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindShopReroll(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.ShopReroll = registry.ShopReroll;
}
internal static void CreateTerrain(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Terrain = new TerrainSystem(store, playerId, config);
}
internal static void WireTerrain(SystemRegistry registry, ComponentStore store, int playerId) {
registry.Terrain!.SetBuffSystem(registry.Buff);
}
internal static void BindTerrain(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Terrain.Terrain = registry.Terrain;
}
internal static void CreatePathfinding(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Pathfinding = new PathfindingSystem(store);
}
internal static void WirePathfinding(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPathfinding(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.Pathfinding = registry.Pathfinding;
scheduler.RegisterPathWaypointCountQuery(registry.Pathfinding!.GetPathWaypointCount);
}
internal static void CreateDeployableTrap(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.DeployableTrap = new DeployableTrapSystem(store);
}
internal static void WireDeployableTrap(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindDeployableTrap(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.DeployableTrap = registry.DeployableTrap;
}
internal static void CreatePathModifier(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.PathModifier = new PathModifierSystem(store);
}
internal static void WirePathModifier(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPathModifier(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.PathModifier = registry.PathModifier;
}
internal static void CreatePull(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Pull = new PullSystem(store, playerId);
}
internal static void WirePull(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPull(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Movement.Pull = registry.Pull;
}
internal static void CreateWeather(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Weather = new WeatherSystem(store, config);
}
internal static void WireWeather(SystemRegistry registry, ComponentStore store, int playerId) {
registry.TowerAttack!.SetWeatherSystem(registry.Weather);
}
internal static void BindWeather(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.Weather = registry.Weather;
}
internal static void CreateDayNight(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.DayNight = new DayNightSystem(store, config);
registry.DayNight.Initialize(playerId);
}
internal static void WireDayNight(SystemRegistry registry, ComponentStore store, int playerId) {
registry.TowerAttack!.SetDayNightSystem(registry.DayNight);
}
internal static void BindDayNight(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.DayNight = registry.DayNight;
}
internal static void CreateWaveMutator(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.WaveMutator = new WaveMutatorSystem(store, playerId, logger);
registry.WaveMutator.LoadMutators(config.WaveMutatorDefs);
}
internal static void WireWaveMutator(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWaveMutator(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Terrain.WaveMutator = registry.WaveMutator;
}
internal static void CreateRandomEvent(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.RandomEvent = new RandomEventSystem(store, config);
}
internal static void WireRandomEvent(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindRandomEvent(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.RandomEvent = registry.RandomEvent;
}
internal static void CreateTelegraph(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Telegraph = new TelegraphSystem(store, logger, config, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"));
}
internal static void WireTelegraph(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTelegraph(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Spatial.Telegraph = registry.Telegraph;
}
internal static void CreateAdaptiveDifficulty(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.AdaptiveDifficulty = new AdaptiveDifficultySystem(store, config);
}
internal static void WireAdaptiveDifficulty(SystemRegistry registry, ComponentStore store, int playerId) {
registry.AdaptiveDifficulty!.SetWaveSpawningSystem(registry.WaveSpawning!);
}
internal static void BindAdaptiveDifficulty(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PreGame.AdaptiveDifficulty = registry.AdaptiveDifficulty;
}
internal static void CreateCorpseEffect(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.CorpseEffect = new CorpseEffectSystem(store, config, registry.Buff, logger);
registry.CorpseEffect.LoadCorpseEffects();
}
internal static void WireCorpseEffect(SystemRegistry registry, ComponentStore store, int playerId) {
registry.CorpseEffect!.SubscribeToOnEnemyKilled();
}
internal static void BindCorpseEffect(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.CorpseEffect = registry.CorpseEffect;
}
internal static void CreateHealingZone(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HealingZone = new HealingZoneSystem(store, logger);
}
internal static void WireHealingZone(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHealingZone(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.HealingZone = registry.HealingZone;
}
internal static void CreateZoneControl(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ZoneControl = new ZoneControlSystem(store, logger);
}
internal static void WireZoneControl(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindZoneControl(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.ZoneControl = registry.ZoneControl;
}
internal static void CreateObjective(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Objective = new ObjectiveSystem(store, playerId, registry.EventBus ?? throw new InvalidOperationException("registry.EventBus dependency is missing"));
}
internal static void WireObjective(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindObjective(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.Objective = registry.Objective;
scheduler.PostDeath.Objective = registry.Objective;
}
internal static void CreateWaveBranch(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.WaveBranch = new WaveBranchSystem(store, logger, config, stateMachine);
}
internal static void WireWaveBranch(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWaveBranch(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.WaveBranch = registry.WaveBranch;
}
internal static void CreateResourceNode(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.ResourceNode = new ResourceNodeSystem(store, logger, playerId);
}
internal static void WireResourceNode(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindResourceNode(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Build.ResourceNode = registry.ResourceNode;
scheduler.PostDeath.ResourceNode = registry.ResourceNode;
}
internal static void CreateWavePreview(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.WavePreview = new WavePreviewSystem(store, config, playerId);
}
internal static void WireWavePreview(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWavePreview(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateDoomClock(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.DoomClock = new DoomClockSystem(store, playerId);
}
internal static void WireDoomClock(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindDoomClock(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.DoomClock = registry.DoomClock;
}
internal static void CreateSoulHarvest(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.SoulHarvest = new SoulHarvestSystem(store, config.SoulHarvest, logger);
}
internal static void WireSoulHarvest(SystemRegistry registry, ComponentStore store, int playerId) {
registry.SoulHarvest!.SubscribeToEvents();
}
internal static void BindSoulHarvest(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.PostDeath.SoulHarvest = registry.SoulHarvest;
}
internal static void CreateReplay(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Replay = new ReplaySystem(store, config, playerId);
}
internal static void WireReplay(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindReplay(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateHotZone(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.HotZone = new HotZoneSystem(store, config, playerId);
}
internal static void WireHotZone(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindHotZone(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.HotZone = registry.HotZone;
}
internal static void CreateTerrainZone(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.TerrainZone = new TerrainZoneSystem(store, config, playerId);
}
internal static void WireTerrainZone(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTerrainZone(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.TerrainZone = registry.TerrainZone;
}
internal static void CreateFrostZone(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.FrostZone = new FrostZoneSystem(store);
}
internal static void WireFrostZone(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindFrostZone(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.FrostZone = registry.FrostZone;
}
internal static void CreateWanderRoam(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.WanderRoam = new WanderRoamSystem(store);
}
internal static void WireWanderRoam(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWanderRoam(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.CombatSetup.WanderRoam = registry.WanderRoam;
}
internal static void CreateMagnetize(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Magnetize = new MagnetizeSystem(store, logger);
}
internal static void WireMagnetize(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindMagnetize(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Magnetize = registry.Magnetize;
}
internal static void CreateSapper(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Sapper = new SapperSystem(store, logger);
}
internal static void WireSapper(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindSapper(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.AI.Sapper = registry.Sapper;
}
internal static void CreateWisp(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Wisp = new WispSystem(store, logger);
}
internal static void WireWisp(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindWisp(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.SkillBuff.Wisp = registry.Wisp;
}
internal static void CreateBossTrailAoe(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.BossTrailAoe = new BossTrailAoeSystem(store, playerId);
}
internal static void WireBossTrailAoe(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindBossTrailAoe(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateTechTree(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
var techConfig = TechTreeSystem.LoadConfig(logger);
registry.TechTree = new TechTreeSystem(store, logger, playerId, techConfig, config);
}
internal static void WireTechTree(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindTechTree(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreatePickup(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Pickup = new PickupSystem(store, config, logger);
}
internal static void WirePickup(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindPickup(SystemRegistry registry, FrameScheduler scheduler) {
scheduler.Combat.Pickup = registry.Pickup;
}
internal static void CreateInventory(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Inventory = new InventorySystem(store, config, logger);
}
internal static void WireInventory(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindInventory(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateAscension(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Ascension = new AscensionSystem(store, logger, config);
registry.Ascension.SelectModifier("tough_enemies");
}
internal static void WireAscension(SystemRegistry registry, ComponentStore store, int playerId) {
registry.WaveSpawning!.SetAscensionSystem(registry.Ascension);
}
internal static void BindAscension(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateSave(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.Save = new SaveSystem(store, playerId);
}
internal static void WireSave(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindSave(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateEventBus(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
registry.EventBus = new EventBus();
}
internal static void WireEventBus(SystemRegistry registry, ComponentStore store, int playerId) {
registry._thornsAuraPlayerId = playerId;
}
internal static void BindEventBus(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateProductionEvents(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
}
internal static void WireProductionEvents(SystemRegistry registry, ComponentStore store, int playerId) {
registry.WaveSpawning!.OnWaveComplete += () => registry.TechTree!.OnWaveComplete();
registry.WaveSpawning.OnWaveComplete += () => registry.Interest!.OnWaveComplete();
registry.WaveSpawning.OnWaveComplete += () => registry.Save!.SaveCheckpoint();
registry.WaveSpawning.OnWaveComplete += () => registry.WaveBranch!.CheckAndActivateBranch(
    registry.WaveSpawning.GetCurrentWave() - 1, registry.WaveSpawning.GetCurrentLevel());
registry.WaveSpawning.OnWaveStart += () =>
{
    int wave = registry.WaveSpawning.GetCurrentWave();
    registry.PlayerTowerAttack!.SetWaveNumber(wave);
    registry.TowerAttack!.SetWaveNumber(wave);
    registry.Skill!.SetWaveNumber(wave);
    registry.Combo!.ResetCombo(playerId);
    registry.WaveMutator!.OnWaveStart(wave);
    registry.Gold!.HandleWaveStart();
    registry.Momentum!.HandleWaveStart();
    registry.PreFightBuff!.HandleWaveStart();
    registry.Crest!.HandleWaveStart(wave);
    registry.WavePreview!.HandleWaveStart(registry.WaveSpawning.GetCurrentLevel(), wave);
};
registry.WaveSpawning.OnWaveComplete += registry.Momentum!.HandleWaveComplete;
registry.WaveSpawning.OnWaveComplete += registry.PreFightBuff!.HandleWaveComplete;
registry.WaveSpawning.OnWaveComplete += registry.Crest!.HandleWaveComplete;
registry.WaveSpawning.OnBreatherWaveComplete += registry.Gold!.HandleBreatherWaveComplete;
registry.WaveSpawning!.OnWaveStart += registry.Culling!.OnWaveStart;
registry.Culling.OnCullingKilled += registry.Gold!.HandleCullingKilled;
}
internal static void BindProductionEvents(SystemRegistry registry, FrameScheduler scheduler) {
}
internal static void CreateFrameSchedulerContract(SystemRegistry registry, ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine, IBattleEventBus? battleEventBus) {
}
internal static void WireFrameSchedulerContract(SystemRegistry registry, ComponentStore store, int playerId) {
}
internal static void BindFrameSchedulerContract(SystemRegistry registry, FrameScheduler scheduler) {
}
}
}
