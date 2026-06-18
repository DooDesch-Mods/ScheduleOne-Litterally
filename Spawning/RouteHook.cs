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

        // dedup within a generation burst (all echoes of one item are same-frame); cleared each Tick.
        private static readonly HashSet<int> _seen = new HashSet<int>();
        private static readonly List<TrashItem> _destroyQueue = new List<TrashItem>();
        internal static int Absorbed, Skipped, AtCap;

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
            if (!_seen.Add(iid)) { Skipped++; return; }      // an echo of an already-handled item

            try
            {
                string id = result.ID;
                Vector3 pos = result.transform.position;
                Quaternion rot = result.transform.rotation;
                int t = InstancedTrash.AddOne(id, pos, rot);
                if (t == -2) { AtCap++; return; }            // managed cap reached -> leave this one real (self-throttles)
                if (t < 0) return;                           // unrenderable id -> leave real, do NOT destroy
                Absorbed++;
                _destroyQueue.Add(result);                   // defer destroy to Tick (out of the RPC fan-out)
            }
            catch (Exception e) { Core.Log?.Warning("[route] absorb: " + e.Message); }
        }

        // Per-frame: destroy the absorbed reals (their data already renders), then reset the same-frame dedup set.
        internal static void Tick()
        {
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
            _seen.Clear();   // echoes of any item are synchronous within a frame, so per-frame dedup is sufficient
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
            PubCalls = PrivCalls = _logged = 0; _distinctPub.Clear(); _distinctPriv.Clear();
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
                    Active = false; Tick();
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
                        Core.Log?.Msg($"[route] STAT  active={Active}  instanced={InstancedTrash.Count}  realTrashItems(manager)={mgr}  absorbed={Absorbed} skippedEchoes={Skipped} atCap={AtCap}");
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
