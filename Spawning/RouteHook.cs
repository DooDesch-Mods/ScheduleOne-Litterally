using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppScheduleOne.Trash;
using Trashville.Instanced;

namespace Trashville.Spawning
{
    /// <summary>
    /// Routes the GAME'S own generated trash through our instanced field so the game can spawn far more trash at
    /// high performance. A Harmony postfix on the trash-create method captures each newly created real TrashItem's
    /// pose, records it as pure DATA in InstancedTrash (rendered cheaply, materialized near the player for pickup),
    /// and destroys the real GameObject - so the world fills with trash without paying for N real objects/saveables.
    ///
    /// Phase-0 findings drive the design: each logical spawn fires CreateAndReturnTrashItem + CreateTrashItem twice
    /// each (public wraps private; FishNet echoes server+observer), all referencing the SAME real item. So we
    /// dedup by instance id (absorb exactly once) and patch the private creator. The DestroyTrash is DEFERRED to
    /// our per-frame Tick (not done inside the postfix) so we never delete the item mid RPC-fan-out.
    /// </summary>
    internal static class RouteHook
    {
        internal static bool Active = false;                 // real routing: absorb game trash into the instanced field
        internal static bool Probe = false;                  // diagnostic: log-only counting (Phase 0)
        [ThreadStatic] internal static bool Suppress;        // TRUE around mod-owned create calls so they stay REAL (not absorbed)

        // The game spawns trash ABOVE the ground and lets physics settle it. If we capture the pose immediately
        // it floats, so we keep the real item alive a moment, let it settle, then virtualize at its SETTLED
        // transform (natural orientation, flush on the ground - like the rest of the field). _tracked dedups the
        // 4 echo-calls per item (persists until the item is virtualized, not just per-frame).
        private sealed class SettleItem { public TrashItem Item; public Rigidbody Rb; public int Age; }
        private static readonly Dictionary<int, SettleItem> _settling = new Dictionary<int, SettleItem>();
        private static readonly HashSet<int> _tracked = new HashSet<int>();
        private static readonly List<int> _doneSettling = new List<int>();
        private static readonly List<TrashItem> _destroyQueue = new List<TrashItem>();
        internal static int Absorbed, Skipped, AtCap;
        private const int SettleMinFrames = 6;       // let it actually start falling before judging it settled
        private const int SettleMaxFrames = 150;     // ~2.5s cap: virtualize even if it never fully sleeps
        private const float SettleVel2 = 0.01f;
        private const int SettlingCap = 1200;        // burst overflow: above this, ground-snap immediately instead of tracking

        // ----- Phase 0 diagnostic counters -----
        internal static int PubCalls, PrivCalls;
        private static readonly HashSet<int> _distinctPub = new HashSet<int>();
        private static readonly HashSet<int> _distinctPriv = new HashSet<int>();
        private static int _logged;

        internal static void OnCreate(bool isPrivate, TrashItem result)
        {
            if (Suppress) return;                            // our own materialize/probe item -> leave real
            if (Probe && !Active) { ProbeNote(isPrivate, result); return; }
            if (!Active || result == null) return;

            int iid;
            try { iid = result.GetInstanceID(); } catch { return; }
            if (!_tracked.Add(iid)) { Skipped++; return; }   // an echo of an item we're already handling

            try
            {
                if (_settling.Count >= SettlingCap) { Record(iid, result, true); return; }   // burst overflow -> ground-snap now
                Rigidbody rb = null; try { rb = result.GetComponentInChildren<Rigidbody>(); } catch { }
                _settling[iid] = new SettleItem { Item = result, Rb = rb, Age = 0 };
            }
            catch (Exception e) { Core.Log?.Warning("[route] track: " + e.Message); _tracked.Remove(iid); }
        }

        private const int GroundMask = ~(1 << 10);   // everything except the Trash layer (10)
        private static int _heightLogged;

