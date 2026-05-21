using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
{
    public class GameConfigLoaderTests
    {
        private GameConfig Config() => GameConfigLoader.GetDefaultConfig();

        [Fact] public void DefaultConfig_HasPlayerMaxHealth()
            => Assert.True(Config().Player.MaxHealth > 0);

        [Fact] public void DefaultConfig_HasAttackInterval()
            => Assert.True(Config().Player.AttackInterval > 0);

        [Fact] public void DefaultConfig_HasSkills()
            => Assert.NotEmpty(Config().Skills);

        [Fact] public void DefaultConfig_HasLevels()
            => Assert.NotEmpty(Config().Levels);

        [Fact] public void DefaultConfig_HasMonsterTypes()
            => Assert.NotEmpty(Config().MonsterTypes);

        [Fact] public void DefaultConfig_MonstersHaveHealth()
        {
            foreach (var m in Config().MonsterTypes)
                Assert.True(m.Health > 0, $"Monster {m.Name} must have Health > 0");
        }

        [Fact] public void DefaultConfig_PlayerHasStartingSkills()
            => Assert.NotEmpty(Config().Player.StartingSkills);
    }
}
