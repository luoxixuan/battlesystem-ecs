using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests.Integration
{
    /// <summary>
    /// 集成层：读取真实 game_config.json 作为输入，但只断言结构自洽与相对关系
    /// （引用存在、唯一性、字段范围），不钉住任何具体配置值。
    /// 渲染器与实体存储复用 <see cref="BattleTestBase"/> 的 Renderer / Store。
    /// </summary>
    public class GameConfigIntegrationTests : BattleTestBase
    {

        [Fact]
        public void LoadConfig_FromRealFile_Succeeds()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            Assert.NotNull(config);
            Assert.NotEmpty(config.MonsterTypes);
            Assert.NotEmpty(config.Levels);
        }

        [Fact]
        public void MonsterTypes_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            foreach (var monster in config.MonsterTypes)
            {
                Assert.False(string.IsNullOrEmpty(monster.Name), $"Monster missing name: {monster.Name}");
                Assert.False(string.IsNullOrEmpty(monster.Type), $"Monster {monster.Name} missing Type");
                Assert.True(monster.Health > 0, $"Monster {monster.Name} has invalid Health: {monster.Health}");
                Assert.True(monster.MaxHealth > 0, $"Monster {monster.Name} has invalid MaxHealth: {monster.MaxHealth}");
                Assert.True(monster.Damage >= 0, $"Monster {monster.Name} has invalid Damage: {monster.Damage}");
                Assert.True(monster.MoveSpeed >= 0, $"Monster {monster.Name} has invalid MoveSpeed: {monster.MoveSpeed}");
                Assert.True(monster.AttackRange >= 0, $"Monster {monster.Name} has invalid AttackRange: {monster.AttackRange}");
                Assert.True(monster.AttackInterval >= 0, $"Monster {monster.Name} has invalid AttackInterval: {monster.AttackInterval}");
                Assert.True(monster.GoldReward >= 0, $"Monster {monster.Name} has invalid GoldReward: {monster.GoldReward}");
                Assert.NotNull(monster.Skills);
            }
        }

        [Fact]
        public void TowerTypes_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            foreach (var tower in config.TowerTypes)
            {
                Assert.False(string.IsNullOrEmpty(tower.Name), $"Tower missing Name");
                Assert.True(tower.Damage > 0, $"Tower {tower.Name} has invalid Damage: {tower.Damage}");
                Assert.True(tower.Range >= 0, $"Tower {tower.Name} has invalid Range: {tower.Range}");
                Assert.True(tower.AttackSpeed > 0, $"Tower {tower.Name} has invalid AttackSpeed: {tower.AttackSpeed}");
                Assert.True(tower.Cost > 0, $"Tower {tower.Name} has invalid Cost: {tower.Cost}");
                Assert.True(tower.UpgradeCost > 0, $"Tower {tower.Name} has invalid UpgradeCost: {tower.UpgradeCost}");
            }
        }

        [Fact]
        public void Skills_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            foreach (var skill in config.Skills)
            {
                Assert.False(string.IsNullOrEmpty(skill.Name), $"Skill missing Name");
                Assert.True(skill.DamageMultiplier >= 0, $"Skill {skill.Name} has invalid DamageMultiplier: {skill.DamageMultiplier}");
                Assert.True(skill.Cooldown >= 0, $"Skill {skill.Name} has invalid Cooldown: {skill.Cooldown}");
                Assert.True(skill.AreaWidth >= 1, $"Skill {skill.Name} has invalid AreaWidth: {skill.AreaWidth}");
                Assert.True(skill.AreaHeight >= 1, $"Skill {skill.Name} has invalid AreaHeight: {skill.AreaHeight}");
                Assert.True(skill.AttackRange >= 0, $"Skill {skill.Name} has invalid AttackRange: {skill.AttackRange}");
            }
        }

        [Fact]
        public void LevelWave_MonsterType_ExistsInMonsterTypes()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            foreach (var level in config.Levels)
            {
                Assert.NotNull(level.Waves);
                foreach (var wave in level.Waves)
                {
                    Assert.False(
                        string.IsNullOrEmpty(wave.MonsterType),
                        $"Level {level.LevelNumber} Wave {wave.WaveNumber} has empty MonsterType"
                    );

                    bool exists = config.MonsterTypes.Any(m => m.Type == wave.MonsterType);
                    Assert.True(exists,
                        $"Level {level.LevelNumber} Wave {wave.WaveNumber} references MonsterType '{wave.MonsterType}' which does not exist in MonsterTypes list");
                }
            }
        }

        [Fact]
        public void AllMonsterNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);
            var duplicates = config.MonsterTypes
                .Select(m => m.Name)
                .GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void AllSkillNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);
            var duplicates = config.Skills
                .Select(s => s.Name)
                .GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void AllTowerNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);
            var duplicates = config.TowerTypes
                .Select(t => t.Name)
                .GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void SpawnEnemies_AllMonsterTypes_MatchesConfigCount()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            int playerId = Store.CreateEntity();
            Store.PlayerMaxHealth[playerId] = 200f;
            Store.PlayerCurrentHealth[playerId] = 200f;
            Store.SetPlayerGold(playerId, 9999f);

            var random = new Random(777);

            foreach (var monsterType in config.MonsterTypes)
            {
                for (int i = 0; i < 5; i++)
                {
                    float x = random.Next(0, 10);
                    float y = random.Next(1, 20);
                    int id = Store.AddEnemy(
                        x, y,
                        monsterType.MoveSpeed,
                        monsterType.Health,
                        monsterType.MaxHealth,
                        monsterType.Damage,
                        (int)monsterType.AttackRange,
                        (int)monsterType.AttackInterval
                    );
                    Store.SetEntityName(id, $"{monsterType.Type}_{i}");
                    Store.SetEnemyAIAction(id, "");
                }
            }

            // 期望值完全由读取到的配置推导，不钉住 200 之类的固定数量
            Assert.Equal(config.MonsterTypes.Count * 5, Store.GetActiveEnemyCount());
        }

        [Fact]
        public void PlayerSkills_AllExistInSkillConfig()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            // 禁止 null 静默通过：玩家与其起始技能列表必须真实存在。
            Assert.NotNull(config.Player);
            Assert.NotEmpty(config.Player.StartingSkills);

            var skillNames = config.Skills.Select(s => s.Name).ToList();
            foreach (var skillName in config.Player.StartingSkills)
            {
                // Skills can be referenced by exact name or by prefix (e.g. "Circuit Breaker" matches "Circuit Breaker #1")
                bool exists = skillNames.Any(n => n == skillName || n.StartsWith(skillName + " #"));
                Assert.True(exists,
                    $"Player StartingSkill '{skillName}' does not exist in game_config.json Skills list");
            }
        }

        [Fact]
        public void Levels_HaveValidWaveCounts()
        {
            var config = GameConfigLoader.LoadConfig(Renderer);

            foreach (var level in config.Levels)
            {
                Assert.True(level.LevelNumber > 0, $"Level has invalid LevelNumber: {level.LevelNumber}");
                Assert.NotNull(level.Waves);
                Assert.True(level.WaveCount > 0, $"Level {level.LevelNumber} has no waves");
                Assert.Equal(level.WaveCount, level.Waves.Count);

                foreach (var wave in level.Waves)
                {
                    Assert.True(wave.WaveNumber > 0, $"Level {level.LevelNumber} wave has invalid WaveNumber");
                    Assert.True(wave.EnemyCount > 0, $"Level {level.LevelNumber} Wave {wave.WaveNumber} has no enemies");
                }
            }
        }
    }
}
