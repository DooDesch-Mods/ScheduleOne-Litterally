using MelonLoader;
using UnityEngine;

namespace Trashville.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("Trashville_...")
    /// so it is auto-detected by the "Mod Manager &amp; Phone App" settings UI (Prowiler).
    /// Release entries (the performance layer) are always registered. The benchmark/dev entries -
    /// including the one-shot "button" toggles - only register in DEBUG builds.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "Trashville_01_Main";

        private static MelonPreferences_Category _category;

        // ----- release performance-layer entries (always compiled) -----
        private static MelonPreferences_Entry<bool> _enablePerf;
        private static MelonPreferences_Entry<bool> _enableInMp;
        private static MelonPreferences_Entry<int> _maxRealItems;
        private static MelonPreferences_Entry<float> _materializeDistance;
        private static MelonPreferences_Entry<int> _trashMultiplier;
        private static MelonPreferences_Entry<bool> _activePhysics;
        private static MelonPreferences_Entry<bool> _showFps;

#if DEBUG
        private static MelonPreferences_Entry<bool> _arm;
        private static MelonPreferences_Entry<bool> _showHud;
        private static MelonPreferences_Entry<int> _spawnPerFrame;
        private static MelonPreferences_Entry<float> _spawnRadius;
        private static MelonPreferences_Entry<float> _spawnHeight;
        private static MelonPreferences_Entry<bool> _muteImpacts;
        private static MelonPreferences_Entry<bool> _spawnKinematic;
        private static MelonPreferences_Entry<bool> _bypassCap;
        private static MelonPreferences_Entry<bool> _optimizeClones;
        private static MelonPreferences_Entry<int> _maxAwakeBudget;

        // One-shot "buttons" (toggle on -> action fires -> auto-reset to off).
        private static MelonPreferences_Entry<bool> _btnSpawn100;
        private static MelonPreferences_Entry<bool> _btnSpawn1000;
        private static MelonPreferences_Entry<bool> _btnSpawn10000;
        private static MelonPreferences_Entry<bool> _btnClear;
        private static MelonPreferences_Entry<bool> _btnPurgeAll;
        private static MelonPreferences_Entry<bool> _btnRunSweep;
