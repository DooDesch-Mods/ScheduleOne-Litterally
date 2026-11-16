using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.Trash;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.DevUtilities;
using Litterally.Instanced;

namespace Litterally.Spawning
{
    /// <summary>
    /// Keeps base-game Cleaner NPCs working with the instanced field. A cleaner only takes real TrashItems that lie
    /// in the pickup zone of one of its assigned trash cans: the can collects them with an OverlapSphere on the Trash
    /// layer and keeps those inside its square (TrashContainerItem.IsPointInPickupZone: PickupSquareWidth on x and z,
    /// 2 m on y). The cleaner picks its next target from that list and has no concept of our data. So we MATERIALIZE
    /// the data items inside those zones into real TrashItems (the same CreateTrashItem path the player path uses, so
    /// they are indistinguishable from game-spawned trash). The cleaner then walks to and collects them exactly as in
    /// the base game; when it destroys one we drop the data entry permanently. Items that leave every zone (and are
    /// not a cleaner's current target) are demoted back to data. Materializing anywhere else would fill the budget
    /// with items no cleaner can take.
    ///
    /// This is the "no feature loss" piece: cleaners behave 1:1 like base game, just over a cheap backing store.
    /// Coordinates with the player Virtualizer purely through InstancedTrash's _realized flag (QueryGrid /
    /// CollectVisible both skip _realized), so an item is never materialized by both actors at once.
    /// </summary>
    internal static class CleanerActor
    {
        internal static bool Enabled = true;        // on by default - cleaners are a base-game feature
        // Diagnostic radius only ('tv cleaner diag'): how much data trash lies around each cleaner.
        internal static float Range = 30f;
        internal static int PerCleaner = 6;          // cap concurrent materialised items per cleaner
        private const float ZoneHeight = 2f;         // TrashContainerItem.IsPointInPickupZone: |dy| <= 2
        private const float DemoteMargin = 1.5f;     // hysteresis: demote only once well outside every zone
        private const int RebuildEvery = 30;         // grid rebuild cadence (the field is mostly static)
        private const int FindCleanersEvery = 120;   // refind cleaners and their cans (they change rarely)

        private static int _frame;
        private static Cleaner[] _cleaners = Array.Empty<Cleaner>();

        // Pickup zone of one assigned can: centre and half-width on x and z.
        private struct Zone { public Vector3 Center; public float Half; }
        private static Zone[] _zones = Array.Empty<Zone>();

        private sealed class Real { public TrashItem Item; public Vector3 Pos; public Quaternion Rot; }
        private static readonly Dictionary<int, Real> _real = new Dictionary<int, Real>();   // instanced idx -> its materialised real
        private static readonly List<int> _remove = new List<int>();
        private static readonly int[] _scan = new int[64];

        internal static int RealCount => _real.Count;

        /// <summary>Materialized items a cleaner took (the real item vanished while we held it).</summary>
        internal static int Collected { get; private set; }

        /// <summary>Copy the instance indices of items currently materialized near cleaners into buf; returns the
        /// count written. For the debug overlay (highlight the cleaner-active set). No allocation.</summary>
        internal static int CopyRealIndices(int[] buf)
        {
            if (buf == null) return 0;
            int n = 0;
            foreach (var kv in _real) { if (n >= buf.Length) break; buf[n++] = kv.Key; }
            return n;
        }

        /// <summary>Copy the CURRENT world position of each cleaner-materialized real item into buf (for a debug
        /// marker that follows the actual item, not its stale stored pose).</summary>
        internal static int CopyRealPositions(Vector3[] buf)
        {
            if (buf == null) return 0;
            int n = 0;
            foreach (var kv in _real)
            {
                if (n >= buf.Length) break;
                TrashItem it = kv.Value != null ? kv.Value.Item : null;
                if (it == null) continue;
                try { buf[n++] = it.transform.position; } catch { }
            }
            return n;
        }

