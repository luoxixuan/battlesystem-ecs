using System;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests.Framework
{
    public class GameConfigLoaderTests : BattleTestBase
    {
        // 命名为 LoadDefaultConfig 而非 Config：基类的 Config 是空 GameConfig，
        // 本类测试的是 GameConfigLoader 从真实 JSON 读出的默认配置，二者语义不同。
        private GameConfig LoadDefaultConfig() => GameConfigLoader.GetDefaultConfig();

        // ── 两个几乎相同的“默认配置字段必须为正”的 [Fact] 合并为 [Theory]。
        [Theory(DisplayName = "默认配置必填字段必须为正")]
        [InlineData("playerMaxHealth")]
        [InlineData("attackInterval")]
        public void DefaultConfig_RequiredFieldsArePositive(string field)
        {
            var config = LoadDefaultConfig();
            switch (field)
            {
                case "playerMaxHealth":
                    Assert.True(config.Player.MaxHealth > 0, "Player.MaxHealth 必须为正");
                    break;
                case "attackInterval":
                    Assert.True(config.Player.AttackInterval > 0, "Player.AttackInterval 必须为正");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, "未知字段");
            }
        }

        // ── 四个“默认配置集合必须非空”的 [Fact] 合并为 [Theory]。
        [Theory(DisplayName = "默认配置必填集合非空")]
        [InlineData("skills")]
        [InlineData("levels")]
        [InlineData("monsterTypes")]
        [InlineData("playerStartingSkills")]
        public void DefaultConfig_RequiredCollectionsAreNotEmpty(string field)
        {
            var config = LoadDefaultConfig();
            switch (field)
            {
                case "skills":
                    Assert.NotEmpty(config.Skills);
                    break;
                case "levels":
                    Assert.NotEmpty(config.Levels);
                    break;
                case "monsterTypes":
                    Assert.NotEmpty(config.MonsterTypes);
                    break;
                case "playerStartingSkills":
                    Assert.NotEmpty(config.Player.StartingSkills);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, "未知字段");
            }
        }

        [Fact]
        public void DefaultConfig_MonstersHaveHealth()
        {
            foreach (var m in LoadDefaultConfig().MonsterTypes)
                Assert.True(m.Health > 0, $"Monster {m.Name} must have Health > 0");
        }
    }
}
