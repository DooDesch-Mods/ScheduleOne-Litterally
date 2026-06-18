using System;
using Trashville.Spawning;

namespace Trashville.SaveGuard
{
    /// <summary>
    /// CRITICAL: benchmark trash must NEVER reach the player's save. Each TrashItem is individually
    /// Saveable, so a 10k pile would write thousands of files and persist forever. We force-clear on
    /// every save/teardown path. The save path uses the guaranteed DestroyAllTrash() sweep (loud),
    /// because our in-memory registry can miss items; manual testing uses the scoped clear.
    /// </summary>
    internal static class SaveSafety
    {
        /// <summary>
        /// Guaranteed clear used on the save / teardown paths. If we ever spawned this session, nuke
        /// ALL world trash so nothing benchmark-related can be serialized. Loud on purpose.
        /// </summary>
        internal static void ForceClearForSave(string reason)
        {
            TrashSpawner.CancelPending();
            // Routing: stop absorbing, drain the transient destroy queue, and un-boost generators so a continued
            // session isn't stuck with boosted generators. Routed trash is pure DATA (never Saveable) so it can
            // never bloat the save; only the transient/near-materialized reals matter and are swept below.
            Trashville.Spawning.RouteHook.Active = false;
            Trashville.Spawning.RouteHook.Tick();
            Trashville.Spawning.GeneratorBoost.Restore();
            Trashville.Instanced.Virtualizer.ClearAll();          // destroy any materialized real items (don't persist)
            Trashville.Instanced.InstancedTrash.AbortCalibration(); // destroy any in-flight calibration probes (real Saveable items)
            Trashville.Instanced.InstancedTrash.AbortDrift();      // destroy any in-flight ground-drift probes (real Saveable items)

            // Bypass clones are GameObjects we own and are NOT in tm.trashItems, so DestroyAllTrash misses
            // them - destroy them ourselves on every save/teardown path or 10k objects leak into the session.
            int clones = CloneRegistry.Count;
            if (clones > 0 || CloneRegistry.EverSpawned)
            {
                CloneRegistry.DestroyAll();
            }

            // The instanced path creates real TrashItems (calibration probes + materialized items) WITHOUT ever
            // touching TrashRegistry, so EverSpawned alone cannot tell us a sweep is unnecessary - OR in the
            // instanced flag too, or a probe/materialized item could be serialized into the player's save.
            bool instancedReals = Trashville.Instanced.InstancedTrash.EverMaterialized;
            if (!TrashRegistry.EverSpawned && TrashRegistry.Count == 0 && !instancedReals)
            {
                if (clones > 0)
                {
                    Core.Log?.Warning($"[SaveGuard] {reason}: destroyed {clones} bypass clones.");
                }
                return;   // we never created game trash via any path; leave the player's trash alone
            }

            var tm = TrashSpawner.TrashManagerOrNull();
            int before = tm != null ? TrashSpawner.TrashItemCount(tm) : 0;
            try
            {
                tm?.DestroyAllTrash();
                Core.Log?.Warning($"[SaveGuard] {reason}: cleared ALL world trash ({before} items) + {clones} clones so benchmark trash is never saved.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[SaveGuard] DestroyAllTrash failed: " + e.Message);
            }
            TrashRegistry.Clear();
        }

        /// <summary>Destroy ONLY the trash this mod spawned (for iterative testing). Best-effort.</summary>
        internal static void ScopedClear()
        {
            TrashSpawner.CancelPending();

            int clones = CloneRegistry.Count;
            if (clones > 0)
            {
                CloneRegistry.DestroyAll();
            }

            TrashRegistry.Compact();
            int n = 0;
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null)
                {
                    continue;
                }
                try
                {
                    item.DestroyTrash();
                    n++;
                }
                catch { /* already gone */ }
            }
            TrashRegistry.Clear();
            Core.Log?.Msg($"[SaveGuard] Scoped clear removed {n} game items + {clones} clones.");
        }

        /// <summary>Nuke ALL trash in the world (recovery for a bloated save). Loud.</summary>
        internal static void PurgeAll()
        {
            TrashSpawner.CancelPending();
            int clones = CloneRegistry.Count;
            if (clones > 0)
            {
                CloneRegistry.DestroyAll();
            }
            var tm = TrashSpawner.TrashManagerOrNull();
            int before = tm != null ? TrashSpawner.TrashItemCount(tm) : 0;
            try
            {
                tm?.DestroyAllTrash();
                Core.Log?.Warning($"[SaveGuard] PURGE: removed {before} world trash + {clones} clones.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[SaveGuard] PurgeAll failed: " + e.Message);
            }
            TrashRegistry.Clear();
        }
    }
}
