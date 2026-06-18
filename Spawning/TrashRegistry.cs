using System.Collections.Generic;

namespace Trashville.Spawning
{
    /// <summary>
    /// The single source of truth for the trash items WE spawned. Ablation toggles and the
    /// scoped clear iterate this list directly - we never call FindObjectsOfType per frame
    /// (that would pollute the very measurement we are taking).
    /// </summary>
    internal static class TrashRegistry
    {
        // Parallel-ish: Spawned holds the live TrashItem refs (entries may go null if the game
        // destroys one); Guids tracks the ids we assigned so save-safety knows we spawned at all.
        internal static readonly List<TrashItem> Spawned = new List<TrashItem>(16000);
        internal static readonly HashSet<string> Guids = new HashSet<string>();

        /// <summary>True once anything has been spawned this session (drives the save guard).</summary>
        internal static bool EverSpawned { get; private set; }

        internal static int Count => Spawned.Count;

        internal static void Add(TrashItem item, string guid)
        {
            if (item == null)
            {
                return;
            }
            Spawned.Add(item);
            if (!string.IsNullOrEmpty(guid))
            {
                Guids.Add(guid);
            }
            EverSpawned = true;
        }

        /// <summary>Drop null/destroyed entries so iteration stays clean. Cheap; call before a sweep step.</summary>
        internal static void Compact()
        {
            for (int i = Spawned.Count - 1; i >= 0; i--)
            {
                if (Spawned[i] == null)
                {
                    Spawned.RemoveAt(i);
                }
            }
        }

        internal static void Clear()
        {
            Spawned.Clear();
            Guids.Clear();
        }
    }
}
