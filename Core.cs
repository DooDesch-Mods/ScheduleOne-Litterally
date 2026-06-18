using System;
using MelonLoader;
using S1API.Lifecycle;
using Trashville.Config;
using Trashville.Profiling;
using Trashville.SaveGuard;
using Trashville.Spawning;
using Trashville.UI;

[assembly: MelonInfo(typeof(Trashville.Core), "Trashville", "0.1.0", "DooDesch", null)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Trashville
{
    /// <summary>
    /// MelonLoader entry point for the Trashville trash-benchmark harness. Wires the spawn pump,
    /// the performance sampler, the ablation sweep, the on-screen HUD, and the save-safety guards.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;
        private int _frame;

        [System.Diagnostics.Conditional("DEBUG")]
        public static void LogDebug(string msg) { Log?.Msg(msg); }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();

            // Console bridge: lets the Schedule1 MCP drive the mod via "tv ..." dev-console commands.
            try { HarmonyInstance.PatchAll(); } catch (Exception e) { Log.Warning("[Core] Harmony patch failed: " + e.Message); }

            // Save-safety: benchmark trash must never be serialized. Clear before any save / scene change.
            GameLifecycle.OnSaveStart += () => SaveSafety.ForceClearForSave("save starting");
            GameLifecycle.OnPreSceneChange += () => SaveSafety.ForceClearForSave("scene changing");
            GameLifecycle.OnLoadComplete += OnGameLoaded;

            HookModManager();

            Log.Msg("Trashville initialized. Press F5 to ARM, then F7/F8/F9 to spawn 100/1k/10k. F11 = automated sweep.");
            Log.Warning("Trashville spawns thousands of TEMPORARY trash for benchmarking - use a throwaway save. It auto-clears on save/scene-change/quit.");
        }

        private void OnGameLoaded()
        {
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
        }

        private void HookModManager()
        {
            // Optional dependency - isolated + guarded so a missing ModManager never breaks load.
            try
            {
                ModManagerPhoneApp.ModSettingsEvents.OnPhonePreferencesSaved += HandleCommands;
                ModManagerPhoneApp.ModSettingsEvents.OnMenuPreferencesSaved += HandleCommands;
                Log.Msg("[Core] Mod Manager & Phone App hooked (in-phone buttons available).");
            }
            catch (Exception)
            {
                Log.Msg("[Core] Mod Manager & Phone App not present - using hotkeys + HUD only.");
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
            if (AblationController.Active)
            {
                AblationController.Abort("scene unloaded");
            }
            PhysicsProbe.Abort("scene unloaded");
            SaveSafety.ForceClearForSave("scene unloaded");
            Instanced.InstancedTrash.Clear();
            CloneSpawner.Reset();
            TrashSpawner.RestoreLimit();
        }

        public override void OnUpdate()
        {
            if (!_inWorld)
            {
                return;
            }

            // Lazy recorder start in case the lifecycle event was missed.
            if (!PerfSampler.RecordersInitialized && TrashSpawner.TrashManagerOrNull() != null)
            {
                OnGameLoaded();
            }

            PollHotkeys();
            TrashSpawner.Tick();
            PerfSampler.Tick();
            AblationController.Tick();
            PhysicsProbe.Tick();
            Instanced.InstancedTrash.Tick(Time.deltaTime);   // pure-array gravity sim (100k path)
            Instanced.Virtualizer.Tick();                    // materialize near-player instances -> real trash
            Instanced.InstancedTrash.Render();               // GPU-instanced render; no-op until set up

            // Crash-resilient heartbeat: after a hard crash, Mods/Trashville/heartbeat.txt holds the
            // last frame's state. Distinguishes a spawn-time death (count climbing) from a post-spawn
            // / steady-state death (count steady at N, e.g. rendering).
            _frame++;
            if ((_frame & 1) == 0)
            {
                var tm = TrashSpawner.TrashManagerOrNull();
                int mgr = tm != null ? TrashSpawner.TrashItemCount(tm) : -1;
                float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
                DiagLog.Heartbeat($"frame={_frame} ours={TrashRegistry.Count} mgr={mgr} pending={TrashSpawner.Pending} fps={fps:F1} mode={(Preferences.SpawnKinematic ? "kin" : "dyn")} hud={Preferences.ShowHud}");
            }
        }

        public override void OnGUI()
        {
            DebugHud.Draw();
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
                if (AblationController.Active)
                {
                    AblationController.Abort(reason);
                }
                PhysicsProbe.Abort(reason);
                SaveSafety.ForceClearForSave(reason);
                CloneSpawner.Reset();
                TrashSpawner.RestoreLimit();
                PerfSampler.Dispose();
            }
            catch { /* shutting down */ }
        }

        // ----- input -----

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
    }
}
