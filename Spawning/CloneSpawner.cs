using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Audio;       // RBImpactSounds
using Il2CppScheduleOne.Dragging;    // Draggable

namespace Litterally.Spawning
{
    /// <summary>
    /// Cap-bypass spawner (Strategy B). Caches ONE stripped, INACTIVE template per trash id, then
    /// Object.Instantiate's cheap clones from it. The clone inherits the template's inactive state, so its
    /// native Awake does NOT run until we SetActive(true) - by which point the TrashItem/RBImpactSounds/
    /// Draggable components are already gone, so no Saveable self-register, no per-collision SFX, no
    /// networking, and NO 2000-cap (these never touch TrashManager.trashItems).
    /// </summary>
    internal static class CloneSpawner
    {
        private static readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();

        /// <summary>Spawn one bypass clone. Returns true on success.</summary>
        internal static bool TrySpawn(TrashManager tm, string id, Vector3 pos, Quaternion rot)
        {
            GameObject template = EnsureTemplate(tm, id);
            if (template == null)
            {
                return false;
            }
            try
            {
                // Clone of an inactive template stays inactive. Activate FIRST (the template is already
                // stripped, so no TrashItem.Awake self-registers), THEN configure the now-live rigidbody -
                // setting gravity/velocity on an inactive body doesn't take, which left them asleep mid-air.
                GameObject clone = UnityEngine.Object.Instantiate(template, pos, rot);
                clone.SetActive(true);
                Rigidbody rb = OptimizationLevers.ConfigureClone(clone, clone.GetComponentInChildren<Rigidbody>(true));
                CloneRegistry.Add(clone, rb);
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Clone] spawn failed: " + e.Message);
                return false;
            }
        }

        private static GameObject EnsureTemplate(TrashManager tm, string id)
        {
            if (_templates.TryGetValue(id, out GameObject cached) && cached != null)
            {
                return cached;
            }
            try
            {
                TrashItem prefab = tm.GetTrashPrefab(id);
                if (prefab == null)
                {
                    return null;
                }
                // Instantiate the prefab once (active -> its Awake runs this one time); then deactivate + strip
                // so all clones made from it are Awake-free and game-hook-free.
                GameObject tmpl = UnityEngine.Object.Instantiate(prefab.gameObject);
                tmpl.SetActive(false);
                Strip(tmpl);
                if (Config.Preferences.OptimizeClones)
                {
                    OptimizationLevers.DropMeshColliders(tmpl);   // bake collider simplification once; clones inherit
                    // NOTE: do NOT force the hierarchy onto layer 10 to kill self-collision - that also breaks
                    // collision with the GROUND (layer 10 doesn't collide with the world here), so clones fall
                    // through the floor forever and never sleep. We rely on a wide spawn spread instead so the
                    // (self-colliding) pile stays thin and cheap.
                }
                UnityEngine.Object.DontDestroyOnLoad(tmpl);
                _templates[id] = tmpl;
                Core.Log?.Msg($"[Clone] template ready for '{id}'.");
                return tmpl;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Clone] template '{id}' failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Remove every game-side hook from the (inactive) template. DestroyImmediate so the
        /// component is GONE before any clone is activated (Object.Destroy is deferred and would let Awake run).</summary>
        private static void Strip(GameObject go)
        {
            try
            {
                TrashItem ti = go.GetComponent<TrashItem>();
                if (ti != null)
                {
                    try { ti.Deinitialize(); } catch { }   // undo the one-time Saveable/manager registration
                    UnityEngine.Object.DestroyImmediate(ti);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[Clone] strip TrashItem: " + e.Message); }

            try
            {
                Il2CppArrayBase<RBImpactSounds> snds = go.GetComponentsInChildren<RBImpactSounds>(true);
                if (snds != null)
                {
                    for (int i = 0; i < snds.Length; i++)
                    {
                        if (snds[i] != null) UnityEngine.Object.DestroyImmediate(snds[i]);
                    }
                }
            }
            catch { }

            try
            {
                Il2CppArrayBase<Draggable> drags = go.GetComponentsInChildren<Draggable>(true);
                if (drags != null)
                {
                    for (int i = 0; i < drags.Length; i++)
                    {
                        if (drags[i] != null) UnityEngine.Object.DestroyImmediate(drags[i]);
                    }
                }
            }
            catch { }
        }

        internal static void Reset()
        {
            foreach (var kv in _templates)
            {
                if (kv.Value != null) { try { UnityEngine.Object.Destroy(kv.Value); } catch { } }
            }
            _templates.Clear();
        }
    }
}
