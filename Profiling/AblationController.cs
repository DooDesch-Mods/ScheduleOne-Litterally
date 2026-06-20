using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Litterally.Spawning;

namespace Litterally.Profiling
{
    /// <summary>
    /// Drives the automated benchmark: for each trash count it records the all-on SETTLING and SETTLED
    /// frame cost, then toggles one subsystem off at a time and records the frame-time delta. The deltas
    /// (not any profiler counter) are the build-independent evidence of which subsystem costs the most.
    /// Output is a CSV under Mods/Litterally/runs/.
    /// </summary>
    internal static class AblationController
    {
        // Counts stay under the game's hard cap (TRASH_ITEM_LIMIT = 2000) so no eviction churn pollutes
        // the measurement. The sweep forces KINEMATIC spawning => a stable scene (no settling physics),
        // which is the only way to get low-noise frame-time deltas for render/collider/script cost.
        private static readonly int[] Counts = { 0, 250, 500, 1000, 1500, 1950 };

        // Frame-count waits (time-independent, so they hold even at low fps).
        private const int WarmupFrames = 130;        // > window size, so the sampled window is fully post-change
        private const int MaxExtraFrames = 900;      // max extra frames to wait for the noise to settle
        private const double NoiseThreshold = 0.22;  // stddev/mean must be <= this to record a cell as stable

        private enum S { Idle, ClearBefore, SpawnWait, SteadyWarm, LeverApplyWarm, LeverRestore, NextN, End }

        private static S _state = S.Idle;
        private static int _ni;          // index into Counts
        private static int _timer;       // warmup frame countdown
        private static int _extraWait;   // remaining extra frames to wait for stability
        private static bool _lastStable; // did the last recorded cell meet the noise gate?
        private static int _leverIdx;    // index into the lever list

        private static FrameStats _steadyBaseline;    // all-on steady, for delta-vs-all-on
        private static FrameStats _n0Baseline;        // N=0 steady, for delta-vs-engine
        private static bool _haveN0;
        private static bool _savedKinematic;

        private static StreamWriter _csv;
        private static System.Collections.Generic.List<TrashGenerator> _pausedGenerators;

        internal static bool Active => _state != S.Idle;
        internal static string Status { get; private set; } = "idle";

        // ----- public control -----

        internal static void StartSweep()
        {
            if (Active)
            {
                Core.Log?.Warning("[Sweep] Already running.");
                return;
            }
            if (!Config.Preferences.ArmBenchmark)
            {
                Core.Log?.Warning("[Sweep] Disarmed - arm the benchmark first (F5).");
                return;
            }
            if (TrashSpawner.TrashManagerOrNull() == null)
            {
                Core.Log?.Warning("[Sweep] TrashManager not ready - be in the world.");
                return;
            }

            if (!OpenCsv())
            {
                return;
            }

            // Clean, controlled environment: remove ALL world trash, force kinematic (stable) spawning,
            // pause the game's auto-generator, uncap the framerate.
            _savedKinematic = Config.Preferences.SpawnKinematic;
            Config.Preferences.SetSpawnKinematic(true);
            SaveGuard.SaveSafety.PurgeAll();
            PauseTrashGenerators();
            PerfSampler.UncapFramerate();
            _ni = 0;
            _haveN0 = false;
            _state = S.ClearBefore;
            Status = "starting";
            Core.Log?.Msg("[Sweep] Started (kinematic, purged). Face the trash pile so render cost is captured. F12 aborts.");
        }

        internal static void Abort(string reason)
        {
            if (!Active)
            {
                return;
            }
            Core.Log?.Warning("[Sweep] Aborted: " + reason);
            Finish();
        }

        // ----- per-frame driver -----

        internal static void Tick()
        {
            if (_state == S.Idle)
            {
                return;
            }

            switch (_state)
            {
                case S.ClearBefore:
                    SaveGuard.SaveSafety.PurgeAll();   // truly clean baseline - no leftover world trash
                    if (Counts[_ni] > 0)
                    {
                        TrashSpawner.RequestSpawn(Counts[_ni]);
                    }
                    Status = $"N={Counts[_ni]} spawning";
                    _state = S.SpawnWait;
                    break;

                case S.SpawnWait:
                    if (!TrashSpawner.IsBusy)
                    {
                        EnterGate();
                        _state = S.SteadyWarm;
                        Status = $"N={Counts[_ni]} stabilizing";
                    }
                    break;

                case S.SteadyWarm:
                    if (GateReady())
                    {
                        FrameStats st = Snapshot();
                        _steadyBaseline = st;
                        if (!_haveN0)
                        {
                            _n0Baseline = st;
                            _haveN0 = true;
                        }
                        RecordRow("STEADY", "all-on", "ON", st);
                        _leverIdx = 0;
                        BeginLever();
                    }
                    break;

                case S.LeverApplyWarm:
                    if (GateReady())
                    {
                        RecordRow("STEADY", Levers[_leverIdx].Name, "OFF", Snapshot());
                        _state = S.LeverRestore;
                    }
                    break;

                case S.LeverRestore:
                    try { Levers[_leverIdx].Restore?.Invoke(); } catch { }
                    _leverIdx++;
                    BeginLever();
                    break;

                case S.NextN:
                    SaveGuard.SaveSafety.PurgeAll();
                    _ni++;
                    if (_ni >= Counts.Length)
                    {
                        _state = S.End;
                    }
                    else
                    {
                        _state = S.ClearBefore;
                    }
                    break;

                case S.End:
                    Core.Log?.Msg("[Sweep] Complete.");
                    Finish();
                    break;
            }
        }

