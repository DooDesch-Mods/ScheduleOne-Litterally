using System;
using System.IO;
using UnityEngine;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Persistence;

namespace Litterally.Instanced
{
    /// <summary>
    /// Persists the ROUTED instanced trash field as ONE compact binary file inside the current save folder, so
    /// game trash the player hasn't cleaned survives save/reload - exactly like base-game trash - WITHOUT
    /// touching the game's per-item Saveable path. Routed trash is never a real TrashItem, so the game writes
    /// nothing for it (no 100k-file folder); this blob is the only trash persistence. The benchmark `tv inst`
    /// field is NOT persisted (it never sets RoutedDataPresent).
    /// </summary>
    internal static class SaveBlob
    {
        private static string BlobPath()
        {
            try
            {
                // Key the blob to the CURRENT save (e.g. <steamid>/SaveGame_2) but store it in a MOD-owned folder
                // OUTSIDE the game's save folder. Writing inside the save folder does NOT work: the game's save
                // process runs DeleteUnapprovedFiles(saveFolder) and prunes any file it didn't write - it deleted
                // our blob right after we wrote it (verified: the file vanished). persistentDataPath/Litterally is
                // a sibling of Saves, so it is never pruned.
                LoadManager lm = PersistentSingleton<LoadManager>.Instance;
                string saveFolder = lm != null ? lm.LoadedGameFolderPath : null;
                if (string.IsNullOrEmpty(saveFolder)) return null;
                saveFolder = saveFolder.Replace('\\', '/').TrimEnd('/');
                string[] parts = saveFolder.Split('/');
                string key = parts.Length >= 2 ? parts[parts.Length - 2] + "_" + parts[parts.Length - 1]
                                               : parts[parts.Length - 1];
                foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
                string dir = Path.Combine(Application.persistentDataPath, "Litterally", "saves");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, key + ".tvb");
            }
            catch (Exception e) { Core.Log?.Warning("[blob] path resolve failed: " + e.Message); return null; }
        }

        /// <summary>Called on OnSaveStart BEFORE the real working-set is cleared. Writes the routed field.</summary>
        internal static void Save()
        {
            if (!InstancedTrash.RoutedDataPresent) return;   // benchmark-only field -> stays ephemeral
            string path = BlobPath();
            if (path == null) { Core.Log?.Warning("[blob] no save path; routed trash NOT persisted this save"); return; }
            try
            {
                int n = InstancedTrash.WriteBlob(path);
                Core.Log?.Msg($"[blob] persisted {n} routed instanced items -> {path}");
                // Clean up the obsolete done-key sidecar from older builds (the populator now reads region fill
                // state from the field itself, so this file is no longer used and must not linger).
                try { string gp = path + ".gen"; if (File.Exists(gp)) File.Delete(gp); } catch { }
            }
            catch (Exception e) { Core.Log?.Warning("[blob] save failed: " + e.Message); }
        }

        /// <summary>Called on OnLoadComplete. Rehydrates the routed field from the blob (if any).</summary>
        internal static void Load()
        {
            // Entering the world fresh: drop any stale in-memory field (an in-session reload keeps the mod's
            // statics, so without this the blob would be added ON TOP of the pre-reload field = doubling).
            InstancedTrash.Clear();
            string path = BlobPath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                int n = InstancedTrash.ReadBlob(path);
                Core.Log?.Msg($"[blob] restored {n} routed instanced items from {path}");
                // keep absorbing newly generated trash so behaviour stays "like base game" after a reload.
                if (n > 0) Spawning.RouteHook.Active = true;
            }
            catch (Exception e) { Core.Log?.Warning("[blob] load failed: " + e.Message); }
        }
    }
}
