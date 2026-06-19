using System;
using System.IO;
using UnityEngine;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Persistence;

namespace Trashville.Instanced
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
                // our blob right after we wrote it (verified: the file vanished). persistentDataPath/Trashville is
                // a sibling of Saves, so it is never pruned.
                LoadManager lm = PersistentSingleton<LoadManager>.Instance;
                string saveFolder = lm != null ? lm.LoadedGameFolderPath : null;
                if (string.IsNullOrEmpty(saveFolder)) return null;
                saveFolder = saveFolder.Replace('\\', '/').TrimEnd('/');
                string[] parts = saveFolder.Split('/');
                string key = parts.Length >= 2 ? parts[parts.Length - 2] + "_" + parts[parts.Length - 1]
                                               : parts[parts.Length - 1];
                foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
                string dir = Path.Combine(Application.persistentDataPath, "Trashville", "saves");
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
                WriteDoneKeys(path);
                Core.Log?.Msg($"[blob] persisted {n} routed instanced items -> {path}");
            }
            catch (Exception e) { Core.Log?.Warning("[blob] save failed: " + e.Message); }
        }

        // Persist which generator regions we've already fully populated, so a reload skips them (no double-fill)
        // while regions the player newly explores still fill. Stored next to the field blob.
        private static void WriteDoneKeys(string blobPath)
        {
            try
            {
                System.Collections.Generic.List<string> keys = Spawning.TrashPopulator.SnapshotDone();
                string gp = blobPath + ".gen";
                if (keys == null || keys.Count == 0) { if (File.Exists(gp)) File.Delete(gp); return; }
                File.WriteAllLines(gp, keys.ToArray());
            }
            catch (Exception e) { Core.Log?.Warning("[blob] region-marker save failed: " + e.Message); }
        }

        private static void ReadDoneKeys(string blobPath)
        {
            try
            {
                string gp = blobPath + ".gen";
                if (!File.Exists(gp)) return;
                string[] keys = File.ReadAllLines(gp);
                Spawning.TrashPopulator.SeedDone(keys);
                Core.Log?.Msg($"[blob] restored {keys.Length} populated-region markers");
            }
            catch (Exception e) { Core.Log?.Warning("[blob] region-marker load failed: " + e.Message); }
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
                ReadDoneKeys(path);
                Core.Log?.Msg($"[blob] restored {n} routed instanced items from {path}");
                // keep absorbing newly generated trash so behaviour stays "like base game" after a reload.
                if (n > 0) Spawning.RouteHook.Active = true;
            }
            catch (Exception e) { Core.Log?.Warning("[blob] load failed: " + e.Message); }
        }
    }
}
