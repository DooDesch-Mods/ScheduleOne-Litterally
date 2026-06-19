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
    /// to TOP the region UP to its new max in small staggered batches - routing absorbs every batch into the
    /// field, keeping trashItems near-empty so the 2000 cap never trips. As you EXPLORE, regions that come within
    /// range fill in, so the whole map fills (up to ~area*0.015*N, N=50 -> ~100k).
    ///
    /// Reload-safety uses the FIELD ITSELF as the single source of truth: the first time we reach a region we
    /// count the trash ALREADY inside its box (restored from the save, or generated earlier this session) and
    /// seed our progress with it, so we only generate the DELTA up to the current target. A region already at
    /// target is skipped (no double-fill); a region under target - because it was filled at a lower multiplier,
    /// or only partially - is topped up to the current multiplier. No separate done-key file to drift out of
    /// sync with the field or the multiplier, and no proximity heuristic to mis-skip a neighbour.
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
        private static readonly HashSet<int> _done = new HashSet<int>();              // runtime ids at/over target (skip)
        private static readonly HashSet<int> _boosted = new HashSet<int>();           // runtime ids whose multiplier+max+seed we've set this session
        private static readonly Dictionary<int, int> _generated = new Dictionary<int, int>();   // our running fill count per runtime id (seeded from the field, then += each batch)

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
                int id;
                try { id = g.GetInstanceID(); } catch { continue; }
                if (_done.Contains(id)) continue;

                Vector3 gp;
                try { gp = g.transform.position; } catch { continue; }
                float dx = gp.x - pp.x, dz = gp.z - pp.z;
                if (dx * dx + dz * dz > r2) continue;        // region not near the player yet

                try
                {
                    // First time we reach this region: raise the vanilla multiplier, recompute its max, and seed
                    // our progress from the trash ALREADY in its box so we only top up the delta (no double-fill).
                    if (_boosted.Add(id))
                    {
                        g.TrashCountMultiplier = Multiplier;
                        g.AutoCalculateTrashCount();
                        int tgt0 = g.MaxTrashCount;
                        int present = 0;
                        BoxCollider box = g.GetComponent<BoxCollider>();
                        if (box != null && tgt0 > 0)
                        {
                            Bounds b = box.bounds;
                            present = Instanced.InstancedTrash.CountInBox(b.center.x, b.center.z, b.extents.x, b.extents.z, tgt0);
                        }
                        _generated[id] = present;
                        if (tgt0 > 0 && present >= tgt0) { _done.Add(id); continue; }
                    }

                    int target = g.MaxTrashCount;
                    int have = _generated.TryGetValue(id, out int v) ? v : 0;
                    if (target <= 0 || have >= target) { _done.Add(id); continue; }

                    int batch = Math.Min(target - have, BatchPerGen);
                    g.GenerateTrash(batch);                  // vanilla generation -> opens the absorb window -> routed into the field
                    _generated[id] = have + batch;
                    advanced++;
                    if (have + batch >= target) _done.Add(id);
                }
                catch (Exception e) { Core.Log?.Warning("[populate] " + e.Message); _done.Add(id); }
            }
        }
    }
}