#endif

        internal static void Initialize()
        {
            if (_category != null)
            {
                return;
            }

#if DEBUG
            _category = MelonPreferences.CreateCategory(CategoryId, "Trashville (Trash Performance + Benchmark)");
#else
            _category = MelonPreferences.CreateCategory(CategoryId, "Trashville (Trash Performance)");
#endif

            // ----- release performance-layer entries -----
            _enablePerf = Create("EnablePerformanceLayer", true, "Enable performance layer",
                "Master switch for the trash performance layer. When ON (default), the game's own generated trash is " +
                "absorbed into a lightweight instanced field so the world can hold far more trash with little cost. " +
                "Turn OFF to run fully vanilla (no routing, no instancing).");
            _enableInMp = Create("EnableInMultiplayer", false, "Enable in multiplayer (experimental)",
                "OFF (default): the performance layer auto-disables in a multiplayer session because the instanced " +
                "field is local-only and would desync between players. ON: force-enable it in multiplayer for testing " +
                "- expect visual desync between clients. Leave OFF unless you are testing.");
            _maxRealItems = Create("MaxRealItems", 200, "Max real (interactable) items",
                "How many trash items are materialised as real, pickup-able objects around you at once (default 200). " +
                "Higher = a larger interactable radius around you, at a small per-item cost. Clamped 50-2000.",
                new MelonLoader.Preferences.ValueRange<int>(50, 2000));
            _materializeDistance = Create("MaterializeDistance", 32f, "Materialize distance (m)",
                "How far ahead of you trash becomes real/interactable (inside the view). Beyond this it is drawn cheaply " +
                "as instanced data. Higher = interactable further out, at more cost. Clamped 8-80.");
            _trashMultiplier = Create("TrashMultiplier", 10, "Trash amount multiplier",
                "Multiplies the game's OWN trash density (vanilla 0.015/m2 x this). 1 = vanilla; 10 = default; " +
                "50 ~ 100,000; up to 1000 ~ 2,000,000 total across the map (extreme - very high values cost FPS " +
                "as dense areas fill). The performance layer absorbs it into a cheap instanced field and fills " +
                "the world around you as you explore. Clamped 1-1000.",
                new MelonLoader.Preferences.ValueRange<int>(1, 1000));
            _activePhysics = Create("ActivePhysics", false, "Materialized trash has active physics",
                "OFF (default) = trash near you is frozen at its resting pose (cheapest, seamless). ON = the real " +
                "items materialized around you have live physics (they fall/settle and can be shoved). Applies live.");
            _showFps = Create("ShowFpsCounter", false, "Show FPS counter",
                "Shows a small on-screen FPS readout (top-right). OFF by default. Applies live.");

#if DEBUG
            _arm = Create("ArmBenchmark", false, "ARM benchmark (spawns thousands of trash)",
                "Master safety switch. While OFF, all spawning is a no-op. Spawned trash is auto-cleared on " +
                "save / scene-change / quit and is NOT meant to persist. Only enable while benchmarking on a throwaway save.");
            _showHud = Create("ShowHud", true, "Show on-screen HUD",
                "Show the live performance readout overlay (FPS, frame ms, item count). Toggle in game with F6.");
            _spawnPerFrame = Create("SpawnPerFrame", 50, "Spawn per frame",
                "How many trash items to spawn each frame while a batch is pending (auto-throttled on heavy frames). Lower = safer.");
            _spawnRadius = Create("SpawnRadius", 12f, "Spawn radius (m)",
                "Trash is scattered in a disc of this radius around the player.");
            _spawnHeight = Create("SpawnHeight", 4f, "Spawn drop height (m)",
                "Trash is dropped from this height above the player so the pile settles.");
            _muteImpacts = Create("MuteImpactSounds", true, "Mute collision SFX on spawned trash",
                "Disables the RBImpactSounds component on spawned trash. The per-collision impact-sound storm " +
                "(SFXManager pool exhaustion -> thousands of warning logs) overloads and CRASHES the game when many " +
                "trash collide at once - it is itself a measured perf killer. Keep ON for stability.");
            _bypassCap = Create("BypassCap", false, "Bypass the 2000 cap (direct clones)",
                "ON = spawn our OWN trash clones (Object.Instantiate of a stripped prefab) instead of the game's " +
                "CreateTrashItem - no 2000 limit, no eviction/despawn, no save, no networking. Required to exceed 2000 / " +
                "reach 10000. Clones are auto-destroyed on save/scene-change/quit. Toggle in game with F1.");
            _maxAwakeBudget = Create("MaxAwakeBudget", 2500, "Max simultaneously-falling (0 = unlimited)",
                "Staggers bypass spawning so no more than this many clones are AWAKE/falling at once - new spawns " +
                "wait until earlier ones land and sleep. Since a sleeping body is ~free, this keeps fps high while " +
                "building up to 10000 piled. 0 = spawn all at once (heaviest). The key feasibility lever.");
            _optimizeClones = Create("OptimizeClones", true, "Optimize clone physics (cheaper falling)",
                "ON = apply per-object physics levers to bypass clones: Discrete collision detection (vs the prefab's " +
                "expensive Continuous), no interpolation, fewer solver iterations, higher sleep threshold, and drop the " +
                "convex MeshCollider (keep the BoxCollider). Makes 10000 falling far cheaper. Per-object only, safe.");
            _spawnKinematic = Create("SpawnKinematic", false, "Spawn kinematic (no fall/physics)",
                "OFF (default) = realistic falling pile with active physics (the real scenario; impact SFX are muted so it " +
                "is stable). ON = trash spawns frozen on the ground (no falling, no collisions) to ISOLATE render/collider/" +
                "script/network/memory cost from active physics. Toggle in game with F4. (Either way the game caps live trash at 2000.)");

            _btnSpawn100 = Create("Spawn100", false, "> Spawn 100 (one-shot)", "Toggle ON to spawn 100 trash around you. Auto-resets.");
            _btnSpawn1000 = Create("Spawn1000", false, "> Spawn 1,000 (one-shot)", "Toggle ON to spawn 1,000 trash around you. Auto-resets.");
            _btnSpawn10000 = Create("Spawn10000", false, "> Spawn 10,000 (one-shot)", "Toggle ON to spawn 10,000 trash around you. Auto-resets.");
            _btnClear = Create("ClearTrash", false, "> Clear MY benchmark trash (one-shot)", "Toggle ON to destroy only the trash this mod spawned. Auto-resets.");
            _btnPurgeAll = Create("PurgeAllTrash", false, "> PURGE ALL world trash (one-shot)", "Toggle ON to call DestroyAllTrash() - removes ALL trash in the world, including legitimate trash. Recovery for a bloated save. Auto-resets.");
            _btnRunSweep = Create("RunSweep", false, "> Run automated benchmark sweep (one-shot)", "Toggle ON to run the full spawn + ablation sweep and write a CSV under Mods/Trashville/runs/. Auto-resets.");
#endif
        }

        private static MelonPreferences_Entry<T> Create<T>(string id, T def, string name, string desc = null,
            MelonLoader.Preferences.ValueValidator validator = null)
        {
            return validator == null
                ? _category.CreateEntry(id, def, name, desc)
                : _category.CreateEntry(id, def, name, desc, false, false, validator);
        }

        // ----- release accessors (always compiled) -----

        internal static bool EnablePerformanceLayer => _enablePerf?.Value ?? true;
        internal static bool EnableInMultiplayer => _enableInMp?.Value ?? false;
        internal static int MaxRealItems => Mathf.Clamp(_maxRealItems?.Value ?? 200, 50, 2000);
        internal static float MaterializeDistance => Mathf.Clamp(_materializeDistance?.Value ?? 32f, 8f, 80f);
        internal static int TrashMultiplier => Mathf.Clamp(_trashMultiplier?.Value ?? 10, 1, 1000);
        internal static bool ActivePhysics => _activePhysics?.Value ?? false;
        internal static bool ShowFpsCounter => _showFps?.Value ?? false;

