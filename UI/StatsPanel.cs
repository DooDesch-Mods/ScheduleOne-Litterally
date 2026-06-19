using UnityEngine;

namespace Trashville.UI
{
    /// <summary>
    /// Release-safe live stats panel (opt-in via Preferences.ShowStatsPanel, default OFF). Puts the same numbers
    /// as the periodic [telemetry] log line on screen, so you can watch the performance layer in real time instead
    /// of reading the log. OnGUI, drawn top-left, string rebuilt ~10x/second. Same pattern as UI/FpsCounter.
    /// </summary>
    internal static class StatsPanel
    {
        private static GUIStyle _label;
        private static string _text = "";
        private static float _next;
        private static float _smoothFps;

        internal static void Draw()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            float dt = Time.smoothDeltaTime;
            float fps = dt > 0f ? 1f / dt : 0f;
            _smoothFps = _smoothFps <= 0f ? fps : Mathf.Lerp(_smoothFps, fps, 0.1f);

            float t = Time.unscaledTime;
            if (t >= _next) { _next = t + 0.1f; _text = Build(); }

            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    richText = false,
                };
                _label.normal.textColor = Color.white;
            }

            Rect box = new Rect(10f, 70f, 232f, 174f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 9f, box.y + 7f, box.width - 14f, box.height - 12f), _text, _label);
        }

        private static string Build()
        {
            int live = Instanced.InstancedTrash.LiveCount;
            int total = Instanced.InstancedTrash.Count;
            int drawn = Instanced.InstancedTrash.Drawn;
            int rp = Instanced.Virtualizer.RealCount;
            int rc = Spawning.CleanerActor.RealCount;
            float mr = Instanced.Virtualizer.MatRadius;
            return "Trashville\n" +
                   $"field-live   {live} / {total}\n" +
                   $"drawn        {drawn}\n" +
                   $"real player  {rp}\n" +
                   $"real cleaner {rc}\n" +
                   $"awake        {Instanced.Virtualizer.AwakeRealCount()}\n" +
                   $"matR         {mr:F1} m\n" +
                   $"absorbed     {Spawning.RouteHook.Absorbed}\n" +
                   $"fps          {Mathf.RoundToInt(_smoothFps)}";
        }
    }
}
