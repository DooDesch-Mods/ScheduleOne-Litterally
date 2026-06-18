using System;
using UnityEngine;
using HarmonyLib;
using Il2CppScheduleOne.Trash;

namespace Trashville.Spawning
{
    /// <summary>
    /// PHASE 0 spike for routing the GAME's own generated trash through our instanced field. This stage is
    /// LOG-ONLY: it postfixes BOTH trash-create methods and just counts which one fires when the game's
    /// TrashGenerator spawns, so we can answer the load-bearing unknowns BEFORE writing the risky absorb path:
    ///   - Which method does the generator use - public CreateTrashItem, private CreateAndReturnTrashItem, or both
    ///     (the public one may call the private internally, which would double-count/double-absorb)?
    ///   - Can we trigger generation on demand to test without walking the map?
    /// It does NOT absorb, destroy, raise TRASH_ITEM_LIMIT, or boost generators yet. `Suppress` already lets the
    /// real feature exclude our own mod-created items; for the probe we simply clear our field first.
    /// </summary>
    internal static class RouteHook
    {
        internal static bool Probe = false;                  // Phase 0: enable log-only counting
        [ThreadStatic] internal static bool Suppress;        // set around mod-owned create calls so they are not counted/absorbed

        internal static int PubCalls, PrivCalls, PubNull, PrivNull;
        private static int _logged;
        private static readonly System.Collections.Generic.HashSet<int> _distinctPub = new System.Collections.Generic.HashSet<int>();
        private static readonly System.Collections.Generic.HashSet<int> _distinctPriv = new System.Collections.Generic.HashSet<int>();

        internal static void ResetCounts() { PubCalls = PrivCalls = PubNull = PrivNull = 0; _logged = 0; _distinctPub.Clear(); _distinctPriv.Clear(); }

        internal static void Note(bool isPrivate, TrashItem result)
        {
            if (!Probe || Suppress) return;
            int iid = 0; try { if (result != null) iid = result.GetInstanceID(); } catch { }
            if (isPrivate) { PrivCalls++; if (result == null) PrivNull++; else _distinctPriv.Add(iid); }
            else { PubCalls++; if (result == null) PubNull++; else _distinctPub.Add(iid); }
            if (_logged < 16)
            {
                _logged++;
                string id = "?"; Vector3 p = Vector3.zero;
                try { if (result != null) { id = result.ID; p = result.transform.position; } } catch { }
                Core.Log?.Msg($"[route-probe] {(isPrivate ? "CreateAndReturnTrashItem" : "CreateTrashItem")} -> {(result == null ? "NULL" : id)} @({p.x:F0},{p.y:F0},{p.z:F0})");
            }
        }

        // ----- console: tv route <probe on|off | burst [n] | stat | clearreal> -----
        internal static void Command(string[] p)
        {
            string sub = p.Length > 2 ? p[2] : "stat";
            switch (sub)
            {
                case "probe":
                    Probe = p.Length > 3 ? (p[3] == "on" || p[3] == "1" || p[3] == "true") : !Probe;
                    ResetCounts();
                    Core.Log?.Msg($"[route-probe] probe = {Probe} (counts reset). Now: tv route burst, then tv route stat.");
                    break;
                case "burst":
                    Burst(p.Length > 3 && int.TryParse(p[3], out int bn) ? bn : 30);
                    break;
                case "stat":
                    Core.Log?.Msg($"[route-probe] STAT  public CreateTrashItem: calls={PubCalls} distinct-items={_distinctPub.Count} (null {PubNull})");
                    Core.Log?.Msg($"[route-probe]        private CreateAndReturnTrashItem: calls={PrivCalls} distinct-items={_distinctPriv.Count} (null {PrivNull})");
                    Core.Log?.Msg($"[route-probe]   -> {(PubCalls > 0 && PrivCalls > 0 ? "BOTH fire" : PrivCalls > 0 ? "ONLY private" : PubCalls > 0 ? "ONLY public" : "NEITHER - generator uses another path!")}; absorb where distinct-items == real spawned count, dedup the rest by instance id.");
                    break;
                case "clearreal":
                    try { var tm = TrashSpawner.TrashManagerOrNull(); if (tm != null) tm.DestroyAllTrash(); Core.Log?.Msg("[route-probe] DestroyAllTrash() called"); } catch (Exception e) { Core.Log?.Warning("[route-probe] clear failed: " + e.Message); }
                    break;
                default:
                    Core.Log?.Msg("[route-probe] usage: tv route probe on|off | burst [n] | stat | clearreal");
                    break;
            }
        }

        // Trigger the game's own generators to spawn a burst near the player, so we can observe which create
        // method fires without having to physically walk into a fresh region.
        private static void Burst(int perGenerator)
        {
            if (!Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) { Core.Log?.Warning("[route-probe] no player"); return; }
            Il2CppSystem.Collections.Generic.List<TrashGenerator> all = null;
            try { all = TrashGenerator.AllGenerators; } catch (Exception e) { Core.Log?.Warning("[route-probe] AllGenerators: " + e.Message); }
            if (all == null) { Core.Log?.Warning("[route-probe] AllGenerators null"); return; }
            int total = all.Count, near = 0, fired = 0;
            for (int i = 0; i < total; i++)
            {
                TrashGenerator g = all[i];
                if (g == null) continue;
                float dist;
                try { dist = Vector3.Distance(g.transform.position, pp); } catch { continue; }
                if (dist > 80f) continue;   // only nearby generators so the burst lands around us
                near++;
                try { g.GenerateTrash(perGenerator); fired++; }
                catch (Exception e) { Core.Log?.Warning("[route-probe] GenerateTrash failed: " + e.Message); }
            }
            Core.Log?.Msg($"[route-probe] burst: {total} generators total, {near} within 80m, GenerateTrash({perGenerator}) called on {fired}. Now: tv route stat.");
        }
    }

    [HarmonyPatch(typeof(TrashManager), "CreateTrashItem", new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(bool) })]
    internal static class TM_CreateTrashItem_Patch
    {
        private static void Postfix(TrashItem __result) { try { RouteHook.Note(false, __result); } catch { } }
    }

    [HarmonyPatch(typeof(TrashManager), "CreateAndReturnTrashItem", new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(bool) })]
    internal static class TM_CreateAndReturnTrashItem_Patch
    {
        private static void Postfix(TrashItem __result) { try { RouteHook.Note(true, __result); } catch { } }
    }
}
