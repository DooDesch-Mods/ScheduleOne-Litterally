using System.Text;
using UnityEngine;
using Trashville.Profiling;
using Trashville.Spawning;

namespace Trashville.UI
{
    /// <summary>
    /// IMGUI on-screen readout (the always-available live evidence display). Drawn from Core.OnGUI.
    /// String content is rebuilt at ~4 Hz to keep the harness's own cost negligible.
    /// </summary>
    internal static class DebugHud
    {
        private static string _cached = "";
        private static float _nextRebuild;
        private static GUIStyle _box;
        private static GUIStyle _banner;

        internal static void Draw()
        {
            if (!Config.Preferences.ShowHud)
            {
                return;
            }

            EnsureStyles();

            if (Time.unscaledTime >= _nextRebuild)
            {
                _nextRebuild = Time.unscaledTime + 0.25f;
                _cached = Build();
            }

            GUI.Box(new Rect(8, 8, 360, 320), _cached, _box);

            if (Config.Preferences.ArmBenchmark)
            {
                GUI.Box(new Rect(8, 332, 360, 24), "BENCHMARK ARMED - trash auto-clears on save", _banner);
            }
        }

        private static string Build()
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("<b>Trashville - Trash Benchmark</b>");

            FrameStats s = PerfSampler.Snapshot();
            sb.AppendLine($"FPS mean {s.MeanFps:F0}  min {s.MinFps:F0}");
            sb.AppendLine($"frame ms: mean {s.MeanMs:F2}  median {s.MedianMs:F2}");
            sb.AppendLine($"          p95 {s.P95Ms:F2}  p99 {s.P99Ms:F2}  sd {s.StdDevMs:F2}");

            var tm = TrashSpawner.TrashManagerOrNull();
            int mgr = tm != null ? TrashSpawner.TrashItemCount(tm) : 0;
            if (Config.Preferences.BypassCap)
            {
                sb.AppendLine($"clones {CloneRegistry.Count}  awake {CloneRegistry.CountAwake()}  manager {mgr}  pending {TrashSpawner.Pending}");
            }
            else
            {
                sb.AppendLine($"trash: ours {TrashRegistry.Count}  manager {mgr}  pending {TrashSpawner.Pending}");
            }
            sb.AppendLine($"GC/1000f: gen0 {PerfSampler.Gc0Per1000Frames():F1}  gen1 {PerfSampler.Gc1Per1000Frames():F1}");

            sb.Append("armed: ").Append(Config.Preferences.ArmBenchmark ? "YES" : "no");
            sb.Append("   mode: ").Append(Config.Preferences.SpawnKinematic ? "KINEMATIC" : "DYNAMIC");
            sb.Append("   bypass: ").Append(Config.Preferences.BypassCap ? "ON" : "off");
            if (AblationController.Active)
            {
                sb.Append("   sweep: ").Append(AblationController.Status);
            }
            if (PhysicsProbe.Active)
            {
                sb.Append("   physAB: ").Append(PhysicsProbe.Status);
            }
            sb.AppendLine();

            int measured = 0;
            foreach (CounterProbe p in PerfSampler.Probes)
            {
                if (p.State == CounterState.Measured)
                {
                    if (measured == 0) sb.AppendLine("<b>counters (MEASURED):</b>");
                    sb.AppendLine($"  {p.Label}: {p.LastValue}");
                    measured++;
                    if (measured >= 4) break;
                }
            }
            if (measured == 0)
            {
                sb.AppendLine("counters: none MEASURED (release build) - using frame ms");
            }

            sb.AppendLine("<b>keys</b> F5 arm  F1 bypass-cap  F4 kin/dyn  F6 hud  F7/8/9 spawn 100/1k/10k");
            sb.AppendLine("     F10 clear  Shift+F10 purge  F11 sweep  F3 physics A/B");
            return sb.ToString();
        }

        private static void EnsureStyles()
        {
            if (_box != null)
            {
                return;
            }
            _box = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                richText = true,
                fontSize = 12,
                wordWrap = true,
            };
            _box.normal.textColor = Color.white;
            _box.padding = new RectOffset(8, 8, 6, 6);

            _banner = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
            };
            _banner.normal.textColor = new Color(1f, 0.55f, 0.55f);
        }
    }
}
