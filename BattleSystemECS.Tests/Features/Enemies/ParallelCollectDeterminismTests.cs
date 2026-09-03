using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Features.Enemies
{
    public sealed class ParallelCollectDeterminismTests : BattleTestBase
    {
        [Fact]
        public void BurrowEmergeUsesStableActiveIndexCollectionAcrossRounds()
        {
            // Bug 回归：多个敌人同帧出土时不得向共享 ConcurrentBag 写入或无序结算。
            int playerId=Player(p=>p.Health=100000f);
            const int sourceCount=32;
            var sources=new int[sourceCount];
            for(int i=0;i<sourceCount;i++)
                sources[i]=Enemy(e=>{e.X=0f;e.Y=0f;e.Health=10000f;e.MaxHealth=10000f;});
            int target=Enemy(e=>{e.X=0f;e.Y=0f;e.Health=10000f;e.MaxHealth=10000f;});
            var system=new EnemyBurrowSystem(Store,playerId);

            for(int round=0;round<8;round++)
            {
                for(int i=0;i<sources.Length;i++)system.TriggerBurrow(sources[i],0.01f,1f,2f,0f);
                system.Update(0.02f);
                system.ApplyBurrowEffects();
                // Standalone system test must consume committed gameplay facts between rounds;
                // production FrameScheduler performs this boundary each frame.
                Store.DamageResolver.Events.Clear();
                Assert.Equal(10000f-(round+1)*sourceCount,Store.EnemyHealth[target]);
                for(int i=0;i<sources.Length;i++)
                    Assert.Equal(10000f-(round+1)*(sourceCount-1),Store.EnemyHealth[sources[i]]);
            }
        }

        [Fact]
        public void EnemyAiParallelAttackAndLifestealCommitInStableEnemyOrder()
        {
            // Bug 回归：并行 AI collect 后必须串行写玩家 HP，并按稳定敌人顺序应用吸血。
            int playerId=Player(p=>{p.Health=100000f;p.X=0f;p.Y=0f;});
            const int enemyCount=600;
            var enemies=new int[enemyCount];
            for(int i=0;i<enemyCount;i++)
            {
                int enemyId=Enemy(e=>{e.X=0f;e.Y=0f;e.Health=50f;e.MaxHealth=100f;e.Damage=1f;});
                enemies[i]=enemyId;
                Store.EnemyLifestealActive[enemyId]=true;
                Store.EnemyLifestealRatio[enemyId]=0.5f;
                Store.EnemyLifestealCap[enemyId]=1f;
            }
            var ability=new EnemyAbilitySystem(Store,Renderer,playerId,Config);
            var system=new EnemyAISystem(Store,Renderer,playerId,Config,ability);

            system.SetTurn(1,0.016f);
            system.Update();

            Assert.Equal(100000f-enemyCount,Store.PlayerCurrentHealth[playerId]);
            for(int i=0;i<enemies.Length;i++)Assert.Equal(50.5f,Store.EnemyHealth[enemies[i]]);
        }

        [Fact]
        public void EnemyAiDecoyExpiryQueuesStableDeathsExactlyOnce()
        {
            // Bug 回归：并行 decoy 到期只能收集 death facts，屏障后稳定进入唯一死亡队列。
            int playerId=Player();
            const int enemyCount=600;
            int callbackCount=0;
            Store.OnEnemyKilled+=(enemyId,killerId)=>callbackCount++;
            for(int i=0;i<enemyCount;i++)
            {
                int enemyId=Enemy(e=>{e.Health=1f;e.MaxHealth=1f;e.GoldReward=0;});
                Store.EnemyIsDecoy[enemyId]=true;
                Store.EnemyDecoyLifetimeLeft[enemyId]=0.001f;
            }
            var ability=new EnemyAbilitySystem(Store,Renderer,playerId,Config);
            var system=new EnemyAISystem(Store,Renderer,playerId,Config,ability);

            system.SetTurn(1,0.016f);
            system.Update();
            Store.ResolveEnemiesKilledThisFrame();
            Store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(0,Store.GetActiveEnemyCount());
            Assert.Equal(enemyCount,callbackCount);
            Assert.Equal(enemyCount,Store.TotalKills);
            Assert.Equal(0f,Store.GetPlayerGold(playerId));
        }

        [Fact]
        public void EnemyAiSequentialDecoyExpiryUsesTheSameDeathFactsBoundary()
        {
            // Bug 回归：小规模顺序分支也必须递减 decoy lifetime，并经同一串行死亡提交。
            int playerId=Player();
            const int enemyCount=8;
            int callbackCount=0;
            Store.OnEnemyKilled+=(enemyId,killerId)=>callbackCount++;
            for(int i=0;i<enemyCount;i++)
            {
                int enemyId=Enemy(e=>{e.Health=1f;e.MaxHealth=1f;e.GoldReward=0;});
                Store.EnemyIsDecoy[enemyId]=true;
                Store.EnemyDecoyLifetimeLeft[enemyId]=0.001f;
            }
            var ability=new EnemyAbilitySystem(Store,Renderer,playerId,Config);
            var system=new EnemyAISystem(Store,Renderer,playerId,Config,ability);
            system.SetTurn(1,0.016f);
            system.Update();
            Store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(0,Store.GetActiveEnemyCount());
            Assert.Equal(enemyCount,callbackCount);
            Assert.Equal(enemyCount,Store.TotalKills);
            Assert.Equal(0f,Store.GetPlayerGold(playerId));
        }

        [Fact]
        public void LifeLinkCandidatesMergeByActiveIndexAcrossRounds()
        {
            // Bug 回归：多个 LifeLinker 并行选择目标时必须稳定建立相同配对。
            Player();
            Config.LifeLinkDefs=new[]{new LifeLinkDef{LifeLinkId="test",MaxLinks=1,
                DamageShareRatio=0.25f,LinkRange=2f,LinkCooldown=0f}};
            const int pairCount=32;
            var linkers=new int[pairCount];
            var targets=new int[pairCount];
            for(int i=0;i<pairCount;i++)
            {
                float x=i*10f;
                linkers[i]=Enemy(e=>{e.X=x;e.Y=0f;});
                targets[i]=Enemy(e=>{e.X=x+0.5f;e.Y=0f;});
                Store.EnemyIsLifeLinker[linkers[i]]=true;
                Store.EnemyLifeLinkDefId[linkers[i]]=0;
            }
            var system=new EnemyLifeLinkSystem(Store,Config);

            for(int round=0;round<8;round++)
            {
                system.SetTurn(round);
                system.Update();
                for(int i=0;i<pairCount;i++)
                {
                    Assert.Equal(targets[i],Store.EnemyLinkedEnemyId[linkers[i]]);
                    Assert.Equal(linkers[i],Store.EnemyLinkedEnemyId[targets[i]]);
                    system.ClearLink(linkers[i]);
                    Store.EnemyLifeLinkDefId[linkers[i]]=0;
                    Store.EnemyLifeLinkCooldownLeft[linkers[i]]=0f;
                }
            }
        }

        [Fact]
        public void SuicideExplosionsDrainByActiveIndexAcrossRounds()
        {
            // Bug 回归：同塔多自爆敌不得用共享 bag 或共享随机数决定提交顺序。
            int playerId=Player(p=>p.Health=100000f);
            int towerId=RawTower(0,0,damage:0f);
            const int enemyCount=64;
            for(int i=0;i<enemyCount;i++)
            {
                int enemyId=Enemy(e=>{e.X=0f;e.Y=0f;e.Health=1000f;e.MaxHealth=1000f;});
                Store.EnemyIsSuicide[enemyId]=true;
                Store.EnemySuicideTriggerRange[enemyId]=1f;
                Store.EnemySuicideDmgRadius[enemyId]=0.1f;
                Store.EnemySuicideDmgAmount[enemyId]=10f;
            }
            Assert.True(Store.TowerActive[towerId]);
            var system=new SuicideBombSystem(Store,playerId);
            system.SetTurn(7);
            system.Update();

            Assert.Equal(100000f-enemyCount*10f,Store.PlayerCurrentHealth[playerId]);
            Store.ResolveEnemiesKilledThisFrame();
            Assert.Equal(0,Store.GetActiveEnemyCount());
        }

        [Fact]
        public void BleedAndFrostbitePreparedEventsDrainInEnemyIndexOrder()
        {
            // Bug 回归：DoT tick collect 必须按敌人槽独占写入并在屏障后稳定提交。
            int playerId=Player();
            const int enemyCount=1024;
            var enemies=new int[enemyCount];
            var bleed=new BleedSystem(Store,playerId);
            var frostbite=new FrostbiteSystem(Store,playerId);
            for(int i=0;i<enemyCount;i++)
            {
                int enemyId=Enemy(e=>{e.Health=1000f;e.MaxHealth=1000f;});
                enemies[i]=enemyId;
                Store.EnemyBleedMaxStacks[enemyId]=10f;
                bleed.ApplyBleedFromTower(0,enemyId,1f,0.001f,10f);
                frostbite.ApplyFrostbite(enemyId,0.001f,10f);
            }

            bleed.Update(1f);bleed.ResolveBleedDamage();
            frostbite.Update(1f);frostbite.ResolveFrostbiteDamage();

            for(int i=0;i<enemies.Length;i++)Assert.Equal(998f,Store.EnemyHealth[enemies[i]]);
        }

        [Fact]
        public void HeroAndProtectorCollectorsUseStableOwnerBuffers()
        {
            // Bug 回归：Hero/Protector 的 Parallel.For 不得 lock append 或无序枚举共享队列。
            int playerId=Player(p=>p.Health=10000f);
            var heroTargets=new int[ComponentStore.MAX_HEROES];
            var hero=new HeroSystem(Store,playerId);
            for(int i=0;i<ComponentStore.MAX_HEROES;i++)
            {
                float x=i*2f;
                heroTargets[i]=Enemy(e=>{e.X=x;e.Y=0f;e.Health=100f;e.MaxHealth=100f;});
                Store.HeroIsDeployed[i]=true;
                Store.HeroPosX[i]=x;Store.HeroPosY[i]=0f;
                Store.HeroTargetX[i]=x;Store.HeroTargetY[i]=0f;
                Store.HeroAttackRange[i]=1;Store.HeroAttackSpeed[i]=1f;
                Store.HeroDamage[i]=i+1;Store.HeroCooldown[i]=0f;
            }
            RebuildGrid();
            hero.Update(0.016f);
            for(int i=0;i<heroTargets.Length;i++)Assert.Equal(100f-(i+1),Store.EnemyHealth[heroTargets[i]]);

            var protector=new ProtectorSystem(Store,playerId);
            var protectors=new int[4];
            for(int i=0;i<protectors.Length;i++)
            {
                float x=200f+i*20f;
                protectors[i]=Enemy(e=>{e.X=x;e.Y=0f;e.Health=100f;e.MaxHealth=100f;});
                int ally=Enemy(e=>{e.X=x+0.5f;e.Y=0f;e.Health=100f;e.MaxHealth=100f;});
                Store.EnemyIsProtector[protectors[i]]=true;
                Store.EnemyProtectRadius[protectors[i]]=2f;
                Store.EnemyProtectDamageTransfer[protectors[i]]=0.5f;
                Store.EnemyProtectMaxTargets[protectors[i]]=1;
                Store.EnemyProtectDamageTransfer[ally]=0.5f;
            }
            for(int round=0;round<4;round++)
            {
                protector.SetTurn(round);
                protector.Update();
                for(int i=0;i<protectors.Length;i++)Assert.Equal(100f-(round+1)*5f,Store.EnemyHealth[protectors[i]]);
            }
        }

        [Fact]
        public void TurnBasedTimersIgnoreDeltaMagnitude()
        {
            // Bug 回归：burrow/fear/channel 配置单位是 turn，0.016 不得把持续时间放大约 60 倍。
            int playerId=Player();
            int burrowEnemy=Enemy();
            var burrow=new EnemyBurrowSystem(Store,playerId);
            burrow.TriggerBurrow(burrowEnemy,2f,0f,0f,0f);
            burrow.Update(0.016f);
            Assert.Equal(1f,Store.EnemyBurrowTimer[burrowEnemy]);
            burrow.TriggerBurrow(burrowEnemy,2f,0f,0f,0f);
            burrow.Update(1f);
            Assert.Equal(1f,Store.EnemyBurrowTimer[burrowEnemy]);

            int fearEnemy=Enemy();
            var fear=new FearSystem(Store,playerId);
            Store.EnemyIsFeared[fearEnemy]=true;
            Store.EnemyFearDurationLeft[fearEnemy]=2f;
            fear.Update(0.016f);
            Assert.Equal(1f,Store.EnemyFearDurationLeft[fearEnemy]);
            Store.EnemyFearDurationLeft[fearEnemy]=2f;
            fear.Update(1f);
            Assert.Equal(1f,Store.EnemyFearDurationLeft[fearEnemy]);

            Config.EnemyAbilities=new System.Collections.Generic.List<EnemyAbilityDef>{new EnemyAbilityDef
            {Id="turn-cast",Name="turn-cast",AbilityType="self_heal",CastTime=2f,HealAmount=0f}};
            var ability=new EnemyAbilitySystem(Store,Renderer,playerId,Config);
            int channelA=Enemy();
            ability.EnqueueAbility(channelA,"turn-cast");
            ability.TickCastTimers(0.016f);
            Assert.Equal(1f,Store.EnemyChannelTimer[channelA]);
            int channelB=Enemy();
            ability.EnqueueAbility(channelB,"turn-cast");
            ability.TickCastTimers(1f);
            Assert.Equal(1f,Store.EnemyChannelTimer[channelB]);
        }
    }
}
