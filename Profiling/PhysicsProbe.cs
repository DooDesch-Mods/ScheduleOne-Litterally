using System;
using System.Globalization;
using UnityEngine;
using Litterally.Spawning;

namespace Litterally.Profiling
{
    /// <summary>
    /// Clean physics A/B on the SAME items (minimal baseline drift): spawn N dynamic, measure the active
    /// SETTLING window (falling + colliding), let them sleep, measure SETTLED (asleep, still dynamic), then
    /// FREEZE them (kinematic) and measure. Deltas isolate active-physics cost vs sleeping-rigidbody overhead
    /// vs the static baseline - the missing piece the kinematic-only sweep can't capture. Trigger: F3.
    /// </summary>
    internal static class PhysicsProbe
    {
        private enum P { Idle, PurgeSpawn, SpawnWait, SettleMeasure, SleepWait, SettledWarm, FreezeWarm, Done }

        private const int Warmup = 130;
        private const int MaxExtra = 900;
        private const double NoiseThreshold = 0.22;
        private const int SettlingWindow = 120;   // frames of active settling to capture right after spawn
        private const int SleepWait = 600;        // frames to let the pile auto-sleep

        private static P _state = P.Idle;
        private static int _timer, _extra;
        private static bool _stable;
        private static int _count;
        private static bool _savedKinematic;

        private static FrameStats _settling, _settled, _frozen;

        internal static bool Active => _state != P.Idle;
        internal static string Status { get; private set; } = "idle";

        internal static void Start()
        {
            if (Active)
            {
                return;
            }
            if (!Config.Preferences.ArmBenchmark)
            {
                Core.Log?.Warning("[PhysAB] Arm the benchmark first (F5).");
                return;
            }
            if (AblationController.Active)
            {
                Core.Log?.Warning("[PhysAB] A sweep is running.");
                return;
            }
            var tm = TrashSpawner.TrashManagerOrNull();
            if (tm == null)
            {
                Core.Log?.Warning("[PhysAB] Not in the world yet.");
                return;
            }

            int limit = SafeLimit();
            _count = limit > 0 ? Math.Min(1000, Math.Max(100, limit - 64)) : 1000;
            _savedKinematic = Config.Preferences.SpawnKinematic;
            PerfSampler.UncapFramerate();
            _state = P.PurgeSpawn;
            Status = "starting";
            Core.Log?.Msg($"[PhysAB] Start: dynamic settling vs sleeping vs frozen @ {_count}. Don't touch keys (F12 aborts).");
        }

        internal static void Abort(string reason)
        {
            if (!Active)
            {
                return;
            }
            Core.Log?.Warning("[PhysAB] Aborted: " + reason);
            Finish();
        }

        internal static void Tick()
        {
            switch (_state)
            {
                case P.PurgeSpawn:
                    SaveGuard.SaveSafety.PurgeAll();
                    Config.Preferences.SetSpawnKinematic(false);   // dynamic = real falling pile
                    TrashSpawner.RequestSpawn(_count);
                    Status = "spawning dynamic";
                    _state = P.SpawnWait;
                    break;

                case P.SpawnWait:
                    if (!TrashSpawner.IsBusy)
                    {
                        _timer = SettlingWindow;   // capture the active-collision window now
                        Status = "measuring SETTLING (active collisions)";
                        _state = P.SettleMeasure;
                    }
                    break;

                case P.SettleMeasure:
                    if (--_timer <= 0)
                    {
                        _settling = PerfSampler.Snapshot();
                        _timer = SleepWait;
                        Status = "waiting for the pile to sleep";
                        _state = P.SleepWait;
                    }
                    break;

                case P.SleepWait:
                    if (--_timer <= 0)
                    {
                        EnterGate();
                        Status = "measuring SETTLED (asleep)";
                        _state = P.SettledWarm;
                    }
                    break;

                case P.SettledWarm:
                    if (GateReady())
                    {
                        _settled = PerfSampler.Snapshot();
                        FreezeAll();   // flip every item to kinematic - same objects, no respawn
                        EnterGate();
                        Status = "measuring FROZEN (kinematic)";
                        _state = P.FreezeWarm;
                    }
                    break;

                case P.FreezeWarm:
                    if (GateReady())
                    {
                        _frozen = PerfSampler.Snapshot();
                        _state = P.Done;
                    }
                    break;

                case P.Done:
                    Report();
                    Finish();
                    break;
            }
        }

        private static void FreezeAll()
        {
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try
                {
                    Rigidbody rb = item.Rigidbody;
                    if (rb != null) rb.isKinematic = true;
                    item.SetPhysicsActive(false);
                }
                catch { }
            }
        }

        private static void Report()
        {
            double set = _settling.MeanMs, setP95 = _settling.P95Ms;
            double slept = _settled.MeanMs;
            double froz = _frozen.MeanMs;
            string msg =
                $"[PhysAB] @{_count}  FROZEN(kinematic) {froz:F1}ms ({Fps(froz)}fps) | " +
                $"SETTLED(asleep) {slept:F1}ms ({Fps(slept)}fps) | " +
                $"SETTLING(active) {set:F1}ms p95 {setP95:F1} ({Fps(set)}fps).  " +
                $"=> active-physics cost = {set - froz:F1}ms (settling) ; sleeping-body overhead = {slept - froz:F1}ms.";
            Core.Log?.Msg(msg);
            DiagLog.Note(msg);
        }

        private static string Fps(double ms) => ms > 0 ? (1000.0 / ms).ToString("F0", CultureInfo.InvariantCulture) : "0";

        private static void Finish()
        {
            Config.Preferences.SetSpawnKinematic(_savedKinematic);
            PerfSampler.RestoreFramerate();
            SaveGuard.SaveSafety.PurgeAll();
            _state = P.Idle;
            Status = "idle";
        }

        // ----- stability gate (same as the sweep) -----

        private static void EnterGate()
        {
            _timer = Warmup;
            _extra = MaxExtra;
        }

        private static bool GateReady()
        {
            if (_timer > 0) { _timer--; return false; }
            double noise = PerfSampler.RelativeNoise();
            if (noise <= NoiseThreshold || _extra <= 0)
            {
                _stable = noise <= NoiseThreshold;
                return true;
            }
            _extra--;
            return false;
        }

        private static int SafeLimit()
        {
            try { return TrashManager.TRASH_ITEM_LIMIT; } catch { return -1; }
        }
    }
}
