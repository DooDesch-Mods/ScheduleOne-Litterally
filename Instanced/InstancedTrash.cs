using System;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Instanced
{
    /// <summary>
    /// The 100k path: trash as pure DATA (flat float arrays), NO GameObjects. Simple gravity + flat-ground
    /// physics runs in plain managed C# (no per-item interop in the hot loop); rendering is GPU-instanced via
    /// Graphics.DrawMeshInstanced (mesh+material lifted from a trash prefab; matrices marshalled as
    /// Il2CppStructArray&lt;Matrix4x4&gt;). The per-item rotation is baked into the matrix once at spawn;
    /// each frame only the translation column (m03/m13/m23) is updated for still-falling items.
    /// </summary>
    internal static class InstancedTrash
    {
        private const int BatchSize = 1023;
        private const float Gravity = -22f;   // a touch stronger than 9.81 so the rain settles faster

        private static Mesh _mesh;
        private static Material _mat;
        private static Il2CppStructArray<Matrix4x4> _batch;

        /// <summary>Shadow casting roughly DOUBLES the render cost (the shadow pass re-renders all 100k).
        /// Off by default for the 100k path; toggle with `tv shadows on`.</summary>
        internal static bool Shadows = false;

        // Struct-of-arrays state (pure managed floats - the sim never touches an il2cpp object).
        private static float[] _py, _vy;
        private static bool[] _settled;
        private static Matrix4x4[] _matrices;   // blittable struct; rotation+scale+xz baked, y updated per frame
        private static float _groundY;
        private static int _count, _active;

        internal static int Count => _count;
        internal static int Active => _active;
        internal static bool Ready => _mesh != null && _mat != null;

        internal static bool Setup(int n, Vector3 center)
        {
            if (!EnsureMeshMaterial())
            {
                Core.Log?.Warning("[inst] could not get a trash mesh/material.");
                return false;
            }
            if (n < 0) n = 0;

            _groundY = center.y;
            float radius = Mathf.Clamp((float)Math.Sqrt(n) * 0.22f, 12f, 90f);   // spread wider for bigger N

            _py = new float[n];
            _vy = new float[n];
            _settled = new bool[n];
            _matrices = new Matrix4x4[n];

            var rng = new System.Random(12345);
            for (int i = 0; i < n; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                double r = Math.Sqrt(rng.NextDouble()) * radius;
                float x = center.x + (float)(Math.Cos(ang) * r);
                float z = center.z + (float)(Math.Sin(ang) * r);
                float y = center.y + 18f + (float)(rng.NextDouble() * 40.0);   // rain column
                _py[i] = y;
                _vy[i] = 0f;
                _settled[i] = false;
                _matrices[i] = BuildMatrix(rng, x, y, z);   // pure C#, no interop
            }
            _count = n;
            _active = n;
            if (_batch == null)
            {
                _batch = new Il2CppStructArray<Matrix4x4>(BatchSize);
            }
            Core.Log?.Msg($"[inst] {n} falling instances, groundY={_groundY:F1} radius={radius:F0}.");
            return true;
        }

        /// <summary>Pure-managed gravity sim for still-falling items; updates each matrix's Y translation.</summary>
        internal static void Tick(float dt)
        {
            if (_active <= 0 || _count <= 0 || dt <= 0f)
            {
                return;
            }
            float g = Gravity * dt;
            int active = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_settled[i])
                {
                    continue;
                }
                _vy[i] += g;
                float y = _py[i] + _vy[i] * dt;
                if (y <= _groundY)
                {
                    y = _groundY;
                    _settled[i] = true;
                }
                else
                {
                    active++;
                }
                _py[i] = y;
                _matrices[i].m13 = y;   // translation Y column; x/z were baked at spawn (no horizontal motion)
            }
            _active = active;
        }

        internal static void Render()
        {
            if (_count <= 0 || _mesh == null || _mat == null || _batch == null)
            {
                return;
            }
            try
            {
                int i = 0;
                while (i < _count)
                {
                    int batch = Math.Min(BatchSize, _count - i);
                    for (int j = 0; j < batch; j++)
                    {
                        _batch[j] = _matrices[i + j];
                    }
                    Graphics.DrawMeshInstanced(_mesh, 0, _mat, _batch, batch, null,
                        Shadows ? ShadowCastingMode.On : ShadowCastingMode.Off, Shadows, 0);
                    i += batch;
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[inst] render error (disabling): " + e.Message);
                _count = 0;
            }
        }

        /// <summary>Build a TRS matrix in pure C# (random uniform rotation, unit scale, translation) so spawning
        /// 100k items does not make 100k native Matrix4x4.TRS / Random.rotation interop calls.</summary>
        private static Matrix4x4 BuildMatrix(System.Random rng, float tx, float ty, float tz)
        {
            // uniform random quaternion
            double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();
            double s1 = Math.Sqrt(1.0 - u1), s2 = Math.Sqrt(u1);
            float qx = (float)(s1 * Math.Sin(2.0 * Math.PI * u2));
            float qy = (float)(s1 * Math.Cos(2.0 * Math.PI * u2));
            float qz = (float)(s2 * Math.Sin(2.0 * Math.PI * u3));
            float qw = (float)(s2 * Math.Cos(2.0 * Math.PI * u3));

            float xx = qx * qx, yy = qy * qy, zz = qz * qz;
            float xy = qx * qy, xz = qx * qz, yz = qy * qz;
            float wx = qw * qx, wy = qw * qy, wz = qw * qz;

            Matrix4x4 m = default;
            m.m00 = 1f - 2f * (yy + zz); m.m01 = 2f * (xy - wz);      m.m02 = 2f * (xz + wy);      m.m03 = tx;
            m.m10 = 2f * (xy + wz);      m.m11 = 1f - 2f * (xx + zz); m.m12 = 2f * (yz - wx);      m.m13 = ty;
            m.m20 = 2f * (xz - wy);      m.m21 = 2f * (yz + wx);      m.m22 = 1f - 2f * (xx + yy); m.m23 = tz;
            m.m33 = 1f;
            return m;
        }

        private static bool EnsureMeshMaterial()
        {
            if (Ready)
            {
                return true;
            }
            try
            {
                var tm = Spawning.TrashSpawner.TrashManagerOrNull();
                if (tm == null)
                {
                    return false;
                }
                var prefabs = tm.TrashPrefabs;
                if (prefabs == null)
                {
                    return false;
                }
                for (int i = 0; i < prefabs.Length; i++)
                {
                    TrashItem ti = prefabs[i];
                    if (ti == null) continue;
                    GameObject go = ti.gameObject;
                    Il2CppArrayBase<MeshFilter> mfs = go.GetComponentsInChildren<MeshFilter>(true);
                    MeshRenderer mr = go.GetComponentInChildren<MeshRenderer>(true);
                    if (mfs == null || mfs.Length == 0 || mr == null || mr.sharedMaterial == null)
                    {
                        continue;
                    }
                    // Pick the LOWEST-vertex mesh (lowest LOD) - cheapest to render 100k of.
                    Mesh best = null;
                    int bestV = int.MaxValue;
                    for (int j = 0; j < mfs.Length; j++)
                    {
                        Mesh m = mfs[j] != null ? mfs[j].sharedMesh : null;
                        if (m != null && m.vertexCount < bestV) { best = m; bestV = m.vertexCount; }
                    }
                    if (best == null) continue;

                    _mesh = best;
                    _mat = new Material(mr.sharedMaterial);
                    _mat.enableInstancing = true;
                    string shader = "?";
                    try { shader = _mat.shader != null ? _mat.shader.name : "null"; } catch { }
                    Core.Log?.Msg($"[inst] mesh '{_mesh.name}' verts={_mesh.vertexCount} (lowest of {mfs.Length} LODs) mat='{mr.sharedMaterial.name}' shader='{shader}'");
                    return true;
                }
                Core.Log?.Warning("[inst] no prefab yielded a MeshFilter+MeshRenderer.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[inst] EnsureMeshMaterial error: " + e.Message);
            }
            return Ready;
        }

        internal static void Clear()
        {
            _count = 0;
            _active = 0;
            _py = null;
            _vy = null;
            _settled = null;
            _matrices = null;
        }
    }
}
