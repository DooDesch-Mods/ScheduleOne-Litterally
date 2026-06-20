using UnityEngine;

namespace Litterally.Instanced
{
    /// <summary>
    /// Camera view-frustum maths, shared by the Virtualizer (which items to materialize) and the instanced
    /// renderer (which items to actually draw). Planes are stored as 6*(nx,ny,nz,d) in a flat float[24], all in
    /// WORLD space and normalized so a margin is expressed in metres. The per-point test is pure managed code
    /// (six multiply-adds) so it can run over 100k items per frame with no interop.
    /// </summary>
    internal static class Frustum
    {
        private static Camera _cam;

        /// <summary>Camera.main, but cached so a TRANSIENT null (e.g. a frame during scene work) returns the last
        /// valid camera instead of null - a null frustum would otherwise bypass culling and draw/materialize the
        /// whole 100k field for that frame (a hitch spike).</summary>
        internal static Camera Cam()
        {
            Camera c = Camera.main;
            if (c != null) _cam = c;
            return _cam;
        }

        /// <summary>Extract the 6 world-space frustum planes from a camera (Gribb-Hartmann from the
        /// view-projection matrix). Returns false (and leaves planes untouched) if there is no camera.</summary>
        internal static bool Compute(Camera cam, float[] planes)
        {
            if (cam == null || planes == null || planes.Length < 24) return false;
            try { return ComputeFromVP(cam.projectionMatrix * cam.worldToCameraMatrix, planes); }
            catch { return false; }
        }

        /// <summary>Extract the 6 world-space frustum planes from an arbitrary view-projection matrix - used to
        /// build a PREDICTED frustum from an extrapolated camera orientation.</summary>
        internal static bool ComputeFromVP(Matrix4x4 m, float[] planes)
        {
            if (planes == null || planes.Length < 24) return false;
            Set(planes, 0, m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03); // left
            Set(planes, 1, m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03); // right
            Set(planes, 2, m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13); // bottom
            Set(planes, 3, m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13); // top
            Set(planes, 4, m.m30 + m.m20, m.m31 + m.m21, m.m32 + m.m22, m.m33 + m.m23); // near
            Set(planes, 5, m.m30 - m.m20, m.m31 - m.m21, m.m32 - m.m22, m.m33 - m.m23); // far
            return true;
        }

        /// <summary>World view-projection for a camera at pos+rot (Unity convention: looks down -Z) with the given
        /// projection. Lets us build a predicted frustum without touching the live camera.</summary>
        internal static Matrix4x4 ViewProjection(Matrix4x4 projection, Vector3 pos, Quaternion rot)
        {
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
            return projection * view;
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
