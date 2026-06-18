using UnityEngine;

namespace Trashville.Instanced
{
    /// <summary>
    /// Camera view-frustum maths, shared by the Virtualizer (which items to materialize) and the instanced
    /// renderer (which items to actually draw). Planes are stored as 6*(nx,ny,nz,d) in a flat float[24], all in
    /// WORLD space and normalized so a margin is expressed in metres. The per-point test is pure managed code
    /// (six multiply-adds) so it can run over 100k items per frame with no interop.
    /// </summary>
    internal static class Frustum
    {
        /// <summary>Extract the 6 world-space frustum planes from a camera (Gribb-Hartmann from the
        /// view-projection matrix). Returns false (and leaves planes untouched) if there is no camera.</summary>
        internal static bool Compute(Camera cam, float[] planes)
        {
            if (cam == null || planes == null || planes.Length < 24) return false;
            try
            {
                Matrix4x4 m = cam.projectionMatrix * cam.worldToCameraMatrix;
                Set(planes, 0, m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03); // left
                Set(planes, 1, m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03); // right
                Set(planes, 2, m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13); // bottom
                Set(planes, 3, m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13); // top
                Set(planes, 4, m.m30 + m.m20, m.m31 + m.m21, m.m32 + m.m22, m.m33 + m.m23); // near
                Set(planes, 5, m.m30 - m.m20, m.m31 - m.m21, m.m32 - m.m22, m.m33 - m.m23); // far
                return true;
            }
            catch { return false; }
        }

        private static void Set(float[] pl, int k, float a, float b, float c, float d)
        {
            float len = Mathf.Sqrt(a * a + b * b + c * c);
            float inv = len > 1e-6f ? 1f / len : 0f;
            int o = k << 2;
            pl[o] = a * inv; pl[o + 1] = b * inv; pl[o + 2] = c * inv; pl[o + 3] = d * inv;
        }

        /// <summary>True if the point is inside the frustum, expanded by margin metres. planes==null =&gt; always true.</summary>
        internal static bool Contains(float[] pl, float px, float py, float pz, float margin)
        {
            if (pl == null) return true;
            for (int k = 0; k < 6; k++)
            {
                int b = k << 2;
                if (pl[b] * px + pl[b + 1] * py + pl[b + 2] * pz + pl[b + 3] < -margin) return false;
            }
            return true;
        }
    }
}
