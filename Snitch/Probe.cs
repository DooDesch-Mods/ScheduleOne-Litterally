#if SNITCH
using Snitch.Api;                 // Profiler, StateSnapshot
using Litterally.Instanced;       // InstancedTrash, Virtualizer
using Litterally.Spawning;        // RouteHook, CleanerActor, GameTrash

namespace Litterally.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for Litterally. Exposes the trash performance layer's key counts/state
    /// to the Snitch profiler (no-op when the Snitch host is absent). Compiled only when SNITCH is defined
    /// (Debug + EnableSnitch); excluded from Release. See Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Profiler.RegisterCounter("Litterally.FieldLive", () => InstancedTrash.LiveCount, "items");
            Profiler.RegisterCounter("Litterally.RealItems", () => Virtualizer.RealCount, "items");
            Profiler.RegisterCounter("Litterally.Absorbed", () => RouteHook.Absorbed, "items");
            // Diagnostics that pinpoint the real cost: awake materialized rigidbodies (the active-physics churn),
            // instances actually drawn after frustum/distance cull, items materialized around cleaner NPCs, and
            // the count mid-settle (the burst-routing physics driver).
            Profiler.RegisterCounter("Litterally.AwakeReal", () => Virtualizer.AwakeRealCount(), "rb");
            Profiler.RegisterCounter("Litterally.Drawn", () => InstancedTrash.Drawn, "items");
            Profiler.RegisterCounter("Litterally.RealCleaner", () => CleanerActor.RealCount, "items");
            Profiler.RegisterCounter("Litterally.Settling", () => RouteHook.SettlingCount, "items");

            Profiler.RegisterStateProvider("Litterally", () =>
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
