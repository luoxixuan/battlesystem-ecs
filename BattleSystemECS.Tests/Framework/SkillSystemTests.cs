using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Framework
{
    public class SkillSystemTests : BattleTestBase
    {
        /// <summary>
        /// 创建默认玩家并初始化技能，返回玩家 id。
        /// </summary>
        private int CreatePlayerAndSkills(out SkillSystem sys)
        {
            int pid = Player(p =>
            {
                p.X = 5f;
                p.Y = 0f;
                p.Health = 200f;
                p.AttackDamage = 10f;
                p.AttackRange = 3f;
            });
            sys = new SkillSystem(Store, Renderer, pid, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.InitializePlayerSkills();
            return pid;
        }

        private int MakeEnemy(float x, float y, float hp = 10f, int gold = 10)
            => Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.Health = hp;
                e.MaxHealth = hp;
                e.GoldReward = gold;
            });

        [Fact]
        public void InitializePlayerSkills_RegistersEveryConfiguredSkill()
        {
            int pid = Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; });
            var sys = new SkillSystem(Store, Renderer, pid, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.InitializePlayerSkills();

            // 注册数量与槽位名称完全由读取到的配置推导，不钉住 3 个技能的字面量。
            Assert.Equal(Config.Skills.Count, Store.AbilityCount[pid]);
            for (int i = 0; i < Config.Skills.Count; i++)
            {
                Assert.Equal(Config.Skills[i].Name, Store.GetAbility(pid, i).Definition.Name);
            }
        }

        // ─── Bug#9 回归：InitializePlayerSkills 不累计 AbilityCount ────────────────

        [Fact]
        public void InitializePlayerSkills_Idempotent_AbilityCount()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            int first = Store.AbilityCount[pid];
            Assert.Equal(Config.Skills.Count, first);
            sys.InitializePlayerSkills();
            Assert.Equal(first, Store.AbilityCount[pid]);
        }

        // ─── 回归：InitializePlayerSkills 重复调用不得累计 ActiveEffectCount ───────

        [Fact]
        public void InitializePlayerSkills_Idempotent_ActiveEffectCount()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            int first = Store.GetEffectCount(pid);
            // 实现固定注入 2 个 buff 效果（Attack+10% / Crit Rate+5%），
            // 重复初始化必须重置为同一数量而不是 4 / 6 地累加。
            Assert.Equal(2, first);
            sys.InitializePlayerSkills();
            Assert.Equal(first, Store.GetEffectCount(pid));
        }

        // ─── Bug#37 回归：AutoCastBestSkill 走 epsilon 边界 ─────────────────────────

        [Fact]
        public void AutoCastBestSkill_FiresWhenCooldownBelowEpsilon()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            float expectedCooldown = Config.Skills[0].Cooldown;

            // 第一次自动施放：冷却从 0 变为配置冷却值 = 确凿的施放证据。
            sys.AutoCastBestSkill();
            Assert.Equal(expectedCooldown, Store.GetAbility(pid, 0).CurrentCooldown, 5);

            // 残余冷却低于 epsilon 时，应能再次施放并重新进入完整冷却。
            var slot = Store.GetAbility(pid, 0);
            slot.CurrentCooldown = 0.00005f;
            Store.SetAbility(pid, 0, slot);

            sys.AutoCastBestSkill();
            Assert.Equal(expectedCooldown, Store.GetAbility(pid, 0).CurrentCooldown, 5);
        }

        [Fact]
        public void AutoCastBestSkill_DoesNotFireWhenCooldownAboveEpsilon()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            const float injectedCooldown = 1.0f; // 测试显式注入，高于 epsilon

            // 所有技能槽都在冷却中 → 自动施放必须全部跳过。
            for (int slot = 0; slot < Config.Skills.Count; slot++)
            {
                var inst = Store.GetAbility(pid, slot);
                inst.CurrentCooldown = injectedCooldown;
                Store.SetAbility(pid, slot, inst);
            }

            sys.AutoCastBestSkill();

            // 状态断言：冷却值原封不动 = 确实没有施放任何技能。
            for (int slot = 0; slot < Config.Skills.Count; slot++)
            {
                Assert.Equal(injectedCooldown, Store.GetAbility(pid, slot).CurrentCooldown, 5);
            }
        }

        [Fact]
        public void AutoCastBestSkill_CastsFirstReadySkill()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            float expectedCooldown = Config.Skills[0].Cooldown;

            sys.AutoCastBestSkill();

            // 第一个配置技能（槽 0）就绪 → 施放后进入完整冷却。
            Assert.Equal(expectedCooldown, Store.GetAbility(pid, 0).CurrentCooldown, 5);
        }

        [Fact]
        public void Update_ReducesCooldown()
        {
            int pid = CreatePlayerAndSkills(out var sys);
            const float injectedCooldown = 2f; // 测试显式注入
            const float injectedDelta = 1f;

            var slot = Store.GetAbility(pid, 0);
            slot.CurrentCooldown = injectedCooldown;
            Store.SetAbility(pid, 0, slot);

            sys.Update(injectedDelta);

            // 无 CDR / Adrenaline 加成时按 1.0 速率衰减：2s - 1s = 1s。
            float expected = injectedCooldown - injectedDelta;
            Assert.Equal(expected, Store.GetAbility(pid, 0).CurrentCooldown, 5);
        }

        [Fact]
        public void SkillCanDamageAndKill()
        {
            int pid = Player(p =>
            {
                p.X = 5f;
                p.Y = 0f;
                p.Health = 200f;
                p.AttackDamage = 10f;
                p.AttackRange = 3f;
            });

            // 期望值全部来自测试显式注入。
            const float injectedEnemyHealth = 10f;
            const int injectedGoldReward = 3;
            int eid = MakeEnemy(5f, 0f, hp: injectedEnemyHealth, gold: injectedGoldReward);

            var sys = new SkillSystem(Store, Renderer, pid, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.InitializePlayerSkills();
            sys.SetTurn(0); // required: populates _activeEnemyList before any Cast call

            string skillName = Config.Skills[0].Name;
            float skillMultiplier = Config.Skills[0].DamageMultiplier;
            float expectedDamage = Store.GetPlayerAttackDamage(pid) * skillMultiplier;

            sys.CastSkill(skillName);
            sys.ResolveSkillDamage(); // serial-phase damage application

            // 直接击杀证据：结算后血量为 注入血量 - 推导伤害（负值）。
            Assert.Equal(0f, Store.EnemyHealth[eid], 3);
            Assert.True(Store.EnemyHealth[eid] <= 0f, "Cross Slash 应直接把敌人打入死亡状态");

            Store.ResolveEnemiesKilledThisFrame(); // frame-end death resolution

            // 帧末结算后的状态：敌人移出活跃列表、击杀计数 +1、金币等于注入的奖励值。
            Assert.DoesNotContain(eid, Store.ActiveEnemyIds);
            Assert.Equal(1, Store.TotalKills);
            Assert.Equal((float)injectedGoldReward, Store.GetPlayerGold(pid), 3);
        }
    }
}
