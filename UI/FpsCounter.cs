using UnityEngine;

namespace Litterally.UI
{
    /// <summary>
    /// Minimal, release-safe on-screen FPS readout (top-right). Opt-in via Preferences.ShowFpsCounter (default
    /// OFF), drawn from Core.OnGUI when enabled. Uses Unity's smoothed delta so the number doesn't jitter.
    /// </summary>
    internal static class FpsCounter
    {
        private static GUIStyle _style;

        internal static void Draw()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            float dt = Time.smoothDeltaTime;
            int fps = dt > 0f ? Mathf.RoundToInt(1f / dt) : 0;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperRight,
                };
            }
            _style.normal.textColor = fps >= 50 ? Color.green : (fps >= 30 ? Color.yellow : new Color(1f, 0.45f, 0.45f));

            string text = fps + " FPS";
            Rect r = new Rect(Screen.width - 140f, 6f, 132f, 28f);
            // cheap drop-shadow for readability over any background
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _style);
            GUI.color = Color.white;
            GUI.Label(r, text, _style);
        }
    }
}
