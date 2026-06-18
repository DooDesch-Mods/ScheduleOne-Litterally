using System;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Instanced
{
    /// <summary>
    /// The 100k path: trash as pure DATA (flat managed arrays), NO GameObjects. Gravity + flat-ground sim in
    /// plain C# (no per-item interop in the hot loop); GPU-instanced rendering via Graphics.DrawMeshInstanced
    /// (matrices marshalled as Il2CppStructArray&lt;Matrix4x4&gt;). To stay 1:1 interactable like base-game
    /// trash, the Virtualizer MATERIALIZES the few instances near the player into real game TrashItems and
    /// HIDES their virtual copy (matrix set to zero = invisible); on demote it Restores the virtual at the
    /// real item's resting pose; on pickup it Kills the virtual permanently.
    /// </summary>
    internal static class InstancedTrash
    {
        private const int BatchSize = 1023;
        private const float Gravity = -22f;

        private static Mesh _mesh;
        private static Material _mat;
        private static string _prefabId;            // id of the trash prefab we lifted the mesh from
        private static Il2CppStructArray<Matrix4x4> _batch;

        internal static bool Shadows = false;

        // Struct-of-arrays state (pure managed - the sim/scan never touch an il2cpp object).
        private static float[] _px, _py, _pz, _vy;
        private static float[] _qx, _qy, _qz, _qw;   // per-item rotation (for materialize + matrix rebuild)
        private static bool[] _settled, _hidden, _dead;
        private static Matrix4x4[] _matrices;        // rotation+scale+xz baked; y updated per frame
        private static float _groundY;
        private static int _count, _active;

        internal static int Count => _count;
        internal static int Active => _active;
        internal static bool Ready => _mesh != null && _mat != null;
        internal static string PrefabId => _prefabId;

        internal static bool Setup(int n, Vector3 center)
        {
            if (!EnsureMeshMaterial())
            {
                Core.Log?.Warning("[inst] could not get a trash mesh/material.");
                return false;
            }
            if (n < 0) n = 0;

            _groundY = center.y;
            // Spread over a large area so density is realistic (~1/m^2 at 100k) - you walk through a littered
            // map, not a solid wall, and only a handful are ever within reach to materialize.
            float radius = Mathf.Clamp((float)Math.Sqrt(n) * 0.5f, 20f, 220f);

            _px = new float[n]; _py = new float[n]; _pz = new float[n]; _vy = new float[n];
            _qx = new float[n]; _qy = new float[n]; _qz = new float[n]; _qw = new float[n];
            _settled = new bool[n]; _hidden = new bool[n]; _dead = new bool[n];
            _matrices = new Matrix4x4[n];

            var rng = new System.Random(12345);
            for (int i = 0; i < n; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                double r = Math.Sqrt(rng.NextDouble()) * radius;
                float x = center.x + (float)(Math.Cos(ang) * r);
                float z = center.z + (float)(Math.Sin(ang) * r);
                float y = center.y + 14f + (float)(rng.NextDouble() * 30.0);

                // uniform random quaternion (pure C#)
                double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();
                double s1 = Math.Sqrt(1.0 - u1), s2 = Math.Sqrt(u1);
                float qx = (float)(s1 * Math.Sin(2.0 * Math.PI * u2));
                float qy = (float)(s1 * Math.Cos(2.0 * Math.PI * u2));
                float qz = (float)(s2 * Math.Sin(2.0 * Math.PI * u3));
                float qw = (float)(s2 * Math.Cos(2.0 * Math.PI * u3));

                _px[i] = x; _py[i] = y; _pz[i] = z; _vy[i] = 0f;
                _qx[i] = qx; _qy[i] = qy; _qz[i] = qz; _qw[i] = qw;
                _matrices[i] = BuildMatrix(qx, qy, qz, qw, x, y, z);
            }
            _count = n;
            _active = n;
            if (_batch == null)
            {
                _batch = new Il2CppStructArray<Matrix4x4>(BatchSize);
            }
            Core.Log?.Msg($"[inst] {n} falling instances, groundY={_groundY:F1} radius={radius:F0} prefab='{_prefabId}'.");
            return true;
        }

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
                if (_settled[i] || _hidden[i] || _dead[i])
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
                _matrices[i].m13 = y;
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
                ShadowCastingMode sc = Shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                int i = 0;
                while (i < _count)
                {
                    int batch = Math.Min(BatchSize, _count - i);
                    for (int j = 0; j < batch; j++)
                    {
                        _batch[j] = _matrices[i + j];
                    }
                    Graphics.DrawMeshInstanced(_mesh, 0, _mat, _batch, batch, null, sc, Shadows, 0);
                    i += batch;
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[inst] render error (disabling): " + e.Message);
                _count = 0;
            }
        }

        // ----- materialization support (used by the Virtualizer) -----

        internal static Vector3 GetPosition(int i) => new Vector3(_px[i], _py[i], _pz[i]);
        internal static Quaternion GetRotation(int i) => new Quaternion(_qx[i], _qy[i], _qz[i], _qw[i]);
        internal static bool IsSettled(int i) => _settled[i];

        /// <summary>Materialized -> hide the virtual copy (zero matrix renders nothing).</summary>
        internal static void Hide(int i)
        {
            _hidden[i] = true;
            _matrices[i] = default;
        }

        /// <summary>Demoted back to virtual at the real item's resting pose (seamless).</summary>
        internal static void Restore(int i, Vector3 pos, Quaternion rot)
        {
            _hidden[i] = false;
            _settled[i] = true;
            _px[i] = pos.x; _py[i] = pos.y; _pz[i] = pos.z;
            _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;
            _matrices[i] = BuildMatrix(rot.x, rot.y, rot.z, rot.w, pos.x, pos.y, pos.z);
        }

        /// <summary>Picked up -> remove the virtual permanently (invisible, never re-materialized).</summary>
        internal static void Kill(int i)
        {
            _dead[i] = true;
            _hidden[i] = true;
            _matrices[i] = default;
        }

        /// <summary>Fill outIdx with up to max SETTLED, non-hidden, non-dead instance indices within radius
        /// (2D distance) of p. Returns the count.</summary>
        internal static int CollectNear(Vector3 p, float radius, int[] outIdx, int max)
        {
            if (_count <= 0 || _px == null) return 0;
            float r2 = radius * radius;
            int n = 0;
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || !_settled[i]) continue;
                float dx = _px[i] - p.x, dz = _pz[i] - p.z;
                if (dx * dx + dz * dz <= r2) outIdx[n++] = i;
            }
            return n;
        }

        private static Matrix4x4 BuildMatrix(float qx, float qy, float qz, float qw, float tx, float ty, float tz)
        {
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
                    _prefabId = ti.ID;
                    string shader = "?";
                    try { shader = _mat.shader != null ? _mat.shader.name : "null"; } catch { }
                    Core.Log?.Msg($"[inst] mesh '{_mesh.name}' verts={_mesh.vertexCount} mat='{mr.sharedMaterial.name}' shader='{shader}' prefab='{_prefabId}'");
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
            _px = _py = _pz = _vy = _qx = _qy = _qz = _qw = null;
            _settled = _hidden = _dead = null;
            _matrices = null;
        }
    }
}
