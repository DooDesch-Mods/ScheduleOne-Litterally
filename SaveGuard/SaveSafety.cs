using System;
using Trashville.Spawning;

namespace Trashville.SaveGuard
{
    /// <summary>
    /// CRITICAL: nothing the performance layer materialises may reach the player's save. The routed instanced
    /// field is pure DATA and is never a Saveable, so it can never bloat the save; it is persisted separately as a
    /// compact blob (SaveBlob.Save runs BEFORE this). This guard only has to destroy the transient REAL items the
    /// layer materialises (near the player / cleaners, plus any in-flight probes) so they are not serialized.
    ///
    /// The DEBUG benchmark additionally creates real game trash via the spawner/clones; those paths nuke ALL world
    /// trash (loud) and are compiled out of Release.
    /// </summary>
    internal static class SaveSafety
    {
        /// <summary>
        /// Clear used on the save / scene-change / teardown paths. Release: a TARGETED clear only - drain the route
        /// destroy queue, un-boost generators, demote the materialised reals back to data, abort in-flight probes.
        /// It deliberately does NOT call DestroyAllTrash (that would also delete the player's own dropped trash,
        /// which with routing on is real + non-routed) and does NOT permanently stop routing (the perf layer must
        /// keep running after the save). The routed data persists via the blob, so clearing the reals loses nothing.
        /// </summary>
        internal static void ForceClearForSave(string reason)
        {
            // Routing: drain the transient destroy queue so no half-absorbed real item lingers, and un-boost the
            // generators. Do NOT turn routing OFF in release - it must keep absorbing after the save completes.
            Trashville.Spawning.RouteHook.Tick();
            Trashville.Spawning.GeneratorBoost.Restore();
            Trashville.Instanced.Virtualizer.ClearAll();          // destroy any materialized (player) real items (don't persist)
            Trashville.Spawning.CleanerActor.ClearAll();          // destroy any cleaner-materialized real items (don't persist)
            Trashville.Instanced.InstancedTrash.AbortCalibration(); // destroy any in-flight calibration probes (real Saveable items)
            Trashville.Instanced.InstancedTrash.AbortDrift();      // destroy any in-flight ground-drift probes (real Saveable items)

#if DEBUG
            // Benchmark-only: stop absorbing, drop in-flight routing state, then nuke ALL world trash because the
            // benchmark spawner + bypass clones create real game trash that DestroyAllTrash / the registries clean
            // up. None of this exists in Release (no spawner, no clones), so it is gated out.
            TrashSpawner.CancelPending();
            Trashville.Spawning.RouteHook.Active = false;
            Trashville.Spawning.RouteHook.Tick();
            Trashville.Spawning.RouteHook.ResetState();

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

            var tm = GameTrash.TrashManagerOrNull();
            int before = tm != null ? GameTrash.TrashItemCount(tm) : 0;
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
#endif
        }

#if DEBUG
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
            var tm = GameTrash.TrashManagerOrNull();
            int before = tm != null ? GameTrash.TrashItemCount(tm) : 0;
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
#endif
    }
}
