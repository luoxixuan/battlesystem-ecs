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

        // ── 死配置接线：TowerOvercharge / PositionalDamage / SkillDefs 解析（注入 JSON 驱动）──

        [Fact]
        public void ParseTowerOverchargeConfig_InjectedJson_FieldsLoaded()
        {
            var config = new GameConfig();
            // 期望值全部由测试注入的 JSON 推导，不钉任何仓库配置文件数值。
            const string json = "{\"TowerOvercharge\":{\"DamageMultiplier\":3.0,\"AttackSpeedMultiplier\":1.7,\"RangeMultiplier\":1.3,\"Duration\":8.0,\"Cooldown\":40.0,\"ManaCost\":33.0,\"MinManaRequired\":12.0}}";
            GameConfigLoader.ParseTowerOverchargeConfig(config, json);
            Assert.Equal(3.0f, config.TowerOvercharge.DamageMultiplier);
            Assert.Equal(40.0f, config.TowerOvercharge.Cooldown);
            Assert.Equal(33.0f, config.TowerOvercharge.ManaCost);
            Assert.Equal(12.0f, config.TowerOvercharge.MinManaRequired);
        }

        [Fact]
        public void ParsePositionalDamageConfig_MissingEnabledKey_StaysDisabled()
        {
            var config = new GameConfig();
            const string json = "{\"PositionalDamage\":{\"BackstabAngleDegrees\":120.0,\"FlankAngleDegrees\":60.0,\"BackstabDamageMultiplier\":1.5,\"FlankDamageMultiplier\":1.25}}";
            GameConfigLoader.ParsePositionalDamageConfig(config, json);
            // JSON 未提供 Enabled 键 → 默认关闭：接线后零行为变化的核心契约。
            Assert.False(config.PositionalDamage.Enabled);
            Assert.Equal(120.0f, config.PositionalDamage.BackstabAngleDegrees);
            Assert.Equal(1.25f, config.PositionalDamage.FlankDamageMultiplier);
        }

        [Fact]
        public void ParsePositionalDamageConfig_ExplicitEnabled_FlagHonored()
        {
            var config = new GameConfig();
            const string json = "{\"PositionalDamage\":{\"Enabled\":true,\"BackstabAngleDegrees\":90.0,\"FlankAngleDegrees\":45.0,\"BackstabDamageMultiplier\":2.0,\"FlankDamageMultiplier\":1.5}}";
            GameConfigLoader.ParsePositionalDamageConfig(config, json);
            Assert.True(config.PositionalDamage.Enabled);
        }

        [Fact]
        public void ParseSections_MissingSection_KeepsCodedDefaults()
        {
            var config = new GameConfig();
            GameConfigLoader.ParseTowerOverchargeConfig(config, "{\"OtherSection\":{}}");
            GameConfigLoader.ParsePositionalDamageConfig(config, "{\"OtherSection\":{}}");
            Assert.NotNull(config.TowerOvercharge);
            Assert.False(config.PositionalDamage.Enabled);
            // 段缺失 → 代码默认原样保留（不被清零）
            Assert.True(config.TowerOvercharge.DamageMultiplier > 1f);
            Assert.True(config.TowerOvercharge.Cooldown > 0f);
        }

        [Fact]
        public void ParseSkillDefsArrayJson_DedupsByName_AndLoadsModifiers()
        {
            var config = new GameConfig();
            const string json = "[{\"Name\":\"Cross Slash\",\"AreaShape\":\"cross\",\"Cooldown\":5,\"DamageMultiplier\":4,\"AutoCast\":false,\"Modifiers\":[{\"Name\":\"CrossSlashDamage\",\"Type\":\"Damage\",\"Duration\":0,\"StackingType\":\"None\",\"StackLimitCount\":0,\"Value\":40,\"EffectTag\":\"Normal\"}]},{\"Name\":\"cross slash\",\"Cooldown\":9},{\"Name\":\"Poison Nova\",\"AreaShape\":\"circle\",\"DotDuration\":5,\"DotTickInterval\":1,\"DotDamagePerTick\":8}]";
            int added = GameConfigLoader.ParseSkillDefsArrayJson(config, json);
            // 第二条与第一条同名（忽略大小写）→ 去重跳过
            Assert.Equal(2, added);
            Assert.Equal(2, config.SkillDefs.Count);

            var cross = config.SkillDefs[0];
            Assert.Equal("cross", cross.AreaShape);
            Assert.Equal(5f, cross.Cooldown);
            Assert.Single(cross.Modifiers);
            Assert.Equal("Damage", cross.Modifiers[0].Type);
            Assert.Equal(40f, cross.Modifiers[0].Value);

            // ConeAngleDegrees 键缺失 → 保持 SkillConfig 代码默认（与 new SkillConfig() 一致，不钉字面量）
            Assert.Equal(new SkillConfig().ConeAngleDegrees, config.SkillDefs[1].ConeAngleDegrees);
        }
    }
}
