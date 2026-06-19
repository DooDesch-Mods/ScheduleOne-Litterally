using System;
using MelonLoader;
using S1API.Lifecycle;
using Trashville.Compat;
using Trashville.Config;
#if DEBUG
using Trashville.Profiling;
#endif
using Trashville.SaveGuard;
using Trashville.Spawning;
#if DEBUG
using Trashville.UI;
#endif

[assembly: MelonInfo(typeof(Trashville.Core), "Trashville", "1.0.0", "DooDesch", null)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Trashville
{
    /// <summary>
    /// MelonLoader entry point for the Trashville trash-performance mod. The release build routes the game's own
    /// generated trash into a cheap instanced field (rendered cheaply, materialized into real pickup-able items
    /// near the player + cleaners) so the world can hold far more trash with little cost. DEBUG builds additionally
    /// wire the benchmark/ablation harness (spawn pump, perf sampler, on-screen HUD, dev hotkeys + console).
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;
        private bool _perfApplied;
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
            Log.Msg("Trashville v1.0.0 (DEBUG) - trash performance layer active. Dev hotkeys: F5 ARM, F7/F8/F9 spawn 100/1k/10k, F11 sweep, F6 HUD.");
            Log.Warning("Trashville DEBUG: the benchmark spawns thousands of TEMPORARY trash - use a throwaway save. It auto-clears on save/scene-change/quit.");
#else
            Log.Msg("Trashville v1.0.0 - trash performance layer active.");
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
                    Log.Msg("[Core] Performance layer DISABLED via preferences - running vanilla trash.");
                    return;
                }

                // TODO: real multiplayer support. The instanced field is local-only (each client routes its own
                // game-trash, materialises locally), so two clients would see divergent trash and desync. Until a
                // host-authoritative sync exists, auto-disable in MP unless a tester force-enables it.
                if (Net.IsMultiplayer() && !Preferences.EnableInMultiplayer)
                {
                    DisableLayer();
                    Log.Msg("[Core] Multiplayer session detected - performance layer auto-DISABLED (set EnableInMultiplayer to force it on for testing).");
                    return;
                }

                RouteHook.Active = true;   // absorb the game's own generated trash into the instanced field
                Instanced.Virtualizer.Enabled = true;   // (re-)enable player + cleaner materialization in case a
                Spawning.CleanerActor.Enabled = true;   // prior frame had the layer disabled

                // Optional: multiply the game's own generator output. 1 = vanilla amount (no boost).
                int mult = Preferences.TrashMultiplier;
                if (mult > 1)
                {
                    GeneratorBoost.Apply(mult);
                }

                // Push the user's interactable budget + range into the Virtualizer.
                Instanced.Virtualizer.MaxReal = Preferences.MaxRealItems;
                Instanced.Virtualizer.ViewDist = Preferences.MaterializeDistance;

                Log.Msg($"[Core] Performance layer ACTIVE (maxReal={Preferences.MaxRealItems}, materializeDist={Preferences.MaterializeDistance}m, trashMult={mult}).");
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
        }

        private void HookModManager()
        {
            // Optional dependency - isolated + guarded so a missing ModManager never breaks load.
            try
            {
#if DEBUG
                ModManagerPhoneApp.ModSettingsEvents.OnPhonePreferencesSaved += HandleCommands;
                ModManagerPhoneApp.ModSettingsEvents.OnMenuPreferencesSaved += HandleCommands;
                Log.Msg("[Core] Mod Manager & Phone App hooked (in-phone buttons available).");
#else
                // Release: no in-phone action buttons; settings are read live. Touch the type so the optional
                // dependency stays referenced and the "not present" path is still exercised cleanly.
                _ = typeof(ModManagerPhoneApp.ModSettingsEvents);
                Log.Msg("[Core] Mod Manager & Phone App present - settings exposed in the phone app.");
#endif
            }
            catch (Exception)
            {
                Log.Msg("[Core] Mod Manager & Phone App not present - using the MelonPreferences config file.");
            }
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

            PollHotkeys();
            TrashSpawner.Tick();
            PerfSampler.Tick();
            AblationController.Tick();
            PhysicsProbe.Tick();
#endif

            // ----- always-compiled performance layer -----
            RouteHook.Tick();                                // drain absorbed game-trash reals (destroy after RPC fan-out)
            Instanced.InstancedTrash.Tick(Time.deltaTime);   // pure-array gravity sim
#if DEBUG
            Instanced.InstancedTrash.DriftTick(Time.deltaTime); // ground-drift self-test timer (no-op unless armed)
#endif
            Instanced.Virtualizer.Tick();                    // materialize near-player instances -> real trash
            Spawning.CleanerActor.Tick();                    // materialize near-cleaner instances -> real trash (NPCs collect them)
            Instanced.InstancedTrash.Render();               // GPU-instanced render; no-op until set up

#if DEBUG
            // Crash-resilient heartbeat: after a hard crash, Mods/Trashville/heartbeat.txt holds the
            // last frame's state. Distinguishes a spawn-time death (count climbing) from a post-spawn
            // / steady-state death (count steady at N, e.g. rendering).
            _frame++;
            if ((_frame & 1) == 0)
            {
                var tm = GameTrash.TrashManagerOrNull();
                int mgr = tm != null ? GameTrash.TrashItemCount(tm) : -1;
                float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
                DiagLog.Heartbeat($"frame={_frame} ours={TrashRegistry.Count} mgr={mgr} pending={TrashSpawner.Pending} fps={fps:F1} mode={(Preferences.SpawnKinematic ? "kin" : "dyn")} hud={Preferences.ShowHud}");
            }
#endif
        }