        internal static void Tick()
        {
            if (!Enabled || InstancedTrash.Count <= 0) return;
            TrashManager tm = GameTrash.TrashManagerOrNull();
            if (tm == null) return;

            // 1) refind cleaners + rebuild the spatial grid periodically.
            if (_frame % FindCleanersEvery == 0) { _cleaners = FindCleaners(); _zones = FindZones(_cleaners); }
            if (_frame % RebuildEvery == 0) InstancedTrash.BuildGrid();
            _frame++;

            // 2) maintenance pass over our materialised items: drop ones the cleaner collected (Item == null ->
            //    permanently gone) and demote ones no longer near any cleaner (and not a cleaner's target) back to
            //    data so they don't pile up as the cleaners roam.
            _remove.Clear();
            foreach (var kv in _real)
            {
                Real r = kv.Value;
                if (r.Item == null) { InstancedTrash.Kill(kv.Key); _remove.Add(kv.Key); Collected++; continue; }   // collected / destroyed
                Vector3 ip;
                try { ip = r.Item.transform.position; } catch { InstancedTrash.Kill(kv.Key); _remove.Add(kv.Key); continue; }
                if (!InAnyZone(ip, DemoteMargin) && !IsAnyCleanerTarget(r.Item))
                {
                    // demote back to data at its current pose (it left every pickup zone, or its can was unassigned)
                    try { InstancedTrash.Restore(kv.Key, ip, r.Item.transform.rotation); r.Item.DestroyTrash(); }
                    catch { InstancedTrash.Kill(kv.Key); }
                    _remove.Add(kv.Key);
                }
            }
            for (int i = 0; i < _remove.Count; i++) _real.Remove(_remove[i]);

            if (_cleaners.Length == 0 || _zones.Length == 0) return;

            // 3) materialise data items inside the pickup zones of the assigned cans, up to a per-cleaner budget.
            int budget = _cleaners.Length * PerCleaner - _real.Count;
            if (budget <= 0) return;
            for (int z = 0; z < _zones.Length && budget > 0; z++)
            {
                Zone zone = _zones[z];
                int n = InstancedTrash.QueryGrid(zone.Center.x, zone.Center.z, zone.Half * 1.415f, _scan, _scan.Length);
                for (int k = 0; k < n && budget > 0; k++)
                {
                    int idx = _scan[k];
                    if (_real.ContainsKey(idx)) continue;       // already real (this actor)
                    Vector3 pos = InstancedTrash.GetPosition(idx);
                    if (!InZone(zone, pos, 0f)) continue;       // the circle query also returns the square's outside corners
                    string id = InstancedTrash.GetTypeId(idx);
                    if (string.IsNullOrEmpty(id)) continue;
                    Quaternion rot = InstancedTrash.GetRotation(idx);
                    TrashItem item = null;
                    RouteHook.Suppress = true;                  // our own create -> route hook must NOT re-absorb it
                    // Spawned as a live dynamic item with colliders on (the 0.4.7 default). Trash cans find litter
                    // with Physics.OverlapSphere on the "Trash" layer (TrashContainerItem.CheckTrashItems) and the
                    // cleaner picks from that list, so a real item with enabled colliders is all it needs.
                    try { item = tm.CreateTrashItem(id, pos, rot, Vector3.zero, System.Guid.NewGuid().ToString()); }
                    catch (Exception e) { Core.Log?.Warning("[cleaner] materialize: " + e.Message); }
                    finally { RouteHook.Suppress = false; }
                    if (item == null) continue;
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

        // Pickup zones of every can assigned to one of the cleaners (Cleaner.configuration.binItems).
        private static Zone[] FindZones(Cleaner[] cleaners)
        {
            var list = new List<Zone>();
            for (int c = 0; c < cleaners.Length; c++)
            {
                try
                {
                    var cfg = cleaners[c] != null ? cleaners[c].configuration : null;
                    var bins = cfg != null ? cfg.binItems : null;
                    if (bins == null) continue;
                    for (int b = 0; b < bins.Count; b++)
                    {
                        var bin = bins[b];
                        if (bin == null) continue;
                        list.Add(new Zone { Center = bin.transform.position, Half = bin.PickupSquareWidth });
                    }
                }
                catch (Exception ex) { Core.Log?.Warning("[cleaner] zones: " + ex.Message); }
            }
            return list.ToArray();
        }

        private static bool InZone(Zone z, Vector3 p, float margin)
        {
            return Mathf.Abs(p.x - z.Center.x) <= z.Half + margin && Mathf.Abs(p.z - z.Center.z) <= z.Half + margin
                && Mathf.Abs(p.y - z.Center.y) <= ZoneHeight + margin;
        }

        private static bool InAnyZone(Vector3 p, float margin)
        {
            for (int z = 0; z < _zones.Length; z++) if (InZone(_zones[z], p, margin)) return true;
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
                Zone[] zones = FindZones(arr);
                Core.Log?.Msg($"[cleaner] DIAG: {arr.Length} cleaner(s) via EmployeeManager registry; field={InstancedTrash.Count} (live {InstancedTrash.LiveCount}); range={Range}m, perCleaner={PerCleaner}; materialised={_real.Count}; can zones={zones.Length}");
                for (int z = 0; z < zones.Length; z++)
                {
                    int near = InstancedTrash.QueryGrid(zones[z].Center.x, zones[z].Center.z, zones[z].Half * 1.415f, buf, buf.Length);
                    int inside = 0;
                    for (int k = 0; k < near; k++) if (InZone(zones[z], InstancedTrash.GetPosition(buf[k]), 0f)) inside++;
                    Vector3 zc = zones[z].Center;
                    Core.Log?.Msg($"[cleaner]   zone #{z} at ({zc.x:F0},{zc.y:F0},{zc.z:F0}) half {zones[z].Half:F1}m: data inside={inside}");
                }
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
