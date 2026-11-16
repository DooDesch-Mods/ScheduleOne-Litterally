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
                return tm != null && tm._trashItems != null ? tm._trashItems.Count : 0;
            }
            catch (System.Exception e)
            {
                Core.Log?.Warning("[GameTrash] trash count read failed: " + e.Message);
                return 0;
            }
        }

        /// <summary>The game's live-trash cap (2000). Read-only: writing the IL2CPP static crashes the game.</summary>
        internal static int TrashItemLimit => TrashManager.TrashItemLimit;

        /// <summary>
        /// Creates a real game TrashItem. Since game 0.4.7 TrashManager.CreateTrashItem has no startKinematic
        /// argument and every trash item spawns as a dynamic rigidbody. kinematic=true freezes the body after
        /// creation, which is what the old startKinematic flag did. A frozen item cannot be dragged (drag
        /// works through rigidbody forces), so callers that hand it to the player wake it with
        /// <see cref="SetPhysicsActive"/> once a drag starts.
        /// </summary>
        internal static TrashItem Create(TrashManager tm, string id, Vector3 pos, Quaternion rot, string guid, bool kinematic)
        {
            TrashItem item = tm.CreateTrashItem(id, pos, rot, Vector3.zero, guid);
            if (item != null && kinematic)
            {
                Rigidbody rb = BodyOf(item);
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.interpolation = RigidbodyInterpolation.None;
                }
                else
                {
                    Core.Log?.Warning("[GameTrash] trash '" + id + "' has no Rigidbody, cannot freeze it");
                }
            }
            return item;
        }

        /// <summary>The item's rigidbody. 0.4.7 made TrashItem.Rigidbody private; the game reads it from the root.</summary>
        internal static Rigidbody BodyOf(TrashItem item)
        {
            return item != null ? item.GetComponent<Rigidbody>() : null;
        }

        /// <summary>Replacement for the removed TrashItem.SetPhysicsActive: dynamic + interpolated + colliders on,
        /// or kinematic + no interpolation (+ colliders off when <paramref name="collidersFollow"/>).</summary>
        internal static void SetPhysicsActive(TrashItem item, bool active, bool collidersFollow = false)
        {
            Rigidbody rb = BodyOf(item);
            if (rb != null)
            {
                rb.isKinematic = !active;
                rb.interpolation = active ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            }
            if (active || collidersFollow) SetCollidersEnabled(item, active);
        }

        /// <summary>Replacement for the removed TrashItem.SetCollidersEnabled.</summary>
        internal static void SetCollidersEnabled(TrashItem item, bool enabled)
        {
            if (item == null) return;
            var cols = item.GetComponentsInChildren<Collider>(true);
            if (cols == null) return;
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) cols[i].enabled = enabled;
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
