#if SNITCH
using Snitch.Api;                 // Profiler, Panel, StateSnapshot
using Litterally.Config;          // Preferences
using Litterally.Instanced;       // InstancedTrash, Virtualizer
using Litterally.Profiling;       // AblationController, PhysicsProbe, PhysicsConfigDump
using Litterally.SaveGuard;       // SaveSafety
using Litterally.Spawning;        // RouteHook, CleanerActor, GameTrash, TrashSpawner, CloneRegistry, TrashRegistry

namespace Litterally.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for Litterally. Builds a Snitch profiler panel that exposes the trash
    /// performance layer's key counts/state AND the full dev control surface (spawn / clear / sweep / physics +
    /// the arm/bypass/spawn-mode toggles) - the same operations the 'tv' dev console and the old dev hotkeys drove.
    /// All actions/toggles call straight into the existing benchmark methods (no logic is reimplemented here) and run
    /// on the main thread. No-op when the Snitch host is absent. Compiled only when SNITCH is defined (Debug +
    /// EnableSnitch); excluded from Release. See Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        // The panel id == the prefix the existing counters already use, so counters/state group under the panel.
        private const string Id = "Litterally";

        public static void Register()
        {
            Panel p = Profiler.RegisterPanel(Id, "Litterally");

            // ----- live counters (grouped under the panel as "Litterally.<name>") -----
            p.Counter("FieldLive", () => InstancedTrash.LiveCount, "items");
            p.Counter("RealItems", () => Virtualizer.RealCount, "items");
            p.Counter("Absorbed", () => RouteHook.Absorbed, "items");
            // Diagnostics that pinpoint the real cost: awake materialized rigidbodies (the active-physics churn),
            // instances actually drawn after frustum/distance cull, items materialized around cleaner NPCs, and
            // the count mid-settle (the burst-routing physics driver).
            p.Counter("AwakeReal", () => Virtualizer.AwakeRealCount(), "rb");
            p.Counter("Drawn", () => InstancedTrash.Drawn, "items");
            p.Counter("RealCleaner", () => CleanerActor.RealCount, "items");
            p.Counter("Settling", () => RouteHook.SettlingCount, "items");

            // ----- state distribution -----
            p.State(() =>
            {
                var tm = GameTrash.TrashManagerOrNull();
                int mgr = tm != null ? GameTrash.TrashItemCount(tm) : 0;
                return new StateSnapshot { Title = "Trash field" }
                    .Add("field-live", InstancedTrash.LiveCount)
                    .Add("real-player", Virtualizer.RealCount)
                    .Add("real-cleaner", CleanerActor.RealCount)
                    .Add("settling", RouteHook.SettlingCount)
                    .Add("manager", mgr);
            });

            // ----- flags/summary readout (what the old on-screen dev HUD showed) -----
            p.Text(() =>
            {
                var tm = GameTrash.TrashManagerOrNull();
                int mgr = tm != null ? GameTrash.TrashItemCount(tm) : -1;
                string mode = Preferences.SpawnKinematic ? "kinematic" : "dynamic";
                string s =
                    $"armed:{(Preferences.ArmBenchmark ? "YES" : "no")}  mode:{mode}  bypass:{(Preferences.BypassCap ? "ON" : "off")}\n" +
                    $"ours:{TrashRegistry.Count}  clones:{CloneRegistry.Count} (awake {CloneRegistry.CountAwake()})  manager:{mgr}  pending:{TrashSpawner.Pending}\n" +
                    $"instanced:{InstancedTrash.Count} (live {InstancedTrash.LiveCount})  drawn:{InstancedTrash.Drawn}  real:{Virtualizer.RealCount}";
                if (AblationController.Active) s += $"\nsweep: {AblationController.Status}";
                if (PhysicsProbe.Active) s += $"\nphysAB: {PhysicsProbe.Status}";
                return s;
            });

            // ----- actions (clickable buttons; same calls the old hotkeys / 'tv' subcommands made) -----
            p.Action("Spawn 100", () => { TrashSpawner.RequestSpawn(100); Profiler.Log(Id, "spawn 100 requested"); });   // was F7 / 'tv spawn 100'
            p.Action("Spawn 1k", () => { TrashSpawner.RequestSpawn(1000); Profiler.Log(Id, "spawn 1k requested"); });    // was F8 / 'tv spawn 1000'
            p.Action("Spawn 10k", () => { TrashSpawner.RequestSpawn(10000); Profiler.Log(Id, "spawn 10k requested"); }); // was F9 / 'tv spawn 10000'
            p.Action("Clear", () => { SaveSafety.ScopedClear(); Profiler.Log(Id, "scoped clear"); });                    // was F10 / 'tv clear'
            p.Action("Purge", () => { SaveSafety.PurgeAll(); Profiler.Log(Id, "purged all"); });                        // was Shift+F10 / 'tv purge'
            p.Action("Physics A/B", () => { PhysicsProbe.Start(); Profiler.Log(Id, "physics A/B started"); });          // was F3 / 'tv physab'
            p.Action("Physics dump", () => { PhysicsConfigDump.Dump(); Profiler.Log(Id, "physics config dumped to log"); }); // was F2 / 'tv dump'
            p.Action("Ablation sweep", () => { AblationController.StartSweep(); Profiler.Log(Id, "ablation sweep started"); }); // was F11 / 'tv sweep'
            p.Action("Abort measure", () =>                                                                              // was F12
            {
                AblationController.Abort("aborted from Snitch panel");
                PhysicsProbe.Abort("aborted from Snitch panel");
                Profiler.Log(Id, "measurement aborted");
            });

            // ----- toggles (on/off controls; same prefs the old toggle hotkeys flipped) -----
            p.Toggle("Bypass cap", () => Preferences.BypassCap, v => Preferences.SetBypassCap(v));   // was F1 / 'tv bypass'
            // "Spawn mode (dynamic)": ON = dynamic (falling physics), OFF = kinematic (frozen). The pref stores the
            // inverse (SpawnKinematic), so invert here.
            p.Toggle("Spawn mode (dynamic)", () => !Preferences.SpawnKinematic, v => Preferences.SetSpawnKinematic(!v)); // was F4 / 'tv kin'
            p.Toggle("Arm benchmark", () => Preferences.ArmBenchmark, v => Preferences.SetArm(v));   // was F5 / 'tv arm'

            // Show this panel's own log channel inside the panel.
            p.Log();

            // ----- ablation levers ('snitch ablate <name>'): each toggles ONE subsystem so the profiler can measure
            // its CAUSAL frame-time cost, including native work the self-measured section timers cannot see. All are
            // process-local (the instanced layer is local-only) and reversible, so they are safe in any session.
            // 'litterally.render' is the headline lever - it isolates the native Graphics.DrawMeshInstanced draw +
            // the CPU frustum/distance cull, which no C# section timer can attribute.
            Profiler.RegisterAblationLever("litterally.render",
                apply: () => InstancedTrash.RenderEnabled = false,
                restore: () => InstancedTrash.RenderEnabled = true);
            // 'litterally.sim' should read ~0 on a settled field (instanced/sleeping trash is nearly free).
            Profiler.RegisterAblationLever("litterally.sim",
                apply: () => InstancedTrash.SimEnabled = false,
                restore: () => InstancedTrash.SimEnabled = true);
            // virtualize/cleaner: stop materializing AND demote the current reals so the delta is clean (this is the
            // active-physics + real-TrashItem cost near the player / cleaners). Restore re-converges over a few frames.
            Profiler.RegisterAblationLever("litterally.virtualize",
                apply: () => { Virtualizer.Enabled = false; Virtualizer.ClearAll(); },
                restore: () => Virtualizer.Enabled = true);
            Profiler.RegisterAblationLever("litterally.cleaner",
                apply: () => { CleanerActor.Enabled = false; CleanerActor.ClearAll(); },
                restore: () => CleanerActor.Enabled = true);
            // 'litterally.route' stops absorbing/settling new game-trash (run it on an already-populated field).
            Profiler.RegisterAblationLever("litterally.route",
                apply: () => RouteHook.Active = false,
                restore: () => RouteHook.Active = true);
        }
    }
}
#endif
