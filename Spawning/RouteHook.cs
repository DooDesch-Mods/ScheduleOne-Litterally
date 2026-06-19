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
        [ThreadStatic] internal static bool Suppress;        // TRUE around OUR OWN create calls so they stay REAL (not absorbed)
        // Good-citizen gate: absorb ONLY the GAME'S OWN generator trash. Set TRUE only while
        // TrashGenerator.GenerateTrash / GenerateMaxTrash is running (see the patches at the bottom of this file),
        // so trash that ANOTHER mod creates directly via TrashManager.CreateTrashItem (outside a generator pass)
        // is left REAL and keeps working. This is an allowlist (fail-closed: an unknown create is never stolen),
        // which is strictly safer than Suppress alone (a denylist that only protects creates WE wrap). When OFF
        // (default), routing never runs at all. Toggle off via `tv route absorbany on` if a user wants the old
        // absorb-everything behaviour. See docs/ARCHITECTURE.md section 10.
        internal static bool AbsorbAny = false;              // if true, absorb ALL game trash (legacy), not just generator trash
        [ThreadStatic] internal static bool GeneratorActive; // TRUE while the game's own generator is creating trash
        [ThreadStatic] private static int _genDepth;         // shared nesting depth across BOTH generator methods (one may call the other)
        // Generator patches call these so the absorb window stays open across nested GenerateMaxTrash->GenerateTrash.
        internal static void EnterGenerator() { if (_genDepth++ == 0) GeneratorActive = true; }
        internal static void ExitGenerator() { if (--_genDepth <= 0) { _genDepth = 0; GeneratorActive = false; } }

        // The game spawns trash ABOVE the ground and lets physics settle it. If we capture the pose immediately
        // it floats, so we keep the real item alive a moment, let it settle, then virtualize at its SETTLED
        // transform (natural orientation, flush on the ground - like the rest of the field). _tracked dedups the
        // 4 echo-calls per item (persists until the item is virtualized, not just per-frame).
        private sealed class SettleItem { public TrashItem Item; public Rigidbody Rb; public int Age; public int Rest; }
        private static readonly Dictionary<int, SettleItem> _settling = new Dictionary<int, SettleItem>();
        private static readonly HashSet<int> _tracked = new HashSet<int>();
        private static readonly List<int> _doneSettling = new List<int>();
        private static readonly List<TrashItem> _destroyQueue = new List<TrashItem>();
        internal static int Absorbed, Skipped, AtCap;
        private const int SettleMinFrames = 6;       // let it actually start falling before judging it settled
        private const int SettleMaxFrames = 150;     // ~2.5s cap: virtualize even if it never fully sleeps
        private const float SettleVel2 = 0.01f;
        private const int SettleRestFrames = 8;      // require SUSTAINED rest (this many consecutive low-velocity frames) before capturing, so a tipping/wobbling item finishes settling instead of freezing mid-tip in an odd pose (e.g. a bottle balanced on its edge)
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
            // Good citizen: only absorb the game's OWN generator output. Trash another mod created directly
            // (no generator pass on the stack) stays real. AbsorbAny restores the legacy absorb-everything mode.
            if (!GeneratorActive && !AbsorbAny) { Skipped++; return; }

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
                    bool atRest = s.Rb == null || s.Rb.velocity.sqrMagnitude < SettleVel2;
                    s.Rest = atRest ? s.Rest + 1 : 0;
                    // Capture only after SUSTAINED rest, not a single low-velocity frame (which can be the apex of a
                    // wobble/tip) - so items finish settling into a natural pose instead of freezing mid-tip.
                    bool settled = s.Age >= SettleMaxFrames || (s.Age >= SettleMinFrames && s.Rest >= SettleRestFrames);
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
            _genDepth = 0; GeneratorActive = false;   // belt-and-suspenders: never leave the absorb window stuck open
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
                        var tm = GameTrash.TrashManagerOrNull();
                        int mgr = -1; try { if (tm != null) mgr = GameTrash.TrashItemCount(tm); } catch { }
                        Core.Log?.Msg($"[route] STAT  active={Active}  absorbMode={(AbsorbAny ? "ANY (legacy)" : "generator-only (good-citizen)")}  instanced={InstancedTrash.Count}  realTrashItems(manager)={mgr}  settling={_settling.Count}  absorbed={Absorbed} skipped(echo+othermod)={Skipped} atCap={AtCap}");
                        if (Probe) Core.Log?.Msg($"[route-probe]  public calls={PubCalls} distinct={_distinctPub.Count}  private calls={PrivCalls} distinct={_distinctPriv.Count}");
                        break;
                    }
                case "absorbany":
                    AbsorbAny = p.Length > 3 ? (p[3] == "on" || p[3] == "1" || p[3] == "true") : !AbsorbAny;
                    Core.Log?.Msg($"[route] absorbAny = {AbsorbAny} ({(AbsorbAny ? "absorb ALL trash incl. other mods' - legacy" : "absorb ONLY the game generator's trash - good-citizen default")}).");
                    break;
                case "testlimit":
                    // EMPIRICALLY RESOLVED 2026-06-19: reading TrashManager.TRASH_ITEM_LIMIT is fine (=2000), but
                    // WRITING it hard-crashes the game (log stops mid-write, process dies). It is NOT a literal
                    // C# const (a real IL2CPP field exists), yet the field is effectively read-only - never write
                    // it. See docs/ARCHITECTURE.md. This command is now read-only.
                    try { Core.Log?.Msg($"[limit] TRASH_ITEM_LIMIT = {TrashManager.TRASH_ITEM_LIMIT} (READ-ONLY; writing it hard-crashes the game - verified)."); }
                    catch (Exception e) { Core.Log?.Warning("[limit] read threw: " + e.Message); }
                    break;
                case "clearreal":
                    try { var tm = GameTrash.TrashManagerOrNull(); if (tm != null) tm.DestroyAllTrash(); Core.Log?.Msg("[route] DestroyAllTrash()"); } catch (Exception e) { Core.Log?.Warning("[route] clear failed: " + e.Message); }
                    break;
                default:
                    Core.Log?.Msg("[route] usage: tv route on|off | boost [mult] | unboost | burst [n] | stat | probe on|off | absorbany on|off | clearreal");
                    break;
            }
        }

        private static void Burst(int perGenerator)
        {
            if (!GameTrash.TryGetPlayerPosition(out Vector3 pp)) { Core.Log?.Warning("[route] no player"); return; }
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

    // Good-citizen gate: mark the window in which the GAME'S OWN generator creates trash. RouteHook.OnCreate
    // absorbs ONLY while this window is open, so trash created OUTSIDE a generator pass (another mod's explicit
    // CreateTrashItem) is left real. GenerateMaxTrash is the natural cadence (Awake/Start/SleepStart);
    // GenerateTrash(int) is the burst path. Nesting-safe via a depth counter (the methods may call each other).
    // Exit via a FINALIZER (not a Postfix) so the absorb window always closes even if the generator throws -
    // otherwise a leaked +1 on the depth counter would leave GeneratorActive stuck true and we'd start stealing
    // other mods' trash. The finalizer must not swallow the original exception, so it returns it unchanged.
    [HarmonyPatch(typeof(TrashGenerator), "GenerateMaxTrash")]
    internal static class TG_GenerateMaxTrash_Patch
    {
        private static void Prefix() { RouteHook.EnterGenerator(); }
        private static Exception Finalizer(Exception __exception) { RouteHook.ExitGenerator(); return __exception; }
    }

    [HarmonyPatch(typeof(TrashGenerator), "GenerateTrash", new Type[] { typeof(int) })]
    internal static class TG_GenerateTrash_Patch
    {
        private static void Prefix() { RouteHook.EnterGenerator(); }
        private static Exception Finalizer(Exception __exception) { RouteHook.ExitGenerator(); return __exception; }
    }
}