        // Capture an item's pose into the instanced field as data, then queue its real object for destruction.
        private static void Record(int iid, TrashItem item, bool groundSnap)
        {
            try
            {
                if (item == null) { _tracked.Remove(iid); return; }
                string id = item.ID;
                Vector3 pos = item.transform.position;
                Quaternion rot = item.transform.rotation;
                // Robustness: if the "settled" item is still well above the ground (early/timeout capture, or the
                // game placed it kinematic in the air), ground-snap so it can never float. Also a one-shot
                // diagnostic of the height-above-ground for the first few, to confirm grounding.
                if (Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit grh, 80f, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    float above = pos.y - grh.point.y;
                    if (_heightLogged < 10) { _heightLogged++; Core.Log?.Msg($"[route] settled '{id}' {above:F2}m above ground{(above > 0.4f ? " -> ground-snap" : "")}"); }
                    if (above > 0.4f) groundSnap = true;
                }
                int t = InstancedTrash.AddOne(id, pos, rot, groundSnap);
                if (t == -2) { AtCap++; _tracked.Remove(iid); return; }   // at managed cap -> leave it real
                if (t < 0) { _tracked.Remove(iid); return; }              // unrenderable id -> leave it real
                Absorbed++;
                _destroyQueue.Add(item);                                  // destroy next Tick (out of the RPC fan-out)
            }
            catch (Exception e) { Core.Log?.Warning("[route] record: " + e.Message); }
            finally { _tracked.Remove(iid); }
        }

        // Per-frame: advance settling items and virtualize the ones that have come to rest; then destroy the
        // already-virtualized reals.
        internal static void Tick()
        {
            if (_settling.Count > 0)
            {
                _doneSettling.Clear();
                foreach (var kv in _settling)
                {
                    SettleItem s = kv.Value;
                    if (s.Item == null) { _doneSettling.Add(kv.Key); _tracked.Remove(kv.Key); continue; }
                    s.Age++;
                    bool settled = s.Age >= SettleMaxFrames ||
                        (s.Age >= SettleMinFrames && (s.Rb == null || s.Rb.velocity.sqrMagnitude < SettleVel2));
                    if (settled) { Record(kv.Key, s.Item, false); _doneSettling.Add(kv.Key); }
                }
                for (int k = 0; k < _doneSettling.Count; k++) _settling.Remove(_doneSettling[k]);
            }

            if (_destroyQueue.Count > 0)
            {
                Suppress = true;
                try
                {
                    for (int k = 0; k < _destroyQueue.Count; k++)
                    {
                        TrashItem it = _destroyQueue[k];
                        if (it == null) continue;
                        try { it.DestroyTrash(); } catch { }
                        try { if (it != null) UnityEngine.Object.Destroy(it.gameObject); } catch { }
                    }
                }
                finally { Suppress = false; }
                _destroyQueue.Clear();
            }
        }

        // Drop all in-flight tracking (routing off / save): leave settling items as real game trash, clear state.
        internal static void ResetState()
        {
            _settling.Clear(); _tracked.Clear(); _doneSettling.Clear(); _destroyQueue.Clear();
        }

        private static void ProbeNote(bool isPrivate, TrashItem result)
        {
            int iid = 0; try { if (result != null) iid = result.GetInstanceID(); } catch { }
            if (isPrivate) { PrivCalls++; if (result != null) _distinctPriv.Add(iid); }
            else { PubCalls++; if (result != null) _distinctPub.Add(iid); }
            if (_logged < 16)
            {
                _logged++;
                string id = "?"; Vector3 p = Vector3.zero;
                try { if (result != null) { id = result.ID; p = result.transform.position; } } catch { }
                Core.Log?.Msg($"[route-probe] {(isPrivate ? "CreateAndReturnTrashItem" : "CreateTrashItem")} -> {(result == null ? "NULL" : id)} @({p.x:F0},{p.y:F0},{p.z:F0})");
            }
        }

        internal static void ResetCounts()
        {
            PubCalls = PrivCalls = _logged = _heightLogged = 0; _distinctPub.Clear(); _distinctPriv.Clear();
            Absorbed = Skipped = AtCap = 0;
        }