#if DEBUG
        // ----- benchmark accessors (DEBUG only) -----

        internal static bool ArmBenchmark => _arm?.Value ?? false;
        internal static bool ShowHud => _showHud?.Value ?? true;
        internal static int SpawnPerFrame => Mathf.Clamp(_spawnPerFrame?.Value ?? 25, 1, 2000);
        internal static float SpawnRadius => Mathf.Clamp(_spawnRadius?.Value ?? 12f, 1f, 100f);
        internal static float SpawnHeight => Mathf.Clamp(_spawnHeight?.Value ?? 4f, 0f, 50f);
        internal static bool MuteImpactSounds => _muteImpacts?.Value ?? true;
        internal static bool SpawnKinematic => _spawnKinematic?.Value ?? false;
        internal static bool BypassCap => _bypassCap?.Value ?? false;
        internal static bool OptimizeClones => _optimizeClones?.Value ?? true;
        internal static int MaxAwakeBudget => Mathf.Max(0, _maxAwakeBudget?.Value ?? 1200);

        internal static void SetMaxAwakeBudget(int value)
        {
            if (_maxAwakeBudget != null)
            {
                _maxAwakeBudget.Value = Mathf.Max(0, value);
            }
        }

        internal static void SetOptimizeClones(bool value)
        {
            if (_optimizeClones != null)
            {
                _optimizeClones.Value = value;
            }
        }

        internal static void SetBypassCap(bool value)
        {
            if (_bypassCap != null)
            {
                _bypassCap.Value = value;
            }
        }

        internal static void SetSpawnKinematic(bool value)
        {
            if (_spawnKinematic != null)
            {
                _spawnKinematic.Value = value;
            }
        }

        internal static void SetArm(bool value)
        {
            if (_arm != null)
            {
                _arm.Value = value;
            }
        }

        internal static void SetShowHud(bool value)
        {
            if (_showHud != null)
            {
                _showHud.Value = value;
            }
        }

        // ----- one-shot button consumers (DEBUG only) -----

        internal static bool ConsumeSpawn100() => Consume(_btnSpawn100);
        internal static bool ConsumeSpawn1000() => Consume(_btnSpawn1000);
        internal static bool ConsumeSpawn10000() => Consume(_btnSpawn10000);
        internal static bool ConsumeClear() => Consume(_btnClear);
        internal static bool ConsumePurgeAll() => Consume(_btnPurgeAll);
        internal static bool ConsumeRunSweep() => Consume(_btnRunSweep);

        private static bool Consume(MelonPreferences_Entry<bool> entry)
        {
            if (entry != null && entry.Value)
            {
                entry.Value = false;   // in-memory reset; avoids a save -> OnPreferencesSaved loop
                return true;
            }
            return false;
        }
#endif
    }
}
