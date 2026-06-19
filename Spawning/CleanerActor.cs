using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.Trash;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.DevUtilities;
using Trashville.Instanced;

namespace Trashville.Spawning
{
    /// <summary>
    /// Keeps base-game Cleaner NPCs working with the instanced field. A Cleaner's native AI only acts on real
    /// TrashItems near it (it reads TrashManager.trashItems / a position - it has no concept of our data). So we
    /// MATERIALIZE the few data items near each active cleaner into real TrashItems (the same CreateTrashItem path
    /// the player path uses, so they are indistinguishable from game-spawned trash). The cleaner then finds, walks
    /// to, and collects them exactly as in the base game; when it destroys one we drop the data entry permanently.
    /// Items that drift out of any cleaner's reach (and are not its current target) are demoted back to data.
    ///
    /// This is the "no feature loss" piece: cleaners behave 1:1 like base game, just over a cheap backing store.
    /// Coordinates with the player Virtualizer purely through InstancedTrash's _realized flag (QueryGrid /
    /// CollectVisible both skip _realized), so an item is never materialized by both actors at once.
    /// </summary>
    internal static class CleanerActor
    {
        internal static bool Enabled = true;        // on by default - cleaners are a base-game feature
        // How far from a cleaner we pre-materialize data trash so its native AI has real targets to path toward.
        // The PerCleaner cap bounds cost regardless of this radius, so it can be generous; ~30m covers a
        // cleaner's patrol awareness (a stationary cleaner needs it wide enough to reach nearby trash).
        internal static float Range = 30f;
        internal static int PerCleaner = 6;          // cap concurrent materialised items per cleaner
        private static float DemoteRange2 => (Range + 8f) * (Range + 8f); // hysteresis: demote only once well outside Range
        private const int RebuildEvery = 30;         // grid rebuild cadence (the field is mostly static)
        private const int FindCleanersEvery = 120;   // refind cleaners (they spawn/despawn rarely)

        private static int _frame;
        private static Cleaner[] _cleaners = Array.Empty<Cleaner>();

        private sealed class Real { public TrashItem Item; public Vector3 Pos; public Quaternion Rot; }
        private static readonly Dictionary<int, Real> _real = new Dictionary<int, Real>();   // instanced idx -> its materialised real
        private static readonly List<int> _remove = new List<int>();
        private static readonly int[] _scan = new int[64];

        internal static int RealCount => _real.Count;

        internal static void Tick()
        {
            if (!Enabled || InstancedTrash.Count <= 0) return;
            TrashManager tm = GameTrash.TrashManagerOrNull();
            if (tm == null) return;

            // 1) refind cleaners + rebuild the spatial grid periodically.
            if (_frame % FindCleanersEvery == 0) _cleaners = FindCleaners();
            if (_frame % RebuildEvery == 0) InstancedTrash.BuildGrid();
            _frame++;

            // 2) maintenance pass over our materialised items: drop ones the cleaner collected (Item == null ->
            //    permanently gone) and demote ones no longer near any cleaner (and not a cleaner's target) back to
            //    data so they don't pile up as the cleaners roam.
            _remove.Clear();
            foreach (var kv in _real)
            {
                Real r = kv.Value;
                if (r.Item == null) { InstancedTrash.Kill(kv.Key); _remove.Add(kv.Key); continue; }   // collected / destroyed
                Vector3 ip;
                try { ip = r.Item.transform.position; } catch { InstancedTrash.Kill(kv.Key); _remove.Add(kv.Key); continue; }
                if (!NearAnyCleaner(ip) && !IsAnyCleanerTarget(r.Item))
                {
                    // demote back to data at its current pose (the cleaner walked off without taking it)
                    try { InstancedTrash.Restore(kv.Key, ip, r.Item.transform.rotation); r.Item.DestroyTrash(); }
                    catch { InstancedTrash.Kill(kv.Key); }
                    _remove.Add(kv.Key);
                }
            }
            for (int i = 0; i < _remove.Count; i++) _real.Remove(_remove[i]);

            if (_cleaners.Length == 0) return;

            // 3) materialise data items near each cleaner, up to a per-cleaner budget.
            int budget = _cleaners.Length * PerCleaner - _real.Count;
            if (budget <= 0) return;
            for (int c = 0; c < _cleaners.Length && budget > 0; c++)
            {
                Cleaner cl = _cleaners[c];
                if (cl == null) continue;
                Vector3 cp;
                try { cp = cl.transform.position; } catch { continue; }
                int n = InstancedTrash.QueryGrid(cp.x, cp.z, Range, _scan, _scan.Length);
                for (int k = 0; k < n && budget > 0; k++)
                {
                    int idx = _scan[k];
                    if (_real.ContainsKey(idx)) continue;       // already real (this actor)
                    string id = InstancedTrash.GetTypeId(idx);
                    if (string.IsNullOrEmpty(id)) continue;
                    Vector3 pos = InstancedTrash.GetPosition(idx);
                    Quaternion rot = InstancedTrash.GetRotation(idx);
                    TrashItem item = null;
                    RouteHook.Suppress = true;                  // our own create -> route hook must NOT re-absorb it
                    // startKinematic=FALSE: spawn it as a LIVE (physics-active) loose item, not a frozen one. The
                    // cleaner's native AI only collects litter that is properly registered to the property; a
                    // kinematic, never-settled item is ignored (verified live: player-thrown items get collected,
                    // raw-materialized frozen ones do not).
                    try { item = tm.CreateTrashItem(id, pos, rot, Vector3.zero, System.Guid.NewGuid().ToString(), false); }
                    catch (Exception e) { Core.Log?.Warning("[cleaner] materialize: " + e.Message); }
                    finally { RouteHook.Suppress = false; }
                    if (item == null) continue;
                    // Replicate what a dropped/thrown item does so the cleaner recognises it as collectable loose
                    // litter: keep physics active and (re)run the property association (TrashItem.RecheckProperty,
                    // :866). Without this the item sits in trashItems but is never targeted by PickUpTrashBehaviour.
                    try { item.SetPhysicsActive(true); item.RecheckProperty(); }
                    catch (Exception e) { Core.Log?.Warning("[cleaner] register: " + e.Message); }
                    InstancedTrash.MarkRealCreated();
                    InstancedTrash.SetRealized(idx, true);       // keep both actors + the route hook off it
                    InstancedTrash.Hide(idx);                    // hide the instanced copy; the real one renders/interacts
                    _real[idx] = new Real { Item = item, Pos = pos, Rot = rot };
                    budget--;
                }
            }
        }