        // ----- console: tv route <on|off | probe on|off | burst [n] | stat | clearreal | boost [mult] | unboost> -----
        internal static void Command(string[] p)
        {
            string sub = p.Length > 2 ? p[2] : "stat";
            switch (sub)
            {
                case "on":
                    Active = true; Probe = false; ResetCounts();
                    Core.Log?.Msg("[route] routing ON: game-generated trash is absorbed into the instanced field.");
                    break;
                case "off":
                    Active = false; Tick(); ResetState();
                    GeneratorBoost.Restore();
                    InstancedTrash.Clear();
                    Core.Log?.Msg("[route] routing OFF: generators restored, instanced field cleared.");
                    break;
                case "boost":
                    GeneratorBoost.Apply(p.Length > 3 && int.TryParse(p[3], out int mb) ? mb : 20);
                    break;
                case "unboost":
                    GeneratorBoost.Restore();
                    break;
                case "probe":
                    Probe = p.Length > 3 ? (p[3] == "on" || p[3] == "1") : !Probe; Active = false; ResetCounts();
                    Core.Log?.Msg($"[route-probe] probe = {Probe} (log-only). tv route burst, then tv route stat.");
                    break;
                case "burst":
                    Burst(p.Length > 3 && int.TryParse(p[3], out int bn) ? bn : 30);
                    break;
                case "stat":
                    {
                        var tm = TrashSpawner.TrashManagerOrNull();
                        int mgr = -1; try { if (tm != null) mgr = TrashSpawner.TrashItemCount(tm); } catch { }
                        Core.Log?.Msg($"[route] STAT  active={Active}  instanced={InstancedTrash.Count}  realTrashItems(manager)={mgr}  settling={_settling.Count}  absorbed={Absorbed} skippedEchoes={Skipped} atCap={AtCap}");
                        if (Probe) Core.Log?.Msg($"[route-probe]  public calls={PubCalls} distinct={_distinctPub.Count}  private calls={PrivCalls} distinct={_distinctPriv.Count}");
                        break;
                    }
                case "clearreal":
                    try { var tm = TrashSpawner.TrashManagerOrNull(); if (tm != null) tm.DestroyAllTrash(); Core.Log?.Msg("[route] DestroyAllTrash()"); } catch (Exception e) { Core.Log?.Warning("[route] clear failed: " + e.Message); }
                    break;
                default:
                    Core.Log?.Msg("[route] usage: tv route on|off | boost [mult] | unboost | burst [n] | stat | probe on|off | clearreal");
                    break;
            }
        }

        private static void Burst(int perGenerator)
        {
            if (!TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) { Core.Log?.Warning("[route] no player"); return; }
            Il2CppSystem.Collections.Generic.List<TrashGenerator> all = null;
            try { all = TrashGenerator.AllGenerators; } catch (Exception e) { Core.Log?.Warning("[route] AllGenerators: " + e.Message); }
            if (all == null) { Core.Log?.Warning("[route] AllGenerators null"); return; }
            int total = all.Count, near = 0, fired = 0;
            for (int i = 0; i < total; i++)
            {
                TrashGenerator g = all[i];
                if (g == null) continue;
                float dist; try { dist = Vector3.Distance(g.transform.position, pp); } catch { continue; }
                if (dist > 120f) continue;
                near++;
                try { g.GenerateTrash(perGenerator); fired++; } catch (Exception e) { Core.Log?.Warning("[route] GenerateTrash: " + e.Message); }
            }
            Core.Log?.Msg($"[route] burst: {total} generators, {near} within 120m, GenerateTrash({perGenerator}) on {fired}. tv route stat.");
        }
    }

    [HarmonyPatch(typeof(TrashManager), "CreateAndReturnTrashItem", new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(bool) })]
    internal static class TM_CreateAndReturnTrashItem_Patch
    {
        private static void Postfix(TrashItem __result) { try { RouteHook.OnCreate(true, __result); } catch { } }
    }

    [HarmonyPatch(typeof(TrashManager), "CreateTrashItem", new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(bool) })]
    internal static class TM_CreateTrashItem_Patch
    {
        // Public wrapper - only used for the Phase-0 probe; real absorb dedups so this is harmless when Active.
        private static void Postfix(TrashItem __result) { try { RouteHook.OnCreate(false, __result); } catch { } }
    }
}
