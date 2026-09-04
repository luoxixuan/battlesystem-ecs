using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>Catalog Tag 词汇表条目。ParentValue &lt; 0 表示无父标签。</summary>
    public readonly struct TagVocabularyEntry
    {
        public readonly string Name;
        public readonly int Id;
        public readonly int ParentValue;

        public TagVocabularyEntry(string name, int id, int parentValue = -1)
        {
            Name = name;
            Id = id;
            ParentValue = parentValue;
        }

        public bool HasParent => ParentValue >= 0;
        public TagId Tag => new TagId(Id);
        public TagId Parent => new TagId(ParentValue);
    }

    /// <summary>
    /// 带 parent 的 Tag 词汇表。静态构造编译每个 TagId 的祖先闭包。
    /// 运行时 HasTag 仍是整数键计数；祖先展开只作用于贡献计数和 Required/Blocked 匹配。
    /// </summary>
    public static class GameplayTagVocabulary
    {
        public const int NoneParent = -1;

        // 0–10 保持既有散列常量、无 parent，避免改 shipped 标签语义。
        // 演示层级：Stun ⊂ Control ⊂ Debuff。不要在这里铺一整棵游戏 Tag 树。
        private static readonly TagVocabularyEntry[] _entries =
        {
            new TagVocabularyEntry("Normal", 0),
            new TagVocabularyEntry("Burn", 1),
            new TagVocabularyEntry("Fire", 2),
            new TagVocabularyEntry("Freeze", 3),
            new TagVocabularyEntry("Lightning", 4),
            new TagVocabularyEntry("Explosion", 5),
            new TagVocabularyEntry("Plasma", 6),
            new TagVocabularyEntry("Poison", 7),
            new TagVocabularyEntry("EnemyBuff", 8),
            new TagVocabularyEntry("TowerSilenced", 9),
            new TagVocabularyEntry("Dispellable", 10),
            new TagVocabularyEntry("Debuff", 11),
            new TagVocabularyEntry("Control", 12, 11),
            new TagVocabularyEntry("Stun", 13, 12)
        };

        private static readonly Dictionary<string, TagId> _byName;
        private static readonly TagId[][] _ancestors;

        public static readonly TagId Normal = new TagId(0);
        public static readonly TagId Burn = new TagId(1);
        public static readonly TagId Fire = new TagId(2);
        public static readonly TagId Freeze = new TagId(3);
        public static readonly TagId Lightning = new TagId(4);
        public static readonly TagId Explosion = new TagId(5);
        public static readonly TagId Plasma = new TagId(6);
        public static readonly TagId Poison = new TagId(7);
        public static readonly TagId EnemyBuff = new TagId(8);
        public static readonly TagId TowerSilenced = new TagId(9);
        public static readonly TagId Dispellable = new TagId(10);
        public static readonly TagId Debuff = new TagId(11);
        public static readonly TagId Control = new TagId(12);
        public static readonly TagId Stun = new TagId(13);

        static GameplayTagVocabulary()
        {
            int count = _entries.Length;
            _byName = new Dictionary<string, TagId>(count, StringComparer.OrdinalIgnoreCase);
            _ancestors = new TagId[count][];
            for (int i = 0; i < count; i++)
            {
                var entry = _entries[i];
                if (entry.Id != i)
                    throw new InvalidOperationException("tag vocabulary ids must be contiguous from 0");
                if (entry.HasParent && ((uint)entry.ParentValue >= (uint)count || entry.ParentValue == entry.Id))
                    throw new InvalidOperationException("tag vocabulary parent is invalid for " + entry.Name);
                if (_byName.ContainsKey(entry.Name))
                    throw new InvalidOperationException("duplicate tag name " + entry.Name);
                _byName.Add(entry.Name, entry.Tag);
            }
            for (int i = 0; i < count; i++)
                _ancestors[i] = CompileAncestors(i, count);
        }

        private static TagId[] CompileAncestors(int id, int count)
        {
            var chain = new List<TagId>(4);
            var seen = new bool[count];
            seen[id] = true;
            int walk = _entries[id].ParentValue;
            while (walk >= 0)
            {
                if ((uint)walk >= (uint)count || seen[walk])
                    throw new InvalidOperationException("tag vocabulary parent cycle at id " + id);
                seen[walk] = true;
                chain.Add(new TagId(walk));
                walk = _entries[walk].ParentValue;
            }
            return chain.Count == 0 ? Array.Empty<TagId>() : chain.ToArray();
        }

        public static int Count => _entries.Length;
        public static IReadOnlyList<TagVocabularyEntry> Entries => _entries;

        public static bool TryResolve(string name, out TagId id) => _byName.TryGetValue(name, out id);

        public static bool Contains(TagId id) => (uint)id.Value < (uint)_entries.Length;

        /// <summary>已编译祖先（不含自身），由近到远。未知 TagId 返回空，不扫运行时列表。</summary>
        public static IReadOnlyList<TagId> AncestorsOf(TagId tag)
        {
            if ((uint)tag.Value >= (uint)_ancestors.Length) return Array.Empty<TagId>();
            return _ancestors[tag.Value];
        }

        public static bool TryGetParent(TagId tag, out TagId parent)
        {
            parent = default(TagId);
            if ((uint)tag.Value >= (uint)_entries.Length) return false;
            var entry = _entries[tag.Value];
            if (!entry.HasParent) return false;
            parent = entry.Parent;
            return true;
        }
    }
}
