using UnityEngine;

namespace Litterally.Spawning
{
    /// <summary>
    /// Tiny always-compiled bridge to the game's trash + player state. Holds the three lookups the
    /// RELEASE performance layer needs (TrashManager, its live item count, the player position). These
    /// used to live in the dev-only TrashSpawner; they were extracted here so the release-core files
    /// (RouteHook, Virtualizer, CleanerActor, SaveSafety, InstancedTrash, Core) never reference the
    /// benchmark harness, which is compiled out of Release builds.
    /// </summary>
    internal static class GameTrash
    {
        internal static TrashManager TrashManagerOrNull()
        {
            try
            {
                return NetworkSingleton<TrashManager>.Instance;
            }
            catch
            {
                return null;
            }
        }

        internal static int TrashItemCount(TrashManager tm)
        {
            try
            {
                return tm != null && tm.trashItems != null ? tm.trashItems.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static bool TryGetPlayerPosition(out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                Player p = Player.Local;
                if (p == null)
                {
                    return false;
                }
                pos = p.transform.position;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
