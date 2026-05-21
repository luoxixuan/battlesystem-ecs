using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    public class MockRenderer : IRenderer
    {
        public List<string> Logs { get; } = new List<string>();

        public void Log(string message) => Logs.Add(message);
        public void LogBattle(string message) => Logs.Add(message);
        public void LogDamage(string attacker, string defender, float damage, bool isCritical)
            => Logs.Add($"[DAMAGE] {attacker} -> {defender}: {damage}");
        public void LogDeath(string entity) => Logs.Add($"[DEATH] {entity}");
        public void LogWin(string winner) => Logs.Add($"[WIN] {winner}");
        public void LogBattleStart(string battleName) => Logs.Add($"[BATTLE] {battleName}");
        public void LogTurn(int turn) => Logs.Add($"[TURN] {turn}");

        public bool HasLogContaining(string substring)
        {
            foreach (var log in Logs) if (log.Contains(substring)) return true;
            return false;
        }
    }
}
