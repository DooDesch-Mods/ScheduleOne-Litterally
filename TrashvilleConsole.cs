using System;
using System.Globalization;
using HarmonyLib;
using Trashville.Config;
using Trashville.Profiling;
using Trashville.Spawning;

namespace Trashville
{
    /// <summary>
    /// Drives the whole mod through the game dev console (so the Schedule1 MCP / run_console_command can
    /// test it headlessly). All commands are namespaced "tv ...". Two Harmony prefixes catch both
    /// Console.SubmitCommand overloads and swallow our commands so the game does not report them as unknown.
    /// </summary>
    internal static class TrashvilleConsole
    {
        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }
            string[] p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return Dispatch(p);
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                return false;
            }
            string[] p = new string[args.Count];
            for (int i = 0; i < args.Count; i++)
            {
                p[i] = args[i];
            }
            return Dispatch(p);
        }

        private static int _lastFrame = -1;
        private static string _lastSig = "";

        private static bool Dispatch(string[] p)
        {
            if (p.Length == 0 || !p[0].Equals("tv", StringComparison.OrdinalIgnoreCase))
            {
                return false;   // not ours - let the game handle it
            }

            // Both SubmitCommand overloads fire (string body calls the List body), so dedupe per frame
            // or a single "tv spawn 1000" would run twice.
            string sig = string.Join(" ", p);
            int frame = UnityEngine.Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig)
            {
                return true;   // already handled this exact command this frame - swallow the duplicate
            }
            _lastFrame = frame;
            _lastSig = sig;

            string cmd = p.Length > 1 ? p[1].ToLowerInvariant() : "status";
            try
            {
                switch (cmd)
                {
                    case "arm": Preferences.SetArm(Bool(p, 2, !Preferences.ArmBenchmark)); Log($"arm = {Preferences.ArmBenchmark}"); break;
                    case "bypass": Preferences.SetBypassCap(Bool(p, 2, !Preferences.BypassCap)); Log($"bypass = {Preferences.BypassCap}"); break;
                    case "opt": Preferences.SetOptimizeClones(Bool(p, 2, !Preferences.OptimizeClones)); CloneSpawner.Reset(); Log($"optimizeClones = {Preferences.OptimizeClones} (templates reset)"); break;
                    case "budget": if (p.Length > 2 && int.TryParse(p[2], out int bv)) Preferences.SetMaxAwakeBudget(bv); Log($"maxAwakeBudget = {Preferences.MaxAwakeBudget}"); break;
                    case "kin": Preferences.SetSpawnKinematic(Bool(p, 2, !Preferences.SpawnKinematic)); Log($"kinematic = {Preferences.SpawnKinematic}"); break;
                    case "spawn": Spawn(p); break;
                    case "clear": SaveGuard.SaveSafety.ScopedClear(); Log("scoped clear"); break;
                    case "purge": SaveGuard.SaveSafety.PurgeAll(); Log("purged all"); break;
                    case "physab": PhysicsProbe.Start(); Log("physics A/B started"); break;
                    case "sweep": AblationController.StartSweep(); Log("sweep started"); break;
                    case "dump": PhysicsConfigDump.Dump(); break;
                    case "diag": CloneRegistry.DumpDiag(); break;
                    case "inst": InstSpawn(p); break;
                    case "instclear": Instanced.Virtualizer.ClearAll(); Instanced.InstancedTrash.Clear(); Log("instanced cleared"); break;
                    case "shadows": Instanced.InstancedTrash.Shadows = Bool(p, 2, !Instanced.InstancedTrash.Shadows); Log($"instanced shadows = {Instanced.InstancedTrash.Shadows}"); break;
                    case "meshdiag": Instanced.InstancedTrash.DumpMeshParts(); Log("mesh parts dumped to log"); break;
                    case "drift": { int dc = (p.Length > 2 && int.TryParse(p[2], out int dn)) ? dn : 20; Instanced.InstancedTrash.BeginDrift(dc); Log($"ground-drift self-test: activated up to {dc} dynamic probes; result in log after ~5s"); break; }
                    case "route": Spawning.RouteHook.Command(p); Log("route probe: see log ([route-probe])"); break;
                    case "gridtest": Instanced.InstancedTrash.GridSelfTest(); Log("spatial-grid self-test: see log ([grid])"); break;
                    case "cleaner":
                        if (p.Length > 2 && p[2] == "diag") { Spawning.CleanerActor.Diagnose(); Log("cleaner diag: see log ([cleaner])"); break; }
                        if (p.Length > 3 && p[2] == "range" && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float crg)) { Spawning.CleanerActor.Range = Mathf.Clamp(crg, 2f, 120f); Log($"cleaner range = {Spawning.CleanerActor.Range}m"); break; }
                        if (p.Length > 2) Spawning.CleanerActor.Enabled = Bool(p, 2, !Spawning.CleanerActor.Enabled);
                        Log($"cleaner-actor enabled={Spawning.CleanerActor.Enabled}, materialized-near-cleaners={Spawning.CleanerActor.RealCount}");
                        break;
                    case "onlytype": Instanced.InstancedTrash.OnlyType = (p.Length > 2 && p[2] != "off") ? p[2] : null; Instanced.Virtualizer.ClearAll(); Instanced.InstancedTrash.Clear(); Instanced.InstancedTrash.ResetPalette(); Log($"onlyType = {(Instanced.InstancedTrash.OnlyType ?? "off")} (respawn to apply)"); break;
                    case "maxtypes": if (p.Length > 2 && int.TryParse(p[2], out int mt)) { Instanced.InstancedTrash.MaxTypes = Mathf.Clamp(mt, 1, 8); Instanced.Virtualizer.ClearAll(); Instanced.InstancedTrash.Clear(); Instanced.InstancedTrash.ResetPalette(); } Log($"maxTypes = {Instanced.InstancedTrash.MaxTypes} (field cleared + palette reset - respawn to apply)"); break;
                    case "real": Instanced.Virtualizer.Enabled = Bool(p, 2, !Instanced.Virtualizer.Enabled); Log($"virtualizer (materialize near player) = {Instanced.Virtualizer.Enabled}"); break;
                    case "realview": case "realradius": if (p.Length > 2 && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float rr)) Instanced.Virtualizer.ViewDist = Mathf.Clamp(rr, 4f, 60f); Log($"realView (in-frustum materialize distance) = {Instanced.Virtualizer.ViewDist}"); break;
                    case "realback": if (p.Length > 2 && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float rb2)) Instanced.Virtualizer.BackRadius = Mathf.Clamp(rb2, 1f, 15f); Log($"realBack (anti-glitch radius) = {Instanced.Virtualizer.BackRadius}"); break;
                    case "realmax": if (p.Length > 2 && int.TryParse(p[2], out int rm)) Instanced.Virtualizer.MaxReal = Mathf.Clamp(rm, 50, 2000); Log($"realMax (max real items - perf/range dial) = {Instanced.Virtualizer.MaxReal}"); break;
                    case "predict": Instanced.Virtualizer.Predict = Bool(p, 2, !Instanced.Virtualizer.Predict); Log($"predict (anticipate turn/movement) = {Instanced.Virtualizer.Predict}"); break;
                    case "collide": Instanced.Virtualizer.Collide = Bool(p, 2, !Instanced.Virtualizer.Collide); Log($"collide = {Instanced.Virtualizer.Collide} (ON=grounded+player collision, dynamic; OFF=performance: kinematic, floats, no collision)"); break;
                    case "perframe": if (p.Length > 2 && int.TryParse(p[2], out int pf)) Instanced.Virtualizer.NewPerFrame = Mathf.Clamp(pf, 5, 600); Log($"newPerFrame (materializations/frame) = {Instanced.Virtualizer.NewPerFrame}"); break;
                    case "fov": SetFov(p); break;
                    case "up": MovePlayerUp(p); break;
                    case "overview": ApplyFov(90f); MovePlayer(0f, 40f); Log("overview: player +40m, fov 90"); break;
                    case "status": Status(); break;
                    default: Log($"unknown subcommand '{cmd}'. Use: arm|bypass|kin|spawn N|clear|purge|physab|sweep|dump|status"); break;
                }
            }
            catch (Exception e)
            {
                Log("error: " + e.Message);
            }
            return true;   // handled (or attempted) - swallow it
        }

        private static void Spawn(string[] p)
        {
            int n = 0;
            if (p.Length > 2)
            {
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            }
            if (n <= 0)
            {
                Log("usage: tv spawn <count>");
                return;
            }
            TrashSpawner.RequestSpawn(n);
        }

        private static void InstSpawn(string[] p)
        {
            int n = 1000;
            if (p.Length > 2) int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            if (!TrashSpawner.TryGetPlayerPosition(out Vector3 c))
            {
                Log("no player position");
                return;
            }
            Instanced.InstancedTrash.Setup(n, c);
        }

        private static void SetFov(string[] p)
        {
            if (p.Length <= 2 || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                Log("usage: tv fov <degrees>");
                return;
            }
            int n = ApplyFov(f);
            Log($"fov {f} applied to {n} camera(s)");
        }

        private static int ApplyFov(float f)
        {
            int n = 0;
            try
            {
                UnityEngine.Camera cam = UnityEngine.Camera.main;
                if (cam != null) { cam.fieldOfView = f; n++; }
            }
            catch { }
            if (n == 0)
            {
                try
                {
                    var all = UnityEngine.Camera.allCameras;
                    if (all != null)
                    {
                        for (int i = 0; i < all.Length; i++)
                        {
                            if (all[i] != null) { all[i].fieldOfView = f; n++; }
                        }
                    }
                }
                catch { }
            }
            return n;
        }

        private static void MovePlayerUp(string[] p)
        {
            float m = 30f;
            if (p.Length > 2) float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out m);
            MovePlayer(0f, m);
            Log($"moved player up {m}m");
        }

        private static void MovePlayer(float forward, float up)
        {
            try
            {
                Player pl = Player.Local;
                if (pl == null) { Log("no player"); return; }
                Transform t = pl.transform;
                Vector3 fwd = t.forward; fwd.y = 0f; if (fwd.sqrMagnitude > 0.01f) fwd.Normalize(); else fwd = Vector3.forward;
                t.position = t.position + fwd * forward + new Vector3(0f, up, 0f);
            }
            catch (Exception e) { Log("move err: " + e.Message); }
        }

        private static void Status()
        {
            FrameStats s = PerfSampler.Snapshot();
            var tm = TrashSpawner.TrashManagerOrNull();
            int mgr = tm != null ? TrashSpawner.TrashItemCount(tm) : -1;
            Log($"armed={Preferences.ArmBenchmark} bypass={Preferences.BypassCap} opt={Preferences.OptimizeClones} mode={(Preferences.SpawnKinematic ? "kinematic" : "dynamic")} | " +
                $"clones={CloneRegistry.Count} awake={CloneRegistry.CountAwake()} instanced={Instanced.InstancedTrash.Count} types={Instanced.InstancedTrash.TypeCount} real={Instanced.Virtualizer.RealCount} view={Instanced.Virtualizer.ViewDist:F0} back={Instanced.Virtualizer.BackRadius:F0} virt={Instanced.Virtualizer.Enabled} budget={Preferences.MaxAwakeBudget} gameTrash={TrashRegistry.Count} mgr={mgr} pending={TrashSpawner.Pending} | " +
                $"fps mean={s.MeanFps:F1} min={s.MinFps:F1} frameMs mean={s.MeanMs:F1} p95={s.P95Ms:F1} | " +
                $"sweep={AblationController.Active} physAB={PhysicsProbe.Active}");
        }

        private static bool Bool(string[] p, int idx, bool toggleDefault)
        {
            if (p.Length <= idx)
            {
                return toggleDefault;   // no arg => toggle
            }
            string v = p[idx].ToLowerInvariant();
            if (v == "on" || v == "true" || v == "1" || v == "yes") return true;
            if (v == "off" || v == "false" || v == "0" || v == "no") return false;
            return toggleDefault;
        }

        private static void Log(string msg) => Core.Log?.Msg("[tv] " + msg);
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(string) })]
    internal static class Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !TrashvilleConsole.TryHandle(args); } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !TrashvilleConsole.TryHandle(args); } catch { return true; }
        }
    }
}
