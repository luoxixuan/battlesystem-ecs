using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 游戏存档系统：波次间存档。
    /// 写入路径：Data/Saves/checkpoint.json（自动创建目录）
    /// </summary>
    public class SaveSystem
    {
        private readonly ComponentStore _store;
        private readonly int _playerId;
        private readonly string _savePath;

        public SaveSystem(ComponentStore store, int playerId, string saveDir = "Data/Saves")
        {
            _store = store;
            _playerId = playerId;
            _savePath = Path.Combine(saveDir, "checkpoint.json");

            string dir = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 序列化当前游戏状态为 JSON 并写入 checkpoint.json。
        /// </summary>
        public void SaveCheckpoint()
        {
            var data = new SaveData
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                PlayerGold = _store.PlayerGold[_playerId],
                PlayerLevel = _store.PlayerCurrentLevel[_playerId],
                PlayerHealth = _store.PlayerCurrentHealth[_playerId],
                PlayerMaxHealth = _store.PlayerMaxHealth[_playerId],
                PlayerShield = _store.PlayerShield[_playerId],
                PlayerBaseLives = _store.PlayerBaseLives[_playerId],
                WaveIndex = _store.PlayerWaveIndex[_playerId],
                EnemiesRemaining = _store.PlayerEnemiesRemaining[_playerId],
                BankedGold = _store.PlayerBankedGold[_playerId],
                InterestRate = _store.PlayerInterestRate[_playerId],
            };

            // Serialize active tower IDs
            data.ActiveTowerIds = new List<int>(_store.ActiveTowerIds);

            // Serialize tower state (position + damage + level)
            data.Towers = new List<TowerSaveData>();
            foreach (var towerId in data.ActiveTowerIds)
            {
                data.Towers.Add(new TowerSaveData
                {
                    TowerId = towerId,
                    X = _store.PositionX[towerId],
                    Y = _store.PositionY[towerId],
                    AttackDamage = _store.TowerAttackDamage[towerId],
                    AttackSpeed = _store.TowerAttackSpeed[towerId],
                    Range = _store.TowerRange[towerId],
                    TowerLevel = _store.TowerLevel[towerId],
                    TowerType = _store.TowerType[towerId],
                    TowerFusionTier = _store.TowerFusionTier[towerId],
                    TowerShieldBreakBonus = _store.TowerShieldBreakBonus[towerId],
                    TowerAccuracy = _store.TowerAccuracy[towerId],
                    TowerProjectileCount = _store.TowerProjectileCount[towerId],
                    TowerScatterAngle = _store.TowerScatterAngle[towerId]
                });
            }

            // Serialize active enemy states
            data.Enemies = new List<EnemySaveData>();
            foreach (var enemyId in _store.GetCachedActiveEnemyIds())
            {
                data.Enemies.Add(new EnemySaveData
                {
                    EnemyId = enemyId,
                    X = _store.PositionX[enemyId],
                    Y = _store.PositionY[enemyId],
                    Health = _store.EnemyHealth[enemyId],
                    MaxHealth = _store.EnemyMaxHealth[enemyId]
                });
            }

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_savePath, json);
        }

        /// <summary>
        /// 反序列化 checkpoint.json 并恢复游戏状态。
        /// </summary>
        /// <returns>true if checkpoint loaded, false if no save file exists</returns>
        public bool LoadCheckpoint()
        {
            if (!File.Exists(_savePath))
                return false;

            try
            {
                string json = File.ReadAllText(_savePath);
                var data = JsonSerializer.Deserialize<SaveData>(json);
                if (data == null)
                    return false;

                // Restore player state
                _store.SetPlayerGold(_playerId, data.PlayerGold);
                _store.PlayerCurrentLevel[_playerId] = data.PlayerLevel;
                _store.PlayerCurrentHealth[_playerId] = data.PlayerHealth;
                _store.PlayerMaxHealth[_playerId] = data.PlayerMaxHealth;
                _store.PlayerShield[_playerId] = data.PlayerShield;
                _store.PlayerBaseLives[_playerId] = data.PlayerBaseLives;
                _store.PlayerWaveIndex[_playerId] = data.WaveIndex;
                _store.PlayerEnemiesRemaining[_playerId] = data.EnemiesRemaining;
                _store.PlayerBankedGold[_playerId] = data.BankedGold;
                _store.PlayerInterestRate[_playerId] = data.InterestRate;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查存档文件是否存在。
        /// </summary>
        public bool HasCheckpoint() => File.Exists(_savePath);

        // ── Serialization models ────────────────────────────────────────

        private class SaveData
        {
            public string Timestamp { get; set; }
            public float PlayerGold { get; set; }
            public int PlayerLevel { get; set; }
            public float PlayerHealth { get; set; }
            public float PlayerMaxHealth { get; set; }
            public float PlayerShield { get; set; }
            public int PlayerBaseLives { get; set; }
            public int WaveIndex { get; set; }
            public int EnemiesRemaining { get; set; }
            public float BankedGold { get; set; }
            public float InterestRate { get; set; }
            public List<int> ActiveTowerIds { get; set; }
            public List<TowerSaveData> Towers { get; set; }
            public List<EnemySaveData> Enemies { get; set; }
        }

        private class TowerSaveData
        {
            public int TowerId { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float AttackDamage { get; set; }
            public float AttackSpeed { get; set; }
            public int Range { get; set; }
            public int TowerLevel { get; set; }
            public string TowerType { get; set; }
            public int TowerFusionTier { get; set; }
            public float TowerShieldBreakBonus { get; set; }
            public float TowerAccuracy { get; set; }
            public int TowerProjectileCount { get; set; }
            public float TowerScatterAngle { get; set; }
        }

        private class EnemySaveData
        {
            public int EnemyId { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Health { get; set; }
            public float MaxHealth { get; set; }
        }
    }
}