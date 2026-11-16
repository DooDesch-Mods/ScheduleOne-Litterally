#if DEBUG
using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Dragging;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.ObjectScripts;
using Litterally.Instanced;
using Litterally.Spawning;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace Litterally.Profiling
{
    /// <summary>
    /// DEBUG-only drag test. 'tv dragtest place' puts one instanced item in front of the camera, a little below the
    /// drag hold point, so the Virtualizer materializes it frozen (performance mode). 'tv dragtest start' starts the
    /// drag through DragManager.StartDragging, the call the game's own "Pick up" interaction makes, and 1.5 s later
    /// logs whether the item woke up and how far it moved toward the hold point. 'tv dragtest stop' drops it.
    /// </summary>
    internal static class DragTest
    {
        private const float HoldDistance = 1.25f, BelowHold = 0.8f, CheckAfter = 1.5f;
        private static int _idx = -1;
        private static TrashItem _item;
        private static Vector3 _before;
        private static float _checkAt = -1f;

        internal static void Command(string[] p)
        {
            string mode = p.Length > 2 ? p[2].ToLowerInvariant() : "status";
            if (mode == "place") Place();
            else if (mode == "start") StartDrag();
            else if (mode == "stop") StopDrag();
            else Log("idx=" + _idx + " item=" + (_item != null) + " kinematic=" + Kinematic(_item));
        }

        private static void Place()
        {
            Transform cam = CameraTransform();
            if (cam == null) { Log("place: no player camera."); return; }
            if (Virtualizer.Collide) Log("place: collide mode is on, the item spawns dynamic, not frozen.");
            string id = TypeId();
            if (id == null) { Log("place: no trash type in the field."); return; }
            Vector3 pos = cam.position + cam.forward * HoldDistance + Vector3.down * BelowHold;
            int t = InstancedTrash.AddOne(id, pos, Quaternion.identity, groundSnap: false);
            if (t < 0) { Log("place: AddOne refused type " + id + " (" + t + ")."); return; }
            _idx = InstancedTrash.Count - 1;
            _item = null;
            _checkAt = -1f;
            Log("placed idx=" + _idx + " type=" + id + " at " + V(pos));
        }

        private static void StartDrag()
        {
            if (_idx < 0) { Log("start: run 'tv dragtest place' first."); return; }
            _item = Virtualizer.RealItemOf(_idx);
            if (_item == null) { Log("start: the Virtualizer has not materialized idx " + _idx + " yet."); return; }
            DragManager dm = NetworkSingleton<DragManager>.Instance;
            TrashItemDraggable drag = _item._draggable;
            if (dm == null || drag == null) { Log("start: DragManager=" + (dm != null) + " draggable=" + (drag != null) + "."); return; }
            _before = _item.transform.position;
            Log("before: kinematic=" + Kinematic(_item) + " pos=" + V(_before));
            dm.StartDragging(drag);
            _checkAt = Time.time + CheckAfter;
        }

        private static void StopDrag()
        {
            DragManager dm = NetworkSingleton<DragManager>.Instance;
            if (dm != null && dm.IsDragging) { dm.StopDragging(Vector3.zero); Log("stopped dragging."); }
            else Log("stop: nothing is being dragged.");
            _checkAt = -1f;
        }

        internal static void Tick()
        {
            if (_checkAt < 0f || Time.time < _checkAt) return;
            _checkAt = -1f;
            if (_item == null) { Log("after: the item is gone."); return; }
            Vector3 now = _item.transform.position;
            DragManager dm = NetworkSingleton<DragManager>.Instance;
            bool dragging = dm != null && dm.IsDragging && _item.IsBeingDragged();
            Log("after: kinematic=" + Kinematic(_item) + " dragging=" + dragging + " moved=" + Vector3.Distance(_before, now).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " pos=" + V(now));
        }

        private static string Kinematic(TrashItem item)
        {
            Rigidbody rb = item != null ? GameTrash.BodyOf(item) : null;
            return rb == null ? "none" : rb.isKinematic.ToString();
        }

        private static Transform CameraTransform()
        {
            try { PlayerCamera pc = PlayerSingleton<PlayerCamera>.Instance; return pc != null ? pc.transform : null; }
            catch (Exception e) { Log("camera: " + e.Message); return null; }
        }

        internal static string TypeId()
        {
            for (int i = 0; i < InstancedTrash.Count; i++)
            {
                string id = InstancedTrash.GetTypeId(i);
                if (!string.IsNullOrEmpty(id)) return id;
            }
            return null;
        }

        internal static string V(Vector3 v) => "(" + v.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ", "
            + v.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ", " + v.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ")";

        private static void Log(string msg) => Core.Log?.Msg("[dragtest] " + msg);
    }

    /// <summary>
    /// DEBUG-only cleaner rig, ported from the Actually Working Employees rig. 'tv cleanerrig setup [code]' picks an
    /// owned property with a free employee home and a disposal area, places two trash cans when it has fewer than two,
    /// and hires a cleaner when it has none. Once the cleaner exists it gets the home and the cans and is paid for
    /// today. The rig then puts trash into Litterally's instanced field (data, not real items) around every can, so
    /// the CleanerActor has to materialize it before the cleaner can see it. A status line every 15 s reports how many
    /// materialized items the cleaner took and how full the cans are.
    /// </summary>
    internal static class CleanerRig
    {
        private const int RegionW = 18, RegionH = 4, PerCanBatch = 8, MaxAdded = 160;
        private static readonly int[][] CanTiles = { new[] { 1, 1 }, new[] { 15, 1 } };
        private const float FillSeconds = 20f, AssignSeconds = 2f, StatusSeconds = 15f, Spread = 1.8f;

        private static readonly List<TrashContainerItem> _cans = new List<TrashContainerItem>();
        private static readonly List<int> _startLevels = new List<int>();
        private static PropertyT _property;
        private static BuildableItem _home;
        private static Cleaner _cleaner;
        private static bool _active, _assigned;
        private static float _nextAssign, _nextFill, _nextStatus;
        private static int _added, _collectedAtStart;
        private static readonly System.Random _rng = new System.Random(7);

        internal static void Command(string[] p)
        {
            string mode = p.Length > 2 ? p[2].ToLowerInvariant() : "status";
            if (mode == "setup") Setup(p.Length > 3 ? p[3] : null);
            else if (mode == "stop") { _active = false; Log("stopped; " + Status()); }
            else Log(Status());
        }

        private static void Setup(string code)
        {
            if (_active) { Log("already set up; " + Status()); return; }
            var owned = PropertyT.OwnedProperties;
            if (owned == null || owned.Count == 0) { Log("no owned property."); return; }
            PropertyT best = null;
            BuildableItem bestHome = null;
            int bestCans = -1;
            for (int i = 0; i < owned.Count; i++)
            {
                PropertyT p = owned[i];
                if (p == null || (code != null && !string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase))) continue;
                if (p.DisposalArea == null) { Log(p.PropertyCode + ": no disposal area."); continue; }
                BuildableItem home = FreeHome(p);
                if (home == null) { Log(p.PropertyCode + ": no free employee home."); continue; }
                int cans = CansOf(p, null);
                if (cans > bestCans) { best = p; bestHome = home; bestCans = cans; }
            }
            if (best == null) { Log("no owned property" + (code == null ? "" : " '" + code + "'") + " has a free home and a disposal area."); return; }
            _property = best;
            _home = bestHome;
            _cans.Clear();
            CansOf(best, _cans);
            if (_cans.Count < 2) PlaceCans(best);
            if (_cans.Count == 0) { Log(best.PropertyCode + ": no trash can to assign."); _property = null; return; }

            _cleaner = FindCleaner();
            bool hired = false;
            if (_cleaner == null)
            {
                NetworkSingleton<EmployeeManager>.Instance.CreateNewEmployee(best, EEmployeeType.Cleaner);
                hired = true;
            }
            _active = true;
            _assigned = false;
            _added = 0;
            _nextAssign = _nextFill = _nextStatus = 0f;
            Log("set up on " + best.PropertyCode + ": " + _cans.Count + " can(s), home " + _home.name + (hired ? ", hired a cleaner." : ", existing cleaner."));
            for (int i = 0; i < _cans.Count; i++) Log("can " + i + " at " + DragTest.V(_cans[i].transform.position));
        }

        internal static void Tick()
        {
            if (!_active) return;
            float now = Time.time;
            if (!_assigned && now >= _nextAssign)
            {
                _nextAssign = now + AssignSeconds;
                TryAssign();
            }
            if (!_assigned) return;
            if (now >= _nextFill)
            {
                _nextFill = now + FillSeconds;
                Fill();
            }
            if (now >= _nextStatus)
            {
                _nextStatus = now + StatusSeconds;
                Log("status " + Status());
            }
        }

        private static void TryAssign()
        {
            if (_cleaner == null) _cleaner = FindCleaner();
            if (_cleaner == null) return;   // a hired cleaner needs a few seconds to spawn
            var cfg = _cleaner.configuration;
            if (cfg == null) { Log(_cleaner.name + " has no configuration yet."); return; }
            if (cfg.assignedHome == null) cfg.Home.SetObject(_home, true);
            for (int i = 0; i < _cans.Count && cfg.binItems.Count < Cleaner.MAX_ASSIGNED_BINS; i++)
                if (_cans[i] != null && !cfg.binItems.Contains(_cans[i])) cfg.Bins.AddItem(_cans[i]);
            _cleaner.SetIsPaid();
            _assigned = cfg.binItems.Count > 0 && cfg.assignedHome != null;
            Log(_cleaner.name + ": home=" + (cfg.assignedHome != null) + " bins=" + cfg.binItems.Count + " paid=" + _cleaner.PaidForToday
                + " at " + DragTest.V(_cleaner.transform.position) + (_assigned ? ", rig running." : ", retrying."));
            if (!_assigned) return;
            _startLevels.Clear();
            for (int i = 0; i < _cans.Count; i++) _startLevels.Add(Level(_cans[i]));
            _collectedAtStart = CleanerActor.Collected;
        }

        // Puts trash into the instanced field (data), never as real items: the CleanerActor must materialize it.
        private static void Fill()
        {
            if (_cleaner != null && !_cleaner.PaidForToday) _cleaner.SetIsPaid();
            if (_added >= MaxAdded) return;
            string fallback = DragTest.TypeId();
            TrashManager tm = GameTrash.TrashManagerOrNull();
            int added = 0;
            for (int c = 0; c < _cans.Count; c++)
            {
                TrashContainerItem can = _cans[c];
                if (can == null) continue;
                for (int n = 0; n < PerCanBatch && _added < MaxAdded; n++)
                {
                    double a = _rng.NextDouble() * Math.PI * 2, r = 0.8 + _rng.NextDouble() * (Spread - 0.8);
                    Vector3 p = can.transform.position + new Vector3((float)(Math.Cos(a) * r), 0.5f, (float)(Math.Sin(a) * r));
                    if (UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) p = hit.position + Vector3.up * 0.3f;
                    string id = null;
                    try { TrashItem prefab = tm != null ? tm.GetRandomGeneratableTrashPrefab() : null; id = prefab != null ? prefab.ID : null; }
                    catch (Exception e) { Log("prefab: " + e.Message); }
                    int t = id != null ? InstancedTrash.AddOne(id, p, Quaternion.Euler(0f, (float)(a * 57.3), 0f), groundSnap: false) : -1;
                    if (t < 0 && fallback != null) t = InstancedTrash.AddOne(fallback, p, Quaternion.identity, groundSnap: false);
                    if (t < 0) { Log("AddOne refused " + (id ?? fallback ?? "-") + " (" + t + ")."); return; }
                    _added++;
                    added++;
                }
            }
            Log("filled " + added + " data item(s) around the cans, " + _added + " in total.");
        }

        private static string Status()
        {
            string levels = "";
            for (int i = 0; i < _cans.Count; i++)
                levels += (i > 0 ? "," : "") + (i < _startLevels.Count ? _startLevels[i] : -1) + "->" + Level(_cans[i]);
            int grabber = -1;
            try { if (_cleaner != null) grabber = _cleaner.GetTrashGrabberAmount(); } catch (Exception e) { Log("grabber: " + e.Message); }
            return "active=" + _active + " property=" + (_property == null ? "-" : _property.PropertyCode) + " cleaner=" + (_cleaner == null ? "-" : _cleaner.name)
                + " assigned=" + _assigned + " added=" + _added + " collected=" + (CleanerActor.Collected - _collectedAtStart)
                + " real-cleaner=" + CleanerActor.RealCount + " grabber=" + grabber + " binLevels=" + levels
                + " cleanerPos=" + (_cleaner == null ? "-" : DragTest.V(_cleaner.transform.position))
                + " playerDist=" + PlayerDistance().ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Horizontal distance from the player to the first can; the Virtualizer takes over trash within ~32 m.
        private static float PlayerDistance()
        {
            if (_cans.Count == 0 || _cans[0] == null || !GameTrash.TryGetPlayerPosition(out Vector3 pp)) return -1f;
            Vector3 d = _cans[0].transform.position - pp;
            d.y = 0f;
            return d.magnitude;
        }

        private static int Level(TrashContainerItem can)
        {
            try { return can != null && can.Container != null ? can.Container.TrashLevel : -1; }
            catch (Exception e) { Log("level: " + e.Message); return -1; }
        }

        private static BuildableItem FreeHome(PropertyT p)
        {
            var items = p.BuildableItems;
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                BuildableItem b = items[i];
                if (b == null) continue;
                EmployeeHome h = b.GetComponent<EmployeeHome>();
                if (h != null && h.AssignedEmployee == null) return b;
            }
            return null;
        }

        private static int CansOf(PropertyT p, List<TrashContainerItem> into)
        {
            var items = p.BuildableItems;
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                TrashContainerItem c = items[i] == null ? null : items[i].TryCast<TrashContainerItem>();
                if (c == null || !c.UsableByCleaners) continue;
                n++;
                into?.Add(c);
            }
            return n;
        }

        private static void PlaceCans(PropertyT p)
        {
            var def = Il2CppScheduleOne.Registry.GetItem("trashcan");
            var build = NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance;
            if (def == null || build == null) { Log("item 'trashcan' or the BuildManager is missing; no can placed."); return; }
            var grids = p.Grids;
            if (grids == null) { Log(p.PropertyCode + " has no grids; no can placed."); return; }
            for (int g = 0; g < grids.Count; g++)
            {
                Il2CppScheduleOne.Tiles.Grid grid = grids[g];
                if (grid == null || !FreeRegion(grid, out int ox, out int oy)) continue;
                int placed = 0;
                foreach (int[] t in CanTiles)
                {
                    var item = build.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(ox + t[0], oy + t[1]), 0, "", null);
                    TrashContainerItem can = item == null ? null : item.TryCast<TrashContainerItem>();
                    if (can == null) { Log("trash can at " + (ox + t[0]) + "," + (oy + t[1]) + " was not created."); continue; }
                    _cans.Add(can);
                    placed++;
                }
                Log("placed " + placed + " trash can(s) on " + p.PropertyCode + " at tile " + ox + "," + oy + ".");
                return;
            }
            Log(p.PropertyCode + ": no free patch of " + RegionW + " x " + RegionH + " tiles for the cans.");
        }

        private static bool FreeRegion(Il2CppScheduleOne.Tiles.Grid grid, out int ox, out int oy)
        {
            ox = oy = 0;
            var tiles = grid.Tiles;
            if (tiles == null || tiles.Count == 0) return false;
            int maxX = 0, maxY = 0;
            for (int i = 0; i < tiles.Count; i++) { if (tiles[i] == null) continue; maxX = Math.Max(maxX, tiles[i].x); maxY = Math.Max(maxY, tiles[i].y); }
            var free = new bool[maxX + 1, maxY + 1];
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (t == null || t.x < 0 || t.y < 0) continue;
                free[t.x, t.y] = (t.BuildableOccupants == null || t.BuildableOccupants.Count == 0) && t.CanBeBuiltOn();
            }
            for (int x = 0; x + RegionW <= maxX + 1; x++)
                for (int y = 0; y + RegionH <= maxY + 1; y++)
                {
                    bool ok = true;
                    for (int i = 0; i < RegionW && ok; i++)
                        for (int j = 0; j < RegionH && ok; j++)
                            ok = free[x + i, y + j];
                    if (ok) { ox = x; oy = y; return true; }
                }
            return false;
        }

        private static Cleaner FindCleaner()
        {
            try
            {
                var emps = NetworkSingleton<EmployeeManager>.Instance.GetEmployeesByType(EEmployeeType.Cleaner);
                for (int i = 0; emps != null && i < emps.Count; i++)
                {
                    Cleaner c = emps[i] == null ? null : emps[i].TryCast<Cleaner>();
                    if (c != null && c.AssignedProperty != null && _property != null && c.AssignedProperty.PropertyCode == _property.PropertyCode) return c;
                }
            }
            catch (Exception e) { Log("find cleaner: " + e.Message); }
            return null;
        }

        private static void Log(string msg) => Core.Log?.Msg("[cleanerrig] " + msg);
    }
}
#endif
