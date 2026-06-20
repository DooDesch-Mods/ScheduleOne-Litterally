using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Litterally.Profiling
{
    internal enum CounterState { Unavailable, Suspect, Measured }

    /// <summary>Computed frame-time distribution over the rolling window.</summary>
    internal struct FrameStats
    {
        public int Samples;
        public double MeanMs, MedianMs, P95Ms, P99Ms, MinMs, MaxMs, StdDevMs;
        public double MinFps => MaxMs > 0.0 ? 1000.0 / MaxMs : 0.0;   // worst frame -> lowest fps
        public double MeanFps => MeanMs > 0.0 ? 1000.0 / MeanMs : 0.0;
    }

    /// <summary>
    /// Frame-time sampler (the backbone of the evidence) plus opportunistic ProfilerRecorder probes.
    /// Frame timing is build-independent and always works. ProfilerRecorder counters may be inert in a
    /// release IL2CPP player, so each probe self-certifies MEASURED / SUSPECT / UNAVAILABLE and is never
    /// the basis of a cost claim.
    /// </summary>
    internal static class PerfSampler
    {
        private const int WindowSize = 120;
        private static readonly double[] _ring = new double[WindowSize];
        private static int _count;
        private static int _head;

        private static readonly List<CounterProbe> _probes = new List<CounterProbe>();
        private static bool _probesStarted;

        private static int _savedVSync = -999;
        private static int _savedTarget = -999;

        // GC collection-count tracking (build-independent allocation-pressure evidence).
        private static int _gc0Base, _gc1Base;
        private static int _gcWindowFrames;

        internal static bool RecordersInitialized => _probesStarted;

        // ----- per-frame tick -----

        internal static void Tick()
        {
            double ms = Time.unscaledDeltaTime * 1000.0;
            _ring[_head] = ms;
            _head = (_head + 1) % WindowSize;
            if (_count < WindowSize)
            {
                _count++;
            }

            for (int i = 0; i < _probes.Count; i++)
            {
                _probes[i].Sample();
            }
            _gcWindowFrames++;
        }

        // ----- frame stats -----

        internal static FrameStats Snapshot()
        {
            var s = new FrameStats { Samples = _count };
            if (_count == 0)
            {
                return s;
            }

            double[] tmp = new double[_count];
            double sum = 0.0, min = double.MaxValue, max = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double v = _ring[i];
                tmp[i] = v;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double mean = sum / _count;
            double varc = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double d = tmp[i] - mean;
                varc += d * d;
            }
            Array.Sort(tmp);

            s.MeanMs = mean;
            s.MinMs = min;
            s.MaxMs = max;
            s.StdDevMs = Math.Sqrt(varc / _count);
            s.MedianMs = Percentile(tmp, 0.50);
            s.P95Ms = Percentile(tmp, 0.95);
            s.P99Ms = Percentile(tmp, 0.99);
            return s;
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0.0;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        /// <summary>Stddev / mean over the window - used by the sweep's stability gate.</summary>
        internal static double RelativeNoise()
        {
            FrameStats s = Snapshot();
            return s.MeanMs > 0 ? s.StdDevMs / s.MeanMs : 1.0;
        }

        // ----- GC pressure -----

        internal static void ResetGcWindow()
        {
            _gc0Base = GC.CollectionCount(0);
            _gc1Base = SafeCollectionCount(1);
            _gcWindowFrames = 0;
        }

        internal static double Gc0Per1000Frames()
        {
            if (_gcWindowFrames <= 0) return 0.0;
            return (GC.CollectionCount(0) - _gc0Base) * 1000.0 / _gcWindowFrames;
        }

        internal static double Gc1Per1000Frames()
        {
            if (_gcWindowFrames <= 0) return 0.0;
            return (SafeCollectionCount(1) - _gc1Base) * 1000.0 / _gcWindowFrames;
        }

        private static int SafeCollectionCount(int gen)
        {
            try { return GC.CollectionCount(gen); } catch { return 0; }
        }

        // ----- framerate cap control (so frame time reflects true cost) -----

        internal static void UncapFramerate()
        {
            try
            {
                if (_savedVSync == -999)
                {
                    _savedVSync = QualitySettings.vSyncCount;
                    _savedTarget = Application.targetFrameRate;
                }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Perf] Could not uncap framerate: " + e.Message);
            }
        }

        internal static void RestoreFramerate()
        {
            try
            {
                if (_savedVSync != -999)
                {
                    QualitySettings.vSyncCount = _savedVSync;
                    Application.targetFrameRate = _savedTarget;
                    _savedVSync = -999;
                    _savedTarget = -999;
                }
            }
            catch { /* best effort */ }
        }

        // ----- ProfilerRecorder probes (opportunistic, self-certifying) -----

        internal static void StartRecorders()
        {
            if (_probesStarted)
            {
                return;
            }
            _probesStarted = true;

            AddProbe("Main Thread", ProfilerCat.Internal, "Main Thread");
            AddProbe("GC Alloc/Frame", ProfilerCat.Memory, "GC Allocated In Frame");
            AddProbe("GC Reserved", ProfilerCat.Memory, "GC Reserved Memory");
            AddProbe("System Used", ProfilerCat.Memory, "System Used Memory");
            AddProbe("Draw Calls", ProfilerCat.Render, "Draw Calls Count");
            AddProbe("SetPass Calls", ProfilerCat.Render, "SetPass Calls Count");
            AddProbe("Batches", ProfilerCat.Render, "Batches Count");
            AddProbe("Triangles", ProfilerCat.Render, "Triangles Count");
            AddProbe("Vertices", ProfilerCat.Render, "Vertices Count");
            AddProbe("Active Bodies", ProfilerCat.Physics, "Active Dynamic Bodies");
        }

        private static void AddProbe(string label, ProfilerCat cat, string stat)
        {
            try
            {
                _probes.Add(new CounterProbe(label, cat, stat));
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Perf] Probe '{label}' could not start: {e.Message}");
            }
        }

        /// <summary>One-time capability report so the reader knows which counters are real in this build.</summary>
        internal static void LogCapabilityReport()
        {
            Core.Log?.Msg("[Perf] ===== ProfilerRecorder Counter Capability =====");
            if (_probes.Count == 0)
            {
                Core.Log?.Msg("[Perf]   (no probes - ProfilerRecorder unavailable; using frame-time + GC fallback only)");
            }
            foreach (CounterProbe p in _probes)
            {
                Core.Log?.Msg($"[Perf]   {p.Label,-16} : {p.State} (last={p.LastValue})");
            }
            Core.Log?.Msg("[Perf] Frame-time ablation is the primary evidence regardless of the above.");
            Core.Log?.Msg("[Perf] ===============================================");
        }

        internal static IReadOnlyList<CounterProbe> Probes => _probes;

        internal static void Dispose()
        {
            foreach (CounterProbe p in _probes)
            {
                p.Dispose();
            }
            _probes.Clear();
            _probesStarted = false;
            RestoreFramerate();
        }
    }

    internal enum ProfilerCat { Internal, Memory, Render, Physics }

    /// <summary>
    /// Wraps one ProfilerRecorder defensively. If the il2cpp binding fails at any point, the probe
    /// downgrades to Unavailable rather than throwing. Never load-bearing.
    /// </summary>
    internal sealed class CounterProbe
    {
        internal string Label { get; }
        private ProfilerRecorder _rec;
        private bool _created;
        private bool _sawNonZero;
        internal long LastValue { get; private set; }

        internal CounterProbe(string label, ProfilerCat cat, string stat)
        {
            Label = label;
            try
            {
                ProfilerCategory category = cat switch
                {
                    ProfilerCat.Memory => ProfilerCategory.Memory,
                    ProfilerCat.Render => ProfilerCategory.Render,
                    ProfilerCat.Physics => ProfilerCategory.Physics,
                    _ => ProfilerCategory.Internal,
                };
                _rec = ProfilerRecorder.StartNew(category, stat);
                _created = true;
            }
            catch
            {
                _created = false;
            }
        }

        internal void Sample()
        {
            if (!_created)
            {
                return;
            }
            try
            {
                if (_rec.Valid && _rec.Count > 0)
                {
                    LastValue = _rec.LastValue;
                    if (LastValue != 0)
                    {
                        _sawNonZero = true;
                    }
                }
            }
            catch
            {
                _created = false;
            }
        }

        internal CounterState State
        {
            get
            {
                if (!_created)
                {
                    return CounterState.Unavailable;
                }
                bool valid;
                try { valid = _rec.Valid; } catch { return CounterState.Unavailable; }
                if (!valid)
                {
                    return CounterState.Unavailable;
                }
                return _sawNonZero ? CounterState.Measured : CounterState.Suspect;
            }
        }

        internal void Dispose()
        {
            if (!_created)
            {
                return;
            }
            try { _rec.Dispose(); } catch { /* ignore */ }
            _created = false;
        }
    }
}