        // Cleaners via the GAME's own employee registry (EmployeeManager.GetEmployeesByType) instead of a
        // FindObjectsOfType scene scan - the canonical, cheaper way (verified against S1API + the game source;
        // S1API exposes no cleaner list of its own).
        private static Cleaner[] FindCleaners()
        {
            var list = new List<Cleaner>();
            try
            {
                EmployeeManager em = NetworkSingleton<EmployeeManager>.Instance;
                if (em != null)
                {
                    Il2CppSystem.Collections.Generic.List<Employee> emps = em.GetEmployeesByType(EEmployeeType.Cleaner);
                    if (emps != null)
                        for (int i = 0; i < emps.Count; i++)
                        {
                            Employee e = emps[i]; if (e == null) continue;
                            Cleaner cl = e.TryCast<Cleaner>(); if (cl != null) list.Add(cl);
                        }
                }
            }
            catch (Exception ex) { Core.Log?.Warning("[cleaner] find: " + ex.Message); }
            return list.ToArray();
        }

        private static bool NearAnyCleaner(Vector3 p)
        {
            for (int c = 0; c < _cleaners.Length; c++)
            {
                Cleaner cl = _cleaners[c];
                if (cl == null) continue;
                Vector3 cp;
                try { cp = cl.transform.position; } catch { continue; }
                float dx = p.x - cp.x, dz = p.z - cp.z;
                if (dx * dx + dz * dz <= DemoteRange2) return true;
            }
            return false;
        }

        // Never demote an item a cleaner is currently navigating to (its PickUpTrashBehaviour.TargetTrash).
        private static bool IsAnyCleanerTarget(TrashItem item)
        {
            for (int c = 0; c < _cleaners.Length; c++)
            {
                Cleaner cl = _cleaners[c];
                if (cl == null) continue;
                try { if (cl.PickUpTrashBehaviour != null && cl.PickUpTrashBehaviour.TargetTrash == item) return true; }
                catch { }
            }
            return false;
        }

        // Diagnostic: how many cleaners exist, where, and how much DATA trash is within reach of each.
        internal static void Diagnose()
        {
            try
            {
                Cleaner[] arr = FindCleaners();
                InstancedTrash.BuildGrid();
                int[] buf = new int[256];
                Core.Log?.Msg($"[cleaner] DIAG: {arr.Length} cleaner(s) via EmployeeManager registry; field={InstancedTrash.Count} (live {InstancedTrash.LiveCount}); range={Range}m, perCleaner={PerCleaner}; materialised={_real.Count}");
                for (int i = 0; i < arr.Length; i++)
                {
                    Cleaner cl = arr[i]; if (cl == null) continue;
                    Vector3 cp = cl.transform.position;
                    int near = InstancedTrash.QueryGrid(cp.x, cp.z, Range, buf, buf.Length);
                    int near50 = InstancedTrash.QueryGrid(cp.x, cp.z, 50f, buf, buf.Length);
                    Core.Log?.Msg($"[cleaner]   #{i} at ({cp.x:F0},{cp.y:F0},{cp.z:F0}): data within {Range}m={near}, within 50m={near50}");
                }
            }
            catch (Exception e) { Core.Log?.Warning("[cleaner] diag: " + e.Message); }
        }

        /// <summary>Demote/destroy all cleaner-materialised reals (save/teardown), leaving the data intact.</summary>
        internal static void ClearAll()
        {
            foreach (var kv in _real)
            {
                Real r = kv.Value;
                if (r != null && r.Item != null)
                {
                    try { InstancedTrash.Restore(kv.Key, r.Item.transform.position, r.Item.transform.rotation); r.Item.DestroyTrash(); }
                    catch { InstancedTrash.Kill(kv.Key); }
                }
                else InstancedTrash.Kill(kv.Key);
            }
            _real.Clear();
            _cleaners = Array.Empty<Cleaner>();
        }
    }
}
