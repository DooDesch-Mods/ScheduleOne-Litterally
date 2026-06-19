using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.Trash;
using Trashville.Instanced;

namespace Trashville.Spawning
{
    /// <summary>
    /// Drives the GAME'S OWN trash generation harder, then lets routing absorb it into the cheap instanced field.
    ///
    /// Vanilla (real Mono source): each TrashGenerator is a BoxCollider region with
    ///   MaxTrashCount = area(m2) * DEFAULT_TRASH_PER_M2 (0.015) * TrashCountMultiplier (default 1)
    /// and it only fills on the daily SleepStart (+20%/day) via GenerateTrash -> TrashManager.CreateTrashItem.
    /// So a fresh save starts near-empty and accumulates slowly; nothing generates while you walk.
    ///
    /// We hook EXACTLY that mechanism: raise the vanilla TrashCountMultiplier to N (up to 50), recompute the max
    /// via the vanilla AutoCalculateTrashCount(), and call the vanilla GenerateTrash() to fill each region near
    /// the player to its new (N x) max - in small staggered batches so the global 2000 cap never trips (routing
    /// absorbs each batch into the field, keeping trashItems near-empty) and there is no per-frame spike. The
    /// world fills with up to ~area*0.015*N trash around the player as they explore (N=50 -> ~100k total).
    /// </summary>
    internal static class TrashPopulator
    {
        internal static bool Enabled = true;
        internal static int Multiplier = 1;        // mirror of Preferences.TrashMultiplier (1 = off, do nothing)
        internal static float Radius = 160f;       // only populate regions within this far of the player

        private const int BatchPerGen = 30;        // items generated per region per pass (bounded so routing drains them before 2000)
        private const int GensPerPass = 2;         // regions advanced per pass (stagger)
        private const float PassEvery = 0.3f;      // seconds between passes
        private const float ReloadSkipRadius = 28f;// if the field already has trash this close to a region, treat it as already populated (reload)

        private static float _timer;
        private static readonly HashSet<int> _done = new HashSet<int>();        // regions filled (or skipped) this session
        private static readonly HashSet<int> _seen = new HashSet<int>();        // regions we've reload-checked
        private static readonly Dictionary<int, int> _generated = new Dictionary<int, int>();   // our generated count per region (absorb removes from generatedTrash, so we can't trust that)
        private static readonly int[] _probe = new int[64];

        internal static void Reset()
        {
            _done.Clear(); _seen.Clear(); _generated.Clear(); _timer = 0f;
        }

        internal static void Tick(float dt)
        {
            if (!Enabled || Multiplier <= 1) return;
            // If this session's field was restored from the save blob, it is already populated - don't double-fill.
            if (InstancedTrash.RestoredFromBlob) return;

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
                int id;
                try { id = g.GetInstanceID(); } catch { continue; }
                if (_done.Contains(id)) continue;

                Vector3 gp;
                try { gp = g.transform.position; } catch { continue; }
                float dx = gp.x - pp.x, dz = gp.z - pp.z;
                if (dx * dx + dz * dz > r2) continue;   // region not near the player yet

                try
                {
                    // First time we reach this region: raise the vanilla multiplier + recompute its max (the
                    // principled hook), and skip it if the restored field already covers it (reload guard).
                    if (_seen.Add(id))
                    {
                        g.TrashCountMultiplier = Multiplier;
                        g.AutoCalculateTrashCount();
                        int near = InstancedTrash.QueryGrid(gp.x, gp.z, ReloadSkipRadius, _probe, _probe.Length);
                        if (near >= _probe.Length) { _done.Add(id); continue; }   // already dense here -> populated
                    }

                    int target = g.MaxTrashCount;
                    int have = _generated.TryGetValue(id, out int v) ? v : 0;
                    if (have >= target || target <= 0) { _done.Add(id); continue; }

                    int batch = Math.Min(target - have, BatchPerGen);
                    g.GenerateTrash(batch);                 // vanilla generation -> opens the absorb window -> routed into the field
                    _generated[id] = have + batch;
                    advanced++;
                    if (have + batch >= target) _done.Add(id);
                }
                catch (Exception e) { Core.Log?.Warning("[populate] " + e.Message); _done.Add(id); }
            }
        }
    }
}
