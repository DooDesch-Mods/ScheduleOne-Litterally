using UnityEngine;

namespace Trashville.UI
{
    /// <summary>
    /// Release-safe world-space debug overlays drawn in screen space (no GL / no Shader.Find): project world
    /// points with Camera.WorldToScreenPoint and stamp tiny GUI rectangles. Two opt-in views:
    ///   - ShowActiveItems: a coloured dot on every trash item that is currently REAL/interactable - green near the
    ///     player, cyan near cleaners - so you can see exactly what the performance layer is materializing.
    ///   - ShowRanges: a ring on the ground for the adaptive materialize radius (matR) + the max radius (ViewDist).
    /// Only the small materialized sets (a few hundred) and 2 rings are projected - never the full field.
    /// </summary>
    internal static class DebugDraw
    {
        private static readonly int[] _idx = new int[2048];

        internal static void Draw()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            Camera cam = Instanced.Frustum.Cam();
            if (cam == null) return;

            if (Config.Preferences.ShowActiveItems)
            {
                int np = Instanced.Virtualizer.CopyRealIndices(_idx);
                DrawDots(cam, np, new Color(0.25f, 1f, 0.35f, 0.95f));   // player-active = green
                int nc = Spawning.CleanerActor.CopyRealIndices(_idx);
                DrawDots(cam, nc, new Color(0.25f, 0.85f, 1f, 0.95f));   // cleaner-active = cyan
            }

            if (Config.Preferences.ShowRanges && Spawning.GameTrash.TryGetPlayerPosition(out Vector3 pp))
            {
                DrawRing(cam, pp, Instanced.Virtualizer.MatRadius, new Color(1f, 1f, 1f, 0.9f));        // active radius = white
                DrawRing(cam, pp, Instanced.Virtualizer.ViewDist, new Color(1f, 0.55f, 0.1f, 0.7f));    // max radius = orange
            }
        }

        private static void DrawDots(Camera cam, int n, Color col)
        {
            GUI.color = col;
            float h = Screen.height;
            for (int k = 0; k < n; k++)
            {
                Vector3 sp = cam.WorldToScreenPoint(Instanced.InstancedTrash.GetPosition(_idx[k]));
                if (sp.z <= 0f) continue;
                GUI.DrawTexture(new Rect(sp.x - 3f, h - sp.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }

        private static void DrawRing(Camera cam, Vector3 center, float radius, Color col)
        {
            if (radius <= 0f) return;
            GUI.color = col;
            float h = Screen.height;
            const int seg = 56;
            for (int i = 0; i < seg; i++)
            {
                float a = (i / (float)seg) * Mathf.PI * 2f;
                Vector3 wp = new Vector3(center.x + Mathf.Cos(a) * radius, center.y, center.z + Mathf.Sin(a) * radius);
                Vector3 sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0f) continue;
                GUI.DrawTexture(new Rect(sp.x - 3f, h - sp.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }
    }
}
