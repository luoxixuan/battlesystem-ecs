using System;
using System.IO;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    public class DebugHallowedTest
    {
        [Fact]
        public void DebugRun()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var store = new ComponentStore();
                int playerId = store.CreateEntity();
                var config = new GameConfig();
                var buff = new BuffSystem(store, playerId, null);
                var sys = new CorpseEffectSystem(store, config, buff, null);
                int zoneId = store.AddCorpseEffect(0f, 0f, 6, 1.5f, 5f, 4f, 1f, 1f);
                sb.AppendLine($"zoneId={zoneId} type={store.CorpseEffectType[zoneId]} dmg={store.CorpseEffectDamagePerTick[zoneId]}");
                int eid = store.CreateEntity();
                store.EnemyActive[eid] = true;
                store.AddActiveEnemyId(eid);
                store.PositionX[eid] = 1f; store.PositionY[eid] = 0f;
                store.SetEnemyHealth(eid, 100f);
                store.EnemyMaxHealth[eid] = 100f;
                int activeCount = store.GetCachedActiveEnemyIds().Count;
                sb.AppendLine($"activeEnemies={activeCount} eid={eid}");
                sys.Update(1.0f);
                int effCount = store.GetEffectCount(eid);
                sb.AppendLine($"after sys.Update effects={effCount} hp={store.EnemyHealth[eid]}");
                if (effCount > 0)
                {
                    var e = store.GetEffect(eid, 0);
                    sb.AppendLine($"effect name='{e.Definition.Name}' type={e.Definition.Type} mag={e.Definition.Magnitude} ticksRem={e.Definition.TicksRemaining} total={e.Definition.TotalTicks} interval={e.Definition.TickInterval} tsl={e.TimeSinceLastTick} remTime={e.Definition.RemainingTime}");
                }
                buff.Update(1.0f);
                sb.AppendLine($"after buff.Update effects={store.GetEffectCount(eid)} hp={store.EnemyHealth[eid]}");
                buff.ResolveDotDamage();
                sb.AppendLine($"FINAL hp={store.EnemyHealth[eid]}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"EX: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "dbg.txt"), sb.ToString());
            // Always pass
            Assert.True(true, sb.ToString());
        }
    }
}
