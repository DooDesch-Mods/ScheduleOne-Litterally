using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.Trash;

namespace Trashville.Spawning
{
    /// <summary>
    /// Makes the game's own TrashGenerators spawn far more by raising each generator's MaxTrashCount (the one lever
    /// confirmed writable). Captures the original per generator ONCE so toggling on/off never compounds, and
    /// restores it exactly. Generation is burst-driven (no per-frame Update), so the boost affects regions that
    /// generate AFTER it is applied - walk into a fresh region, or force a burst with 'tv route burst'.
    /// </summary>
    internal static class GeneratorBoost
    {
        private static readonly Dictionary<TrashGenerator, int> _saved = new Dictionary<TrashGenerator, int>();
        private static bool _applied;

        internal static void Apply(int mult)
        {
            mult = Mathf.Clamp(mult, 1, 1000);
            Il2CppSystem.Collections.Generic.List<TrashGenerator> all = null;
            try { all = TrashGenerator.AllGenerators; } catch (Exception e) { Core.Log?.Warning("[route] boost AllGenerators: " + e.Message); }
            if (all == null) { Core.Log?.Warning("[route] boost: no generators"); return; }
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                TrashGenerator g = all[i];
                if (g == null) continue;
                if (!_saved.ContainsKey(g)) { try { _saved[g] = g.MaxTrashCount; } catch { continue; } }
                try { g.MaxTrashCount = _saved[g] * mult; n++; } catch { }
            }
            _applied = true;
            Core.Log?.Msg($"[route] boosted MaxTrashCount x{mult} on {n} generators. Walk into a fresh region or 'tv route burst' to see it.");
        }

        internal static void Restore()
        {
            if (!_applied && _saved.Count == 0) return;
            foreach (var kv in _saved)
            {
                TrashGenerator g = kv.Key;
                if (g == null) continue;
                try { g.MaxTrashCount = kv.Value; } catch { }
            }
            _saved.Clear();
            _applied = false;
            Core.Log?.Msg("[route] generator MaxTrashCount restored.");
        }
    }
}
