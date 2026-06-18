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

            // Bypass clones are GameObjects we own and are NOT in tm.trashItems, so DestroyAllTrash misses
            // them - destroy them ourselves on every save/teardown path or 10k objects leak into the session.
            int clones = CloneRegistry.Count;
            if (clones > 0 || CloneRegistry.EverSpawned)
            {
                CloneRegistry.DestroyAll();
            }

            if (!TrashRegistry.EverSpawned && TrashRegistry.Count == 0)
            {
                if (clones > 0)
                {
                    Core.Log?.Warning($"[SaveGuard] {reason}: destroyed {clones} bypass clones.");
                }
                return;   // we never created game trash; leave the player's trash alone
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