        private static void BeginLever()
        {
            // Skip levers we cannot run for this cell.
            while (_leverIdx < Levers.Length)
            {
                if (Counts[_ni] == 0)
                {
                    _leverIdx++;   // nothing spawned to act on
                    continue;
                }
                if (Levers[_leverIdx].Name == "physics" && Config.Preferences.SpawnKinematic)
                {
                    // Items spawned kinematic: physics is already off, and turning it ON for a huge
                    // pile storms/crashes. Skip - measure active-physics manually at smaller counts.
                    Core.Log?.Msg("[Sweep] skipping physics lever (kinematic spawn).");
                    _leverIdx++;
                    continue;
                }
                break;
            }
            if (_leverIdx >= Levers.Length)
            {
                _state = S.NextN;
                return;
            }
            TrashRegistry.Compact();
            try { Levers[_leverIdx].Apply?.Invoke(); } catch (Exception e) { Core.Log?.Warning("[Sweep] lever apply failed: " + e.Message); }
            EnterGate();
            _state = S.LeverApplyWarm;
            Status = $"N={Counts[_ni]} {Levers[_leverIdx].Name}-off";
        }

        private static FrameStats Snapshot() => PerfSampler.Snapshot();

        // ----- stability gate: warm up, then wait until frame time is low-noise before recording -----

        private static void EnterGate()
        {
            _timer = WarmupFrames;
            _extraWait = MaxExtraFrames;
        }

        private static bool GateReady()
        {
            if (_timer > 0)
            {
                _timer--;
                return false;
            }
            double noise = PerfSampler.RelativeNoise();   // stddev / mean
            if (noise <= NoiseThreshold || _extraWait <= 0)
            {
                _lastStable = noise <= NoiseThreshold;
                return true;
            }
            _extraWait--;
            return false;
        }

        // ----- lever definitions -----

        private sealed class Lever
        {
            public string Name;
            public Action Apply;
            public Action Restore;   // null => terminal (no restore; we clear N afterwards)
        }

        // NOTE: no "net" lever - it would write TrashManager.TRASH_REPLICATIONS_PER_SECOND, which (like
        // TRASH_ITEM_LIMIT) is a game const and crashes on write. Networking cost is not ablatable this way.
        private static readonly Lever[] Levers =
        {
            new Lever { Name = "physics",   Apply = () => SetPhysics(false), Restore = () => SetPhysics(true) },
            new Lever { Name = "colliders", Apply = () => SetColliders(false), Restore = () => SetColliders(true) },
            new Lever { Name = "renderers", Apply = () => SetRenderers(false), Restore = () => SetRenderers(true) },
            new Lever { Name = "scripts",   Apply = ScriptsOff, Restore = null },   // terminal: CancelInvoke can't be undone
        };

        private static void SetPhysics(bool on)
        {
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try
                {
                    Rigidbody rb = item.Rigidbody;
                    if (rb != null) rb.isKinematic = !on;
                    item.SetPhysicsActive(on);
                }
                catch { }
            }
        }

