using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    public sealed class FrameGraphCombatBehaviorTests : BattleTestBase
    {
        [Fact]
        public void ManaBurnParallelCollectUsesStableEnemyOrderWithoutSteadyStateAllocation()
        {
            // Bug 回归：共享并发容器会让 current-percent 与 flat 混合扣蓝随调度顺序漂移。
            int playerId=Player();
            Store.PlayerMaxMana[playerId]=1000f;
            var system=new ManaBurnSystem(Store,playerId);
            for(int i=0;i<600;i++)
            {
                int enemyId=Enemy();
                Store.EnemyManaBurnType[enemyId]=(i&1)==0?0:1;
                Store.EnemyManaBurnAmount[enemyId]=(i&1)==0?1f:0.001f;
            }
            float expected=1000f;
            for(int i=0;i<600;i++)expected=(i&1)==0?Math.Max(0f,expected-1f):expected-expected*0.001f;
            for(int run=0;run<16;run++)
            {
                Store.PlayerMana[playerId]=1000f;
                system.Update();
                Assert.Equal(expected,Store.PlayerMana[playerId]);
            }
            Store.PlayerMana[playerId]=1000f;
            long baselineBefore=GC.GetAllocatedBytesForCurrentThread();
            Parallel.For(0,3,ParallelOptionsCache.Capped4,static _=>{});
            long tplBaseline=GC.GetAllocatedBytesForCurrentThread()-baselineBefore;
            long before=GC.GetAllocatedBytesForCurrentThread();
            system.Update();
            long actualAllocation=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.True(actualAllocation<=tplBaseline,
                $"ManaBurn scratch must add no allocation beyond the Parallel.For scheduler baseline. actual={actualAllocation}, baseline={tplBaseline}");
        }

        [Fact]
        public void ManaBurnClearsPreparedSpanAcrossEmptyShrinkAndRegrowFrames()
        {
            // Bug 回归：nonempty→empty→shrink→grow 不得重放旧 active-index 槽中的扣蓝事件。
            int playerId=Player();
            Store.PlayerMaxMana[playerId]=100f;
            Store.PlayerMana[playerId]=100f;
            var system=new ManaBurnSystem(Store,playerId);
            var first=new int[8];
            for(int i=0;i<first.Length;i++)
            {
                first[i]=Enemy();
                Store.EnemyManaBurnAmount[first[i]]=1f;
            }
            system.Update();
            Assert.Equal(92f,Store.PlayerMana[playerId]);
            for(int i=0;i<first.Length;i++)Store.DestroyEntity(first[i]);

            system.Update();
            int quiet=Enemy();
            system.Update();
            Assert.Equal(92f,Store.PlayerMana[playerId]);
            Store.EnemyManaBurnAmount[quiet]=2f;
            for(int i=0;i<15;i++)Enemy();
            system.Update();
            Assert.Equal(90f,Store.PlayerMana[playerId]);
        }

        [Fact]
        public void ReflectPrepareKeepsOriginalAndAuraRequestsExactlyOnce()
        {
            // Bug 回归：ping-pong 翻转不得丢弃原始反伤，也不得重复提交 aura 派生项。
            int playerId=Player();
            int source=Tower(0,0);
            int aura=Tower(1,0);
            int enemy=Enemy(e=>e.Health=100f);
            Store.TowerReflectRatio[source]=0.5f;
            Store.TowerReflectAuraRadius[source]=3f;
            Store.TowerReflectRatio[aura]=0.5f;
            var reflect=new ReflectTowerSystem(Store,playerId);
            reflect.QueueReflect(source,enemy,10f);
            reflect.ResolveReflect();
            reflect.ApplyReflectDamage();
            Assert.Equal(92.5f,Store.EnemyHealth[enemy],3);
            reflect.ApplyReflectDamage();
            Assert.Equal(92.5f,Store.EnemyHealth[enemy],3);
        }

        [Fact]
        public void RegistryWiringIncrementsComboOncePerEnemyKill()
        {
            // Bug 回归：Combo 构造订阅与 Registry 重复订阅会让一次击杀累计两次。
            int playerId=Player();
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine());
            registry.WireDependencies(Store,playerId);
            int enemy=Enemy(e=>e.GoldReward=0);
            float before=Store.PlayerComboCount[playerId];
            Store.QueueEnemyDeath(enemy,playerId);
            Store.ResolveEnemiesKilledThisFrame();
            Assert.Equal(before+1f,Store.PlayerComboCount[playerId]);
        }

        [Fact]
        public void ManyTowersSameEnemyCommitArmorShredAndInvulnerabilityDeterministically()
        {
            // Bug 回归：并行塔不能直接对同一敌人的 shred 做 += 或提前写入本次命中的 I-frame。
            (float health,float shred,float duration,int invuln)? expected=null;
            for(int run=0;run<12;run++)
            {
                using var world=new TestWorld();
                int playerId=world.Player();
                world.DisablePerTypeTowerCapsInstance(playerId);
                int enemy=world.Enemy(e=>e.Health=1000f);
                world.Store.PositionX[enemy]=0f;
                world.Store.PositionY[enemy]=1f;
                world.Store.EnemyInvulnOnHitFrames[enemy]=3;
                var config=new TechTreeConfig
                {
                    branches=new List<TechBranchDef>
                    {
                        new TechBranchDef
                        {
                            id="attack",name="attack",color="red",
                            nodes=new List<TechNodeDef>
                            {
                                new TechNodeDef{id="shred",name="shred",description="shred",cost=0,
                                    prerequisites=new List<string>(),effects=new List<TechEffect>{new TechEffect{type="armor_shred",value=1f}}}
                            }
                        }
                    }
                };
                var tech=new TechTreeSystem(world.Store,world.Renderer,playerId,config,world.Config);
                Assert.True(tech.TryUnlock("shred"));
                for(int i=0;i<16;i++)
                {
                    int tower=world.RawTower(0,0,TowerType.Basic,10f,10,10f);
                    world.Store.TowerArmorShredBonus[tower]=1f;
                }
                world.Store.RebuildSpatialGrid();
                var attack=new TowerAttackSystem(world.Store,world.Renderer,tech);
                attack.SetTurn(0);
                attack.Update(1f);
                var actual=(world.Store.EnemyHealth[enemy],world.Store.EnemyArmorShredStacks[enemy],
                    world.Store.EnemyArmorShredDuration[enemy],world.Store.EnemyInvulnFramesLeft[enemy]);
                expected??=actual;
                Assert.Equal(expected.Value,actual);
                Assert.Equal(840f,actual.Item1,3);
                Assert.Equal(16f,actual.Item2);
                Assert.Equal(4f,actual.Item3);
                Assert.Equal(3,actual.Item4);
            }
        }

        [Fact]
        public void ManyTowersSameEnemyCommitShieldRecentDamageElementsAndVanguardDeterministically()
        {
            // Bug 回归：同一敌人的护盾、伤害窗口、元素状态和前锋转移只能在并行屏障后稳定提交。
            (float targetHealth,float vanguardHealth,float recentDamage,int recentFrame,
                float shield,ElementType elements,float fireTimer,float iceTimer)? expected=null;
            int oldWindow=DamageSaturationConfig.SaturationWindowFrames;
            float oldThreshold=DamageSaturationConfig.SaturationThresholdMult;
            float oldScale=DamageSaturationConfig.SaturationScaleMult;
            try
            {
                DamageSaturationConfig.SaturationWindowFrames=30;
                DamageSaturationConfig.SaturationThresholdMult=100f;
                DamageSaturationConfig.SaturationScaleMult=0.1f;
                for(int run=0;run<12;run++)
                {
                    using var world=new TestWorld();
                    int playerId=world.Player();
                    world.DisablePerTypeTowerCapsInstance(playerId);
                    int vanguard=world.Enemy(e=>{e.X=100f;e.Y=0f;e.Health=1000f;});
                    world.Store.EnemyIsVanguard[vanguard]=true;
                    world.Store.EnemyVanguardCoverRange[vanguard]=-1f;
                    int target=world.Enemy(e=>{e.X=0f;e.Y=1f;e.Health=1000f;});
                    world.Store.EnemyVanguardDmgTransfer[target]=0.25f;
                    world.Store.EnemyHitShieldCount[target]=3f;
                    world.Store.EnemyHitShieldMax[target]=3f;
                    for(int i=0;i<16;i++)
                    {
                        int tower=world.RawTower(0,0,TowerType.Basic,10f,10,10f);
                        int element=(i&1)==0?1:2;
                        float duration=(i&1)==0?5f:7f;
                        world.Store.SetTowerEnchantment(tower,element,0f,duration,-1);
                    }
                    world.Store.RebuildSpatialGrid();
                    var attack=new TowerAttackSystem(world.Store,world.Renderer);
                    attack.SetHitShieldSystem(new HitShieldSystem(world.Store,world.Renderer));
                    attack.SetTurn(0);
                    attack.Update(1f);

                    var actual=(world.Store.EnemyHealth[target],world.Store.EnemyHealth[vanguard],
                        world.Store.EnemyRecentDamageSum[target],world.Store.EnemyRecentDamageFrame[target],
                        world.Store.EnemyHitShieldCount[target],world.Store.EnemyElementStatus[target],
                        world.Store.EnemyElementTimer[target*4],world.Store.EnemyElementTimer[target*4+1]);
                    expected??=actual;
                    Assert.Equal(expected.Value,actual);
                    Assert.Equal(870f,actual.Item1,3);
                    Assert.Equal(967.5f,actual.Item2,3);
                    Assert.Equal(130f,actual.Item3,3);
                    Assert.Equal(world.Store.CurrentFrame,actual.Item4);
                    Assert.Equal(0f,actual.Item5);
                    Assert.Equal(ElementType.Fire|ElementType.Ice,actual.Item6&(ElementType.Fire|ElementType.Ice));
                    Assert.Equal(5f,actual.Item7,3);
                    Assert.Equal(7f,actual.Item8,3);
                }
            }
            finally
            {
                DamageSaturationConfig.SaturationWindowFrames=oldWindow;
                DamageSaturationConfig.SaturationThresholdMult=oldThreshold;
                DamageSaturationConfig.SaturationScaleMult=oldScale;
            }
        }

        [Fact]
        public void EnemyAbilityTelegraphRunsThroughProductionGraphAndPublishesDamageOnce()
        {
            // Bug 回归：EnemyAbility 创建的预警区必须由生产 graph 的 Telegraph 节点结算一次。
            Config.EnemyAbilities=new List<EnemyAbilityDef>
            {
                new EnemyAbilityDef
                {
                    Id="graph_telegraph",Name="graph_telegraph",AbilityType="aoe_damage",
                    AoeRadius=20,DamageMultiplier=2f,TelegraphDuration=1f,Cooldown=5f
                }
            };
            int playerId=Player(p=>{p.Health=100f;p.X=0f;p.Y=0f;});
            int enemyId=Enemy(e=>{e.X=0f;e.Y=10f;e.Damage=10f;});
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine());
            registry.WireDependencies(Store,playerId);
            int published=0;
            float publishedDamage=0f;
            float publishedRemaining=0f;
            registry.EventBus!.PlayerDamaged.Subscribe(evt=>{published++;publishedDamage+=evt.Damage;publishedRemaining=evt.RemainingHealth;});
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.WavePhase;
            registry.EnemyAbility!.EnqueueAbility(enemyId,"graph_telegraph");

            scheduler.Tick(1f,0);

            Assert.Equal(1,published);
            Assert.Equal(Store.EnemyDamage[enemyId]*2f,publishedDamage,3);
            Assert.True(Store.PlayerCurrentHealth[playerId]<100f);
            Assert.Equal(publishedRemaining,Store.PlayerCurrentHealth[playerId],3);
            Assert.Equal(0,registry.Telegraph!.ActiveZoneCount);
            FrameNodeAdapter telegraph=Assert.Single(scheduler.FrameGraphPlan,
                node=>node.Metadata.Id.Value=="spatial.telegraph.update");
            Assert.Contains(FrameResource.TelegraphState,telegraph.Metadata.Writes);
            Assert.Contains(FrameResource.PlayerResources,telegraph.Metadata.Writes);
            Assert.Contains(FrameResource.GameplayEvents,telegraph.Metadata.Writes);
        }

    }
}
