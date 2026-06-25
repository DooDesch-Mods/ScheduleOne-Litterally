using System;
using MelonLoader;
using S1API.Lifecycle;
using Litterally.Compat;
using Litterally.Config;
#if DEBUG
using Litterally.Profiling;
#endif
using Litterally.SaveGuard;
using Litterally.Spawning;
#if SNITCH
using Snitch.Api;                 // Profiler section timing (Debug + EnableSnitch only; no-op when host absent)
#endif

[assembly: MelonInfo(typeof(Litterally.Core), "Litterally", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Litterally")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Litterally
{
    /// <summary>
    /// MelonLoader entry point for the Litterally trash-performance mod. The release build routes the game's own
    /// generated trash into a cheap instanced field (rendered cheaply, materialized into real pickup-able items
    /// near the player + cleaners) so the world can hold far more trash with little cost. DEBUG builds additionally
    /// wire the benchmark/ablation harness (spawn pump, perf sampler, dev console + the Snitch profiler panel).
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;
        private bool _perfApplied;
        private int _perfLogState = -1;   // last logged perf-layer state (-1 unknown, 0 off-pref, 1 off-MP, 2 active) - dedups the re-arm logs
        private float _teleElapsed; private int _teleFrames; private float _teleMaxDt;   // periodic-telemetry window accumulators
#if DEBUG
        private int _frame;
#endif

        [System.Diagnostics.Conditional("DEBUG")]
        public static void LogDebug(string msg) { Log?.Msg(msg); }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();

            // Console bridge: lets the Schedule1 MCP drive the mod via "tv ..." dev-console commands.
            try { HarmonyInstance.PatchAll(); } catch (Exception e) { Log.Warning("[Core] Harmony patch failed: " + e.Message); }

            // Save-safety: the materialised real items must never be serialized. Clear before any save / scene
            // change. Routed game-trash persists as a compact mod blob: write it BEFORE the working-set is cleared
            // (the blob sees the SoA data; ForceClearForSave only destroys the real items), restore on load.
            // A save also un-boosts the generators (ForceClearForSave -> GeneratorBoost.Restore); re-arm the perf
            // layer so the next in-world frame re-asserts routing + the TrashMultiplier boost + Virtualizer dials.
            GameLifecycle.OnSaveStart += () => { Instanced.SaveBlob.Save(); SaveSafety.ForceClearForSave("save starting"); _perfApplied = false; };
            GameLifecycle.OnPreSceneChange += () => SaveSafety.ForceClearForSave("scene changing");
            GameLifecycle.OnLoadComplete += OnGameLoaded;
            // Only restore the routed field when the performance layer is enabled - otherwise stay fully vanilla
            // (an empty field renders nothing; the blob is left untouched on disk for when it's re-enabled).
            GameLifecycle.OnLoadComplete += () => { if (Preferences.EnablePerformanceLayer) Instanced.SaveBlob.Load(); };

            HookModManager();

#if DEBUG
            Log.Msg("Litterally v1.0.0 (DEBUG) - trash performance layer active. Dev controls: the Snitch profiler panel (spawn/clear/sweep/toggles) or the 'tv' dev-console commands.");
            Log.Warning("Litterally DEBUG: the benchmark spawns thousands of TEMPORARY trash - use a throwaway save. It auto-clears on save/scene-change/quit.");
#else
            Log.Msg("Litterally v1.0.0 - trash performance layer active.");
#endif
        }

        private void OnGameLoaded()
        {
            // A fresh world load: re-apply the performance layer on the first in-world frame.
            _perfApplied = false;
#if DEBUG
            try
            {
                PerfSampler.StartRecorders();
                PerfSampler.LogCapabilityReport();
                PerfSampler.ResetGcWindow();
            }
            catch (Exception e)
            {
                Log.Warning("[Core] OnGameLoaded init failed: " + e.Message);
            }
#endif
        }

        // ----- release performance-layer activation -----

        /// <summary>
        /// Turns the performance layer on the first in-world frame, honouring the user's preferences. AUTO-ON by
        /// default (routing absorbs the game's generated trash automatically). Auto-DISABLED in multiplayer because
        /// the instanced field is local-only and would desync; EnableInMultiplayer forces it on for testers.
        /// </summary>
        private void ApplyPerformanceLayer()
        {
            _perfApplied = true;
            try
            {
                if (!Preferences.EnablePerformanceLayer)
                {
                    DisableLayer();
                    if (_perfLogState != 0) { Log.Msg("[Core] Performance layer DISABLED via preferences - running vanilla trash."); _perfLogState = 0; }
                    return;
                }

                // TODO: real multiplayer support. The instanced field is local-only (each client routes its own
                // game-trash, materialises locally), so two clients would see divergent trash and desync. Until a
                // host-authoritative sync exists, auto-disable in MP unless a tester force-enables it.
                if (Net.IsMultiplayer() && !Preferences.EnableInMultiplayer)
                {
                    DisableLayer();
                    if (_perfLogState != 1) { Log.Msg("[Core] Multiplayer session detected - performance layer auto-DISABLED (set EnableInMultiplayer to force it on for testing)."); _perfLogState = 1; }
                    return;
                }

                RouteHook.Active = true;   // absorb the game's own generated trash into the instanced field
                Instanced.Virtualizer.Enabled = true;   // (re-)enable player + cleaner materialization in case a
                Spawning.CleanerActor.Enabled = true;   // prior frame had the layer disabled

                // Drive the game's OWN generators harder (vanilla TrashCountMultiplier, up to 50x) and let routing
                // absorb the extra into the cheap field, around the player as they explore. 1 = vanilla amount.
                int mult = Preferences.TrashMultiplier;
                Spawning.TrashPopulator.Multiplier = mult;

                // Push the user's interactable budget + range into the Virtualizer.
                Instanced.Virtualizer.MaxReal = Preferences.MaxRealItems;
                Instanced.Virtualizer.ViewDist = Preferences.MaterializeDistance;
                Instanced.InstancedTrash.RenderDist = Preferences.RenderDistance;   // distance cull for the instanced field
                bool collideBefore = Instanced.Virtualizer.Collide;
                Instanced.Virtualizer.Collide = Preferences.ActivePhysics;   // materialized items: dynamic (on) vs frozen (off)
                // Live toggle: if active-physics changed, demote the currently-materialized items so they
                // re-materialize next frame under the new mode (otherwise only newly-seen items would reflect it).
                if (collideBefore != Instanced.Virtualizer.Collide) Instanced.Virtualizer.ClearAll();

                if (_perfLogState != 2) { Log.Msg($"[Core] Performance layer ACTIVE (maxReal={Preferences.MaxRealItems}, materializeDist={Preferences.MaterializeDistance}m, trashMult={mult})."); _perfLogState = 2; }
            }
            catch (Exception e)
            {
                Log.Warning("[Core] ApplyPerformanceLayer failed: " + e.Message);
            }
        }

        /// <summary>Fully stand down the performance layer: stop absorbing, stop materializing real items near the
        /// player + cleaners. The instanced field (if any was loaded) then renders nothing new and creates no real
        /// trash, so the game behaves vanilla.</summary>
        private static void DisableLayer()
        {
            RouteHook.Active = false;
            Instanced.Virtualizer.Enabled = false;
            Spawning.CleanerActor.Enabled = false;
            Spawning.TrashPopulator.Multiplier = 1;   // stop driving extra generation
            Spawning.TrashPopulator.Reset();          // so a later re-enable re-populates from scratch
            Instanced.InstancedTrash.Clear();         // drop the mod's field so the world is vanilla again
        }

        // Compact periodic status line (~every 15s). The Release build has no on-screen HUD or crash heartbeat, so
        // this is the one window into the running performance layer - for support and for reviewing a play session.
        private void TelemetryTick()
        {
            float dt = Time.unscaledDeltaTime;
            _teleElapsed += dt;
            _teleFrames++;
            if (dt > _teleMaxDt) _teleMaxDt = dt;
            if (_teleElapsed < 15f) return;

            try
            {
                float meanFps = _teleFrames / _teleElapsed;
                float minFps = _teleMaxDt > 0f ? 1f / _teleMaxDt : 0f;
                var tm = GameTrash.TrashManagerOrNull();
                int mgr = tm != null ? GameTrash.TrashItemCount(tm) : -1;
                Log.Msg($"[telemetry] fps={meanFps:F0} (min {minFps:F0})  field-live={Instanced.InstancedTrash.LiveCount}  real-player={Instanced.Virtualizer.RealCount}  matR={Instanced.Virtualizer.MatRadius:F1}  real-cleaner={Spawning.CleanerActor.RealCount}  mgr={mgr}  absorbed={RouteHook.Absorbed}");
            }
            catch { }
            finally { _teleElapsed = 0f; _teleFrames = 0; _teleMaxDt = 0f; }
        }

        private void HookModManager()
        {
            // Optional dependency - isolated + guarded so a missing ModManager never breaks load.
            try
            {
                // Live settings: re-apply whenever the user saves settings in the phone app or the in-game menu.
                ModManagerPhoneApp.ModSettingsEvents.OnPhonePreferencesSaved += OnSettingsSaved;
                ModManagerPhoneApp.ModSettingsEvents.OnMenuPreferencesSaved += OnSettingsSaved;
                Log.Msg("[Core] Mod Manager & Phone App hooked (settings apply live).");
            }
            catch (Exception)
            {
                Log.Msg("[Core] Mod Manager & Phone App not present - settings via the MelonPreferences config file (apply live on save).");
            }
        }

        /// <summary>
        /// Re-apply the performance layer when settings change (MelonPreferences save or the phone app), so
        /// changes - materialize distance, max real items, active physics, the FPS counter, the trash multiplier,
        /// or enabling/disabling the layer - take effect LIVE without a restart.
        /// </summary>
        private void OnSettingsSaved()
        {
            try
            {
                if (_inWorld && GameTrash.TrashManagerOrNull() != null) ApplyPerformanceLayer();
            }
            catch (Exception e) { Log.Warning("[Core] live settings re-apply failed: " + e.Message); }
#if DEBUG
            HandleCommands();
#endif
        }

        public override void OnPreferencesSaved()
        {
            OnSettingsSaved();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
            _perfApplied = false;
#if DEBUG
            if (AblationController.Active)
            {
                AblationController.Abort("scene unloaded");
            }
            PhysicsProbe.Abort("scene unloaded");
#endif
            SaveSafety.ForceClearForSave("scene unloaded");
            Instanced.InstancedTrash.Clear();
            Spawning.TrashPopulator.Reset();
#if DEBUG
            CloneSpawner.Reset();
            TrashSpawner.RestoreLimit();
#endif
        }

        public override void OnUpdate()
        {
            if (!_inWorld)
            {
                return;
            }

            // Release performance layer: turn on once we're actually in the world (player + managers exist).
            if (!_perfApplied && GameTrash.TrashManagerOrNull() != null)
            {
                ApplyPerformanceLayer();
            }

#if DEBUG
            // Lazy recorder start in case the lifecycle event was missed.
            if (!PerfSampler.RecordersInitialized && GameTrash.TrashManagerOrNull() != null)
            {
                PerfSampler.StartRecorders();
                PerfSampler.LogCapabilityReport();
                PerfSampler.ResetGcWindow();
            }

            TrashSpawner.Tick();
            PerfSampler.Tick();
            AblationController.Tick();
            PhysicsProbe.Tick();
#endif

            // ----- always-compiled performance layer -----
            // Snitch (Debug only): decompose the per-frame cost into named sections so the profiler can attribute
            // time to routing vs gravity-sim vs materialization vs the GPU-instanced render. No-op in Release.
#if SNITCH
            using (Profiler.Sample("Litterally.Route")) RouteHook.Tick();
            using (Profiler.Sample("Litterally.Sim")) Instanced.InstancedTrash.Tick(Time.deltaTime);
#else
            RouteHook.Tick();                                // drain absorbed game-trash reals (destroy after RPC fan-out)
            Instanced.InstancedTrash.Tick(Time.deltaTime);   // pure-array gravity sim
#endif
#if DEBUG
            Instanced.InstancedTrash.DriftTick(Time.deltaTime); // ground-drift self-test timer (no-op unless armed)
#endif
#if SNITCH
            using (Profiler.Sample("Litterally.Virtualize")) Instanced.Virtualizer.Tick();
            using (Profiler.Sample("Litterally.Cleaner")) Spawning.CleanerActor.Tick();
            using (Profiler.Sample("Litterally.Populate")) Spawning.TrashPopulator.Tick(Time.deltaTime);
            using (Profiler.Sample("Litterally.Render")) Instanced.InstancedTrash.Render();
#else
            Instanced.Virtualizer.Tick();                    // materialize near-player instances -> real trash
            Spawning.CleanerActor.Tick();                    // materialize near-cleaner instances -> real trash (NPCs collect them)
            Spawning.TrashPopulator.Tick(Time.deltaTime);    // drive the game's generators harder near the player -> routed into the field
            Instanced.InstancedTrash.Render();               // GPU-instanced render; no-op until set up
#endif

            TelemetryTick();                                 // compact periodic status line (release has no HUD)

#if DEBUG
            // Crash-resilient heartbeat: after a hard crash, Mods/Litterally/heartbeat.txt holds the
            // last frame's state. Distinguishes a spawn-time death (count climbing) from a post-spawn
            // / steady-state death (count steady at N, e.g. rendering).
            _frame++;
            if ((_frame & 1) == 0)
            {
                var tm = GameTrash.TrashManagerOrNull();
                int mgr = tm != null ? GameTrash.TrashItemCount(tm) : -1;
                float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
                DiagLog.Heartbeat($"frame={_frame} ours={TrashRegistry.Count} mgr={mgr} pending={TrashSpawner.Pending} fps={fps:F1} mode={(Preferences.SpawnKinematic ? "kin" : "dyn")}");
            }
#endif
        }

        public override void OnGUI()
        {
            if (!_inWorld) return;
            if (Preferences.ShowFpsCounter) UI.FpsCounter.Draw();
            if (Preferences.ShowStatsPanel) UI.StatsPanel.Draw();
            if (Preferences.ShowActiveItems || Preferences.ShowRanges) UI.DebugDraw.Draw();
        }

        public override void OnApplicationQuit()
        {
            Teardown("application quit");
        }

        public override void OnDeinitializeMelon()
        {
            Teardown("melon unload");
        }

        private void Teardown(string reason)
        {
            try
            {
#if DEBUG
                if (AblationController.Active)
                {
                    AblationController.Abort(reason);
                }
                PhysicsProbe.Abort(reason);
#endif
                SaveSafety.ForceClearForSave(reason);
#if DEBUG
                CloneSpawner.Reset();
                TrashSpawner.RestoreLimit();
                PerfSampler.Dispose();
#endif
            }
            catch { /* shutting down */ }
        }

#if DEBUG
        // ----- command dispatch (shared by MelonPrefs + ModManager events) -----

        private void HandleCommands()
        {
            try
            {
                if (Preferences.ConsumeSpawn100()) TrashSpawner.RequestSpawn(100);
                if (Preferences.ConsumeSpawn1000()) TrashSpawner.RequestSpawn(1000);
                if (Preferences.ConsumeSpawn10000()) TrashSpawner.RequestSpawn(10000);
                if (Preferences.ConsumeClear()) SaveSafety.ScopedClear();
                if (Preferences.ConsumePurgeAll()) SaveSafety.PurgeAll();
                if (Preferences.ConsumeRunSweep()) AblationController.StartSweep();
            }
            catch (Exception e)
            {
                Log.Warning("[Core] command dispatch failed: " + e.Message);
            }
        }
#endif
    }
}
