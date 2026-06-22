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
        }
    }
}
#endif