        private static void SetColliders(bool on)
        {
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try { item.SetCollidersEnabled(on); } catch { }
            }
        }

        private static void SetRenderers(bool on)
        {
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try
                {
                    Il2CppArrayBase<Renderer> rs = item.GetComponentsInChildren<Renderer>(true);
                    if (rs == null) continue;
                    for (int i = 0; i < rs.Length; i++)
                    {
                        if (rs[i] != null) rs[i].enabled = on;
                    }
                }
                catch { }
            }
        }

        // NetRestore kept as a no-op so Finish() stays simple; we never write the (const) replication field.
        private static void NetRestore() { }

        private static void ScriptsOff()
        {
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try
                {
                    item.CancelInvoke();   // stops the natively-scheduled MinPass/Recheck tick
                    item.enabled = false;
                }
                catch { }
            }
        }

        private static void CountBodies(out int awake, out int sleeping)
        {
            awake = 0;
            sleeping = 0;
            foreach (TrashItem item in TrashRegistry.Spawned)
            {
                if (item == null) continue;
                try
                {
                    Rigidbody rb = item.Rigidbody;
                    if (rb == null) continue;
                    if (rb.IsSleeping()) sleeping++; else awake++;
                }
                catch { }
            }
        }

        // ----- CSV -----

        private static bool OpenCsv()
        {
            try
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Litterally", "runs");
                Directory.CreateDirectory(dir);
                string runId = "sweep_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, runId + ".csv");
                _csv = new StreamWriter(path, false);
                _csv.WriteLine("trashCount,phase,subsystem,subsystemState,stable,frameMeanMs,frameMedianMs,frameP95Ms,frameP99Ms,frameMinFps,frameStdDevMs,deltaVsAllOnMs,deltaVsBaselineN0Ms,awakeBodies,sleepingBodies,managerTrashCount,gc0Per1000f,gc1Per1000f," + CounterHeader());
                _csv.Flush();
                Core.Log?.Msg("[Sweep] Writing " + path);
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[Sweep] Could not open CSV: " + e.Message);
                return false;
            }
        }

        private static string CounterHeader()
        {
            var sb = new System.Text.StringBuilder();
            foreach (CounterProbe p in PerfSampler.Probes)
            {
                string col = p.Label.Replace(' ', '_').Replace('/', '_');
                sb.Append(col).Append(',').Append(col).Append("_validity,");
            }
            if (sb.Length > 0) sb.Length--;   // drop trailing comma
            return sb.ToString();
        }

        private static void RecordRow(string phase, string subsystem, string state, FrameStats st)
        {
            CountBodies(out int awake, out int sleeping);
            var tm = TrashSpawner.TrashManagerOrNull();
            int mgrCount = tm != null ? TrashSpawner.TrashItemCount(tm) : 0;
            double dAll = st.MeanMs - _steadyBaseline.MeanMs;
            double dN0 = _haveN0 ? st.MeanMs - _n0Baseline.MeanMs : 0.0;

            var sb = new System.Text.StringBuilder(256);
            sb.Append(Counts[_ni]).Append(',');
            sb.Append(phase).Append(',');
            sb.Append(subsystem).Append(',');
            sb.Append(state).Append(',');
            sb.Append(_lastStable ? "1" : "0").Append(',');
            sb.Append(F(st.MeanMs)).Append(',');
            sb.Append(F(st.MedianMs)).Append(',');
            sb.Append(F(st.P95Ms)).Append(',');
            sb.Append(F(st.P99Ms)).Append(',');
            sb.Append(F(st.MinFps)).Append(',');
            sb.Append(F(st.StdDevMs)).Append(',');
            sb.Append(F(dAll)).Append(',');
            sb.Append(F(dN0)).Append(',');
            sb.Append(awake).Append(',');
            sb.Append(sleeping).Append(',');
            sb.Append(mgrCount).Append(',');
            sb.Append(F(PerfSampler.Gc0Per1000Frames())).Append(',');
            sb.Append(F(PerfSampler.Gc1Per1000Frames())).Append(',');
            foreach (CounterProbe p in PerfSampler.Probes)
            {
                sb.Append(p.State == CounterState.Measured ? p.LastValue.ToString(CultureInfo.InvariantCulture) : "NA").Append(',');
                sb.Append(p.State).Append(',');
            }
            if (PerfSampler.Probes.Count > 0) sb.Length--;

            try
            {
                _csv?.WriteLine(sb.ToString());
                _csv?.Flush();
            }
            catch { }

            PerfSampler.ResetGcWindow();   // start a fresh GC window for the next cell
            Core.Log?.Msg($"[Sweep] N={Counts[_ni]} {subsystem}-{state}: mean={st.MeanMs:F2}ms p95={st.P95Ms:F2}ms dAllOn={dAll:F2}ms stable={_lastStable} mgr={mgrCount}");
        }

        private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

        // ----- TrashGenerator pausing (measurement cleanliness) -----

        private static void PauseTrashGenerators()
        {
            _pausedGenerators = new System.Collections.Generic.List<TrashGenerator>();
            try
            {
                Il2CppArrayBase<TrashGenerator> gens = UnityEngine.Object.FindObjectsOfType<TrashGenerator>();
                if (gens != null)
                {
                    for (int i = 0; i < gens.Length; i++)
                    {
                        TrashGenerator g = gens[i];
                        if (g != null && g.enabled)
                        {
                            g.enabled = false;
                            _pausedGenerators.Add(g);
                        }
                    }
                }
                Core.Log?.Msg($"[Sweep] Paused {_pausedGenerators.Count} TrashGenerator(s) for the run.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sweep] Could not pause TrashGenerators: " + e.Message);
            }
        }

        private static void ResumeTrashGenerators()
        {
            if (_pausedGenerators == null) return;
            foreach (TrashGenerator g in _pausedGenerators)
            {
                try { if (g != null) g.enabled = true; } catch { }
            }
            _pausedGenerators = null;
        }

        private static void Finish()
        {
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
            NetRestore();
            ResumeTrashGenerators();
            PerfSampler.RestoreFramerate();
            Config.Preferences.SetSpawnKinematic(_savedKinematic);   // restore the user's spawn mode
            SaveGuard.SaveSafety.PurgeAll();
            _state = S.Idle;
            Status = "idle";
        }
    }
}
