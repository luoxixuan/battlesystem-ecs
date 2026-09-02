# M7 SystemRegistry Nullable Ledger

Generated from explicit spec `tools/system-registration-spec.json` and verified against `Core/SystemRegistry.cs`.

| property | system kind | dependencies | policy | recipe |
|---|---|---|---|---|
| Map | MapSystem |  | production-service | CreateMap / WireMap / BindMap |
| WaveSpawning | WaveSpawningSystem |  | production-frame-binding | CreateWaveSpawning / WireWaveSpawning / BindWaveSpawning |
| Nest | NestSystem |  | production-frame-binding | CreateNest / WireNest / BindNest |
| Gold | GoldSystem | TechTree | production-frame-binding | CreateGold / WireGold / BindGold |
| Upgrade | UpgradeSystem | TowerUpgrade | production-frame-binding | CreateUpgrade / WireUpgrade / BindUpgrade |
| Interest | InterestSystem |  | production-frame-binding | CreateInterest / WireInterest / BindInterest |
| Skill | SkillSystem | Buff,HealingZone,Mana,Necromancer,TechTree,TimeRewind | production-frame-binding | CreateSkill / WireSkill / BindSkill |
| Buff | BuffSystem |  | production-frame-binding | CreateBuff / WireBuff / BindBuff |
| ElementalReaction | ElementalReactionSystem |  | production-frame-binding | CreateElementalReaction / WireElementalReaction / BindElementalReaction |
| Combo | ComboSystem |  | production-frame-binding | CreateCombo / WireCombo / BindCombo |
| AutoSkill | AutoSkillSystem | Skill | production-frame-binding | CreateAutoSkill / WireAutoSkill / BindAutoSkill |
| Mana | ManaSystem | TechTree | production-frame-binding | CreateMana / WireMana / BindMana |
| ManaShield | ManaShieldSystem |  | production-frame-binding | CreateManaShield / WireManaShield / BindManaShield |
| GlobalSkill | GlobalSkillSystem |  | production-frame-binding | CreateGlobalSkill / WireGlobalSkill / BindGlobalSkill |
| AbilityPayloads | ProductionAbilityPayloadHandler | EnemyAbility,GlobalSkill,HeroSkill,Necromancer,Skill,TimeRewind,TowerActiveSkill | production-service | CreateAbilityPayloads / WireAbilityPayloads / BindAbilityPayloads |
| Mark | MarkSystem |  | production-frame-binding | CreateMark / WireMark / BindMark |
| DeathMark | DeathMarkSystem |  | production-frame-binding | CreateDeathMark / WireDeathMark / BindDeathMark |
| Culling | CullingSystem |  | production-frame-binding | CreateCulling / WireCulling / BindCulling |
| TimeRewind | TimeRewindSnapshotSystem |  | production-frame-binding | CreateTimeRewind / WireTimeRewind / BindTimeRewind |
| TowerPlacement | TowerPlacementSystem | TowerModifier | production-service | CreateTowerPlacement / WireTowerPlacement / BindTowerPlacement |
| TowerAttack | TowerAttackSystem | Bleed,Buff,Culling,Desperation,EventBus,FireTrail,HitShield,LifeLink,Projectile,TechTree | production-frame-binding | CreateTowerAttack / WireTowerAttack / BindTowerAttack |
| TowerUpgrade | TowerUpgradeSystem |  | production-service | CreateTowerUpgrade / WireTowerUpgrade / BindTowerUpgrade |
| TowerExperience | TowerExperienceSystem |  | production-service | CreateTowerExperience / WireTowerExperience / BindTowerExperience |
| TowerSynergy | TowerSynergySystem |  | production-frame-binding | CreateTowerSynergy / WireTowerSynergy / BindTowerSynergy |
| TowerFortress | TowerFortressSystem |  | production-frame-binding | CreateTowerFortress / WireTowerFortress / BindTowerFortress |
| KillCooldownReset | KillCooldownResetSystem |  | production-service | CreateKillCooldownReset / WireKillCooldownReset / BindKillCooldownReset |
| HealOnKill | HealOnKillSystem |  | production-service | CreateHealOnKill / WireHealOnKill / BindHealOnKill |
| Bloodlust | BloodlustSystem |  | production-frame-binding | CreateBloodlust / WireBloodlust / BindBloodlust |
| PreFightBuff | PreFightBuffSystem |  | production-frame-binding | CreatePreFightBuff / WirePreFightBuff / BindPreFightBuff |
| Momentum | MomentumSystem |  | production-frame-binding | CreateMomentum / WireMomentum / BindMomentum |
| Adrenaline | AdrenalineSystem |  | production-frame-binding | CreateAdrenaline / WireAdrenaline / BindAdrenaline |
| Crest | CrestSystem |  | production-frame-binding | CreateCrest / WireCrest / BindCrest |
| TowerMorph | TowerMorphSystem |  | production-frame-binding | CreateTowerMorph / WireTowerMorph / BindTowerMorph |
| AuraTower | AuraTowerSystem |  | production-frame-binding | CreateAuraTower / WireAuraTower / BindAuraTower |
| Curse | CurseAuraSystem |  | production-frame-binding | CreateCurse / WireCurse / BindCurse |
| PullTower | PullTowerSystem |  | production-frame-binding | CreatePullTower / WirePullTower / BindPullTower |
| Taunt | TauntSystem |  | production-frame-binding | CreateTaunt / WireTaunt / BindTaunt |
| Bleed | BleedSystem |  | production-frame-binding | CreateBleed / WireBleed / BindBleed |
| Frostbite | FrostbiteSystem |  | production-frame-binding | CreateFrostbite / WireFrostbite / BindFrostbite |
| HealAura | HealAuraSystem |  | production-frame-binding | CreateHealAura / WireHealAura / BindHealAura |
| ThornsAura | ThornsAuraSystem |  | production-frame-binding | CreateThornsAura / WireThornsAura / BindThornsAura |
| Rally | RallySystem | EventBus | production-frame-binding | CreateRally / WireRally / BindRally |
| TowerActiveSkill | TowerActiveSkillSystem |  | production-frame-binding | CreateTowerActiveSkill / WireTowerActiveSkill / BindTowerActiveSkill |
| Aggro | AggroSystem |  | production-frame-binding | CreateAggro / WireAggro / BindAggro |
| EchoClone | EchoCloneSystem |  | production-frame-binding | CreateEchoClone / WireEchoClone / BindEchoClone |
| TowerModifier | TowerModifierSystem |  | production-service | CreateTowerModifier / WireTowerModifier / BindTowerModifier |
| FireTrail | FireTrailSystem |  | production-service | CreateFireTrail / WireFireTrail / BindFireTrail |
| Projectile | ProjectileSystem |  | production-frame-binding | CreateProjectile / WireProjectile / BindProjectile |
| ChronoTower | ChronoTowerSystem |  | production-frame-binding | CreateChronoTower / WireChronoTower / BindChronoTower |
| Mine | MineSystem |  | production-frame-binding | CreateMine / WireMine / BindMine |
| TowerShrine | TowerShrineSystem |  | production-frame-binding | CreateTowerShrine / WireTowerShrine / BindTowerShrine |
| TowerBeacon | TowerBeaconSystem |  | production-frame-binding | CreateTowerBeacon / WireTowerBeacon / BindTowerBeacon |
| PlayerTowerAttack | PlayerTowerAttackSystem | EventBus,HitShield,LifeLink,TechTree | production-frame-binding | CreatePlayerTowerAttack / WirePlayerTowerAttack / BindPlayerTowerAttack |
| Hero | HeroSystem |  | production-frame-binding | CreateHero / WireHero / BindHero |
| HeroSkill | HeroSkillSystem |  | production-frame-binding | CreateHeroSkill / WireHeroSkill / BindHeroSkill |
| EnemyMovement | EnemyMovementSystem | BossTrailAoe,DayNight,Pathfinding,Weather | production-frame-binding | CreateEnemyMovement / WireEnemyMovement / BindEnemyMovement |
| EnemyAI | EnemyAISystem | EnemyAbility,EventBus,TechTree,WaveSpawning | production-frame-binding | CreateEnemyAI / WireEnemyAI / BindEnemyAI |
| EnemyAbility | EnemyAbilitySystem | EventBus,Telegraph | production-frame-binding | CreateEnemyAbility / WireEnemyAbility / BindEnemyAbility |
| EnemyFission | EnemyFissionSystem |  | production-frame-binding | CreateEnemyFission / WireEnemyFission / BindEnemyFission |
| EnemyMorph | EnemyMorphSystem |  | production-frame-binding | CreateEnemyMorph / WireEnemyMorph / BindEnemyMorph |
| EnemyBurrow | EnemyBurrowSystem |  | production-service | CreateEnemyBurrow / WireEnemyBurrow / BindEnemyBurrow |
| Necromancer | NecromancerSystem |  | production-frame-binding | CreateNecromancer / WireNecromancer / BindNecromancer |
| LifeLink | EnemyLifeLinkSystem |  | production-frame-binding | CreateLifeLink / WireLifeLink / BindLifeLink |
| HitShield | HitShieldSystem |  | production-frame-binding | CreateHitShield / WireHitShield / BindHitShield |
| TowerSabotage | TowerSabotageSystem |  | production-frame-binding | CreateTowerSabotage / WireTowerSabotage / BindTowerSabotage |
| ManaBurn | ManaBurnSystem |  | production-frame-binding | CreateManaBurn / WireManaBurn / BindManaBurn |
| Phase | PhaseSystem |  | production-frame-binding | CreatePhase / WirePhase / BindPhase |
| Fear | FearSystem |  | production-frame-binding | CreateFear / WireFear / BindFear |
| EnemyStrafe | EnemyStrafeSystem | TowerAttack | production-frame-binding | CreateEnemyStrafe / WireEnemyStrafe / BindEnemyStrafe |
| SuicideBomb | SuicideBombSystem | ReflectTower,TowerStealth | production-frame-binding | CreateSuicideBomb / WireSuicideBomb / BindSuicideBomb |
| ReflectTower | ReflectTowerSystem |  | production-frame-binding | CreateReflectTower / WireReflectTower / BindReflectTower |
| TowerStealth | TowerStealthSystem |  | production-frame-binding | CreateTowerStealth / WireTowerStealth / BindTowerStealth |
| PathBlock | PathBlockSystem |  | production-frame-binding | CreatePathBlock / WirePathBlock / BindPathBlock |
| Desperation | DesperationSystem |  | production-frame-binding | CreateDesperation / WireDesperation / BindDesperation |
| ShopReroll | ShopRerollSystem |  | production-frame-binding | CreateShopReroll / WireShopReroll / BindShopReroll |
| Terrain | TerrainSystem | Buff | production-frame-binding | CreateTerrain / WireTerrain / BindTerrain |
| Pathfinding | PathfindingSystem |  | production-frame-binding | CreatePathfinding / WirePathfinding / BindPathfinding |
| DeployableTrap | DeployableTrapSystem |  | production-frame-binding | CreateDeployableTrap / WireDeployableTrap / BindDeployableTrap |
| PathModifier | PathModifierSystem |  | production-frame-binding | CreatePathModifier / WirePathModifier / BindPathModifier |
| Pull | PullSystem |  | production-frame-binding | CreatePull / WirePull / BindPull |
| Weather | WeatherSystem | TowerAttack | production-frame-binding | CreateWeather / WireWeather / BindWeather |
| DayNight | DayNightSystem | TowerAttack | production-frame-binding | CreateDayNight / WireDayNight / BindDayNight |
| WaveMutator | WaveMutatorSystem |  | production-frame-binding | CreateWaveMutator / WireWaveMutator / BindWaveMutator |
| RandomEvent | RandomEventSystem |  | production-frame-binding | CreateRandomEvent / WireRandomEvent / BindRandomEvent |
| Telegraph | TelegraphSystem | EventBus | production-frame-binding | CreateTelegraph / WireTelegraph / BindTelegraph |
| AdaptiveDifficulty | AdaptiveDifficultySystem | WaveSpawning | production-frame-binding | CreateAdaptiveDifficulty / WireAdaptiveDifficulty / BindAdaptiveDifficulty |
| CorpseEffect | CorpseEffectSystem | Buff | production-frame-binding | CreateCorpseEffect / WireCorpseEffect / BindCorpseEffect |
| HealingZone | HealingZoneSystem |  | production-frame-binding | CreateHealingZone / WireHealingZone / BindHealingZone |
| ZoneControl | ZoneControlSystem |  | production-frame-binding | CreateZoneControl / WireZoneControl / BindZoneControl |
| Objective | ObjectiveSystem | EventBus | production-frame-binding | CreateObjective / WireObjective / BindObjective |
| WaveBranch | WaveBranchSystem |  | production-frame-binding | CreateWaveBranch / WireWaveBranch / BindWaveBranch |
| ResourceNode | ResourceNodeSystem |  | production-frame-binding | CreateResourceNode / WireResourceNode / BindResourceNode |
| WavePreview | WavePreviewSystem |  | production-service | CreateWavePreview / WireWavePreview / BindWavePreview |
| DoomClock | DoomClockSystem |  | production-frame-binding | CreateDoomClock / WireDoomClock / BindDoomClock |
| SoulHarvest | SoulHarvestSystem |  | production-frame-binding | CreateSoulHarvest / WireSoulHarvest / BindSoulHarvest |
| Replay | ReplaySystem |  | production-service | CreateReplay / WireReplay / BindReplay |
| HotZone | HotZoneSystem |  | production-frame-binding | CreateHotZone / WireHotZone / BindHotZone |
| TerrainZone | TerrainZoneSystem |  | production-frame-binding | CreateTerrainZone / WireTerrainZone / BindTerrainZone |
| FrostZone | FrostZoneSystem |  | production-frame-binding | CreateFrostZone / WireFrostZone / BindFrostZone |
| WanderRoam | WanderRoamSystem |  | production-frame-binding | CreateWanderRoam / WireWanderRoam / BindWanderRoam |
| Magnetize | MagnetizeSystem |  | production-frame-binding | CreateMagnetize / WireMagnetize / BindMagnetize |
| Sapper | SapperSystem |  | production-frame-binding | CreateSapper / WireSapper / BindSapper |
| Wisp | WispSystem |  | production-frame-binding | CreateWisp / WireWisp / BindWisp |
| BossTrailAoe | BossTrailAoeSystem |  | production-service | CreateBossTrailAoe / WireBossTrailAoe / BindBossTrailAoe |
| TechTree | TechTreeSystem |  | production-service | CreateTechTree / WireTechTree / BindTechTree |
| Pickup | PickupSystem |  | production-frame-binding | CreatePickup / WirePickup / BindPickup |
| Inventory | InventorySystem |  | production-service | CreateInventory / WireInventory / BindInventory |
| Ascension | AscensionSystem | WaveSpawning | production-service | CreateAscension / WireAscension / BindAscension |
| Save | SaveSystem |  | production-service | CreateSave / WireSave / BindSave |
| EventBus | EventBus |  | production-service | CreateEventBus / WireEventBus / BindEventBus |
| ProductionEvents | composition | Combo,Crest,Culling,Gold,Interest,Momentum,PlayerTowerAttack,PreFightBuff,Save,Skill,TechTree,TowerAttack,WaveBranch,WaveMutator,WavePreview,WaveSpawning | production-event-wiring | CreateProductionEvents / WireProductionEvents / BindProductionEvents |
| FrameScheduler | composition |  | production-frame-orchestration | CreateFrameSchedulerContract / WireFrameSchedulerContract / BindFrameSchedulerContract |
| TowerIncome | disabled |  | feature-disabled | disabled: no executable recipe |
| TowerRelocate | disabled |  | feature-disabled | disabled: no executable recipe |
| Construction | disabled |  | feature-disabled | disabled: no executable recipe |
| EnemyAffix | disabled |  | feature-disabled | disabled: no executable recipe |
| Wound | disabled |  | feature-disabled | disabled: no executable recipe |
| EnemyHealer | disabled |  | feature-disabled | disabled: no executable recipe |
| StealGold | disabled |  | feature-disabled | disabled: no executable recipe |
| Summon | disabled |  | feature-disabled | disabled: no executable recipe |
| TowerOvercharge | disabled |  | feature-disabled | disabled: no executable recipe |
| TowerLink | disabled |  | feature-disabled | disabled: no executable recipe |
| PatrolTower | disabled |  | feature-disabled | disabled: no executable recipe |
| Fog | disabled |  | feature-disabled | disabled: no executable recipe |
| PointDefense | disabled |  | feature-disabled | disabled: no executable recipe |
| Heat | disabled |  | feature-disabled | disabled: no executable recipe |
| Demolish | disabled |  | feature-disabled | disabled: no executable recipe |
| TowerSilence | disabled |  | feature-disabled | disabled: no executable recipe |
| Dispel | disabled |  | feature-disabled | disabled: no executable recipe |
| EnemyProjectile | disabled |  | feature-disabled | disabled: no executable recipe |