#if DEBUG
        public override void OnGUI()
        {
            DebugHud.Draw();
        }
#endif

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
        // ----- input (DEBUG only) -----

        private void PollHotkeys()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F5))
                {
                    bool armed = !Preferences.ArmBenchmark;
                    Preferences.SetArm(armed);
                    Log.Msg("[Core] Benchmark " + (armed ? "ARMED" : "disarmed") + ".");
                }
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    Preferences.SetShowHud(!Preferences.ShowHud);
                }
                if (Input.GetKeyDown(KeyCode.F4))
                {
                    bool kin = !Preferences.SpawnKinematic;
                    Preferences.SetSpawnKinematic(kin);
                    Log.Msg("[Core] Spawn mode = " + (kin ? "KINEMATIC (frozen, safe for 10k)" : "DYNAMIC (falling physics)") + ".");
                }
                if (Input.GetKeyDown(KeyCode.F1))
                {
                    bool bp = !Preferences.BypassCap;
                    Preferences.SetBypassCap(bp);
                    Log.Msg("[Core] Cap bypass = " + (bp ? "ON (direct clones - no 2000 limit)" : "off (game CreateTrashItem - 2000 cap)") + ".");
                }
                // While a sweep or physics A/B runs, ignore manual keys - they would pollute the
                // controlled measurement (which is exactly what corrupted the first CSV).
                if (AblationController.Active || PhysicsProbe.Active)
                {
                    if (Input.GetKeyDown(KeyCode.F3) || Input.GetKeyDown(KeyCode.F7) || Input.GetKeyDown(KeyCode.F8)
                        || Input.GetKeyDown(KeyCode.F9) || Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.F11))
                    {
                        Log.Warning("[Core] A measurement is running - manual keys ignored. Press F12 to abort.");
                    }
                    if (Input.GetKeyDown(KeyCode.F12))
                    {
                        AblationController.Abort("user pressed F12");
                        PhysicsProbe.Abort("user pressed F12");
                    }
                    return;
                }

                if (Input.GetKeyDown(KeyCode.F2)) PhysicsConfigDump.Dump();
                if (Input.GetKeyDown(KeyCode.F3)) PhysicsProbe.Start();
                if (Input.GetKeyDown(KeyCode.F7)) TrashSpawner.RequestSpawn(100);
                if (Input.GetKeyDown(KeyCode.F8)) TrashSpawner.RequestSpawn(1000);
                if (Input.GetKeyDown(KeyCode.F9)) TrashSpawner.RequestSpawn(10000);
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    {
                        SaveSafety.PurgeAll();
                    }
                    else
                    {
                        SaveSafety.ScopedClear();
                    }
                }
                if (Input.GetKeyDown(KeyCode.F11)) AblationController.StartSweep();
            }
            catch (Exception e)
            {
                Log.Warning("[Core] hotkey error: " + e.Message);
            }
        }

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

        public override void OnPreferencesSaved()
        {
            HandleCommands();
        }
#endif
    }
}
