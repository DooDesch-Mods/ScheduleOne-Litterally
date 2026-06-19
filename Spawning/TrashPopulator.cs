using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.Trash;

namespace Trashville.Spawning
{
    /// <summary>
    /// Drives the GAME'S OWN trash generation harder, then lets routing absorb it into the cheap instanced field.
    ///
    /// Vanilla (real Mono source): each TrashGenerator is a BoxCollider region with
    ///   MaxTrashCount = floor(area_m2 * DEFAULT_TRASH_PER_M2 (0.015) * TrashCountMultiplier (default 1))
    /// and it only fills on the daily SleepStart (+20%/day) via GenerateTrash -> TrashManager.CreateTrashItem.
    /// So a fresh save starts near-empty and accumulates slowly; nothing generates while you walk.
    ///
    /// We hook EXACTLY that mechanism: raise the vanilla TrashCountMultiplier to N (up to 50) on regions near the
    /// player, recompute the max via the vanilla AutoCalculateTrashCount(), and call the vanilla GenerateTrash()
    /// to fill each to its new max in small staggered batches - the routing GenerateTrash-window absorbs every
    /// batch into the field, keeping trashItems near-empty so the 2000 cap never trips. As you EXPLORE, regions
    /// that come within range fill in, so the whole map fills (up to ~area*0.015*N, N=50 -> ~100k).
    ///
    /// Reload-safety is per-region and PERSISTED: each fully-filled region is remembered by a stable position
    /// key, and SaveBlob writes/restores that set alongside the field. So on a continued save the already-filled
    /// regions are skipped (no double-fill) while regions you newly explore still fill - and there is no
    /// proximity heuristic that could mis-skip a region just because a neighbour's trash is nearby.
    /// </summary>
    internal static class TrashPopulator
    {
        internal static bool Enabled = true;
        internal static int Multiplier = 1;        // mirror of Preferences.TrashMultiplier (1 = off, do nothing)
        internal static float Radius = 170f;       // only populate regions within this far of the player

        private const int BatchPerGen = 30;        // items generated per region per pass (bounded so routing drains them before 2000)
        private const int GensPerPass = 2;         // regions advanced per pass (stagger)
        private const float PassEvery = 0.3f;      // seconds between passes

        private static float _timer;
        private static readonly HashSet<string> _done = new HashSet<string>();        // region keys fully filled (persisted with the save)
        private static readonly HashSet<int> _boosted = new HashSet<int>();           // runtime ids whose multiplier+max we've set this session
        private static readonly Dictionary<int, int> _generated = new Dictionary<int, int>();   // our generated count per runtime id (absorb removes from generatedTrash, so we can't trust that)

        // Stable key for a region across sessions (generators never move; 1m rounding is unique enough between regions).
        private static string Key(Vector3 p) => Mathf.RoundToInt(p.x) + "_" + Mathf.RoundToInt(p.z);

        /// <summary>Snapshot of fully-filled region keys, for SaveBlob to persist.</summary>
        internal static List<string> SnapshotDone() => new List<string>(_done);

        /// <summary>Restore fully-filled region keys from the save (called by SaveBlob.Load) so they aren't re-filled.</summary>
        internal static void SeedDone(IEnumerable<string> keys)
        {
            if (keys == null) return;
            foreach (string k in keys) if (!string.IsNullOrEmpty(k)) _done.Add(k);
        }

        internal static void Reset()
        {
            _done.Clear(); _boosted.Clear(); _generated.Clear(); _timer = 0f;
        }

        internal static void Tick(float dt)
        {
            if (!Enabled || Multiplier <= 1) return;
            _timer += dt;
            if (_timer < PassEvery) return;
            _timer = 0f;

            if (!GameTrash.TryGetPlayerPosition(out Vector3 pp)) return;
            Il2CppSystem.Collections.Generic.List<TrashGenerator> all;
            try { all = TrashGenerator.AllGenerators; } catch { return; }
            if (all == null) return;

            float r2 = Radius * Radius;
            int advanced = 0;
            for (int i = 0; i < all.Count && advanced < GensPerPass; i++)
            {
                TrashGenerator g = all[i];
                if (g == null) continue;
                Vector3 gp;
                try { gp = g.transform.position; } catch { continue; }
                float dx = gp.x - pp.x, dz = gp.z - pp.z;
                if (dx * dx + dz * dz > r2) continue;        // region not near the player yet

                string key = Key(gp);
                if (_done.Contains(key)) continue;           // already fully filled (this session or restored from the save)

                int id;
                try { id = g.GetInstanceID(); } catch { continue; }
                try
                {
                    // First time we reach this region: raise the vanilla multiplier + recompute its max.
                    if (_boosted.Add(id))
                    {
                        g.TrashCountMultiplier = Multiplier;
                        g.AutoCalculateTrashCount();
                    }
                    int target = g.MaxTrashCount;
                    int have = _generated.TryGetValue(id, out int v) ? v : 0;
                    if (have >= target || target <= 0) { _done.Add(key); continue; }

                    int batch = Math.Min(target - have, BatchPerGen);
                    g.GenerateTrash(batch);                  // vanilla generation -> opens the absorb window -> routed into the field
                    _generated[id] = have + batch;
                    advanced++;
                    if (have + batch >= target) _done.Add(key);
                }
                catch (Exception e) { Core.Log?.Warning("[populate] " + e.Message); _done.Add(key); }
            }
        }
    }
}
