using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Instanced
{
    /// <summary>
    /// The 100k path: trash as pure DATA (flat managed arrays), NO GameObjects. Gravity sim + per-item
    /// terrain ground in plain C# (no per-item interop in the hot loop); GPU-instanced rendering via
    /// Graphics.DrawMeshInstanced, one batch set per trash MESH TYPE (mixed believable litter field).
    ///
    /// Three things make it look + behave 1:1 like base-game trash while staying cheap:
    ///  - TERRAIN: each item's ground height comes from NavMesh.SamplePosition (walkable surface only, so
    ///    never floats in tree canopies / on roofs); the slope normal tilts the resting pose.
    ///  - SEAM: the resting orientation per type is CALIBRATED empirically - we drop one real probe per type,
    ///    let physics settle it, and capture its real resting rotation + ground clearance; virtual settled
    ///    items then use that exact pose, so a materialized real item is indistinguishable from its neighbours.
    ///  - INTERACTION: the Virtualizer materializes the few instances near the player into real TrashItems and
    ///    hides their virtual copy; on demote it Restores the virtual at the real resting pose; on pickup it
    ///    Kills the virtual permanently.
    /// </summary>
    internal static class InstancedTrash
    {
        private const int BatchSize = 1023;
        private const float Gravity = -22f;
        private const float GroundCellSize = 5f;     // navmesh sample cache cell (m) - ground varies slowly; bigger = fewer one-time samples (less spawn hitch)
        private const float NavSampleMaxDist = 25f;  // how far up/down NavMesh.SamplePosition searches

        internal static bool Shadows = false;
        internal static int MaxTypes = 8;            // runtime-switchable (tv maxtypes N): 1 = single type, 8 = variety

        // ----- type palette (one render bucket per mesh) -----
        private sealed class TType
        {
            public string Id;
            public Mesh Mesh;
            public Material Mat;
            public Matrix4x4 MeshLocal = Matrix4x4.identity; // mesh transform relative to prefab root (bakes prefab scale/offset)
            public Quaternion RestRot = Quaternion.identity; // calibrated resting root rotation on flat ground
            public float Clearance = 0f;                     // root.y - groundY when resting
            public bool Calibrated;
            public float Weight = 1f;                         // relative share of instances
            public int Start, Len;                            // contiguous instance range [Start, Start+Len)
        }

        // Curated common litter (id, weight). soilbag is the known-good mesh; the rest add variety.
        private static readonly (string id, float w)[] Palette =
        {
            ("trashbag", 3f), ("glassbottle", 2f), ("waterbottle", 2f), ("coffeecup", 1.8f),
            ("energydrink", 1.5f), ("cigarettebox", 1.4f), ("litter1", 1.2f), ("soilbag", 1f),
        };

        private static TType[] _types;
        private static Il2CppStructArray<Matrix4x4> _batch;
        private static readonly float[] _renderPlanes = new float[24];
        private const float RenderCullMargin = 6f;   // expand the cull frustum so big/edge items never pop at the screen edge (also covers fast pans)
        private static int _lastDrawn;               // how many instances were actually drawn last frame (after culling)

        // ----- struct-of-arrays instance state (pure managed - the sim/scan never touch an il2cpp object) -----
        private static float[] _px, _pz, _py, _vy, _restY, _baseColY;
        private static float[] _qx, _qy, _qz, _qw;   // per-item ROOT rotation (for materialize)
        private static byte[] _type;                 // index into _types
        private static bool[] _settled, _hidden, _dead;
        private static Matrix4x4[] _matrices;        // full resting matrix; only m13 animates during the fall
        private static int _count, _active;

        // ----- navmesh ground cache -----
        private struct Ground { public float Y; public Vector3 N; public bool Hit; }
        private static readonly Dictionary<long, Ground> _groundCache = new Dictionary<long, Ground>(8192);

        // ----- deferred build (we may need to calibrate first) -----
        private static bool _pending;
        private static int _pendingN;
        private static Vector3 _pendingCenter;

        internal static int Count => _count;
        internal static int Active => _active;
        internal static int Drawn => _lastDrawn;     // instances actually drawn last frame (after frustum culling)
        internal static int TypeCount => _types != null ? _types.Length : 0;
        internal static bool Ready => _types != null && _types.Length > 0;

        // Save-safety: set the moment the instanced path creates ANY real TrashItem (calibration probe OR a
        // Virtualizer-materialized item). The save guard ORs this in so its DestroyAllTrash sweep is never
        // skipped while a mod-created real item could be live. See SaveSafety.ForceClearForSave.
        internal static bool EverMaterialized { get; private set; }
        internal static void MarkRealCreated() => EverMaterialized = true;

        /// <summary>Destroy any in-flight calibration probes and cancel a pending build, WITHOUT touching the
        /// virtual field. Safe to call from every save/teardown path (probes are real Saveable TrashItems).</summary>
        internal static void AbortCalibration()
        {
            Calibration.Reset();
            _pending = false;
        }

        // =========================================================================================
        //  Public entry: request a spawn. Builds the palette, calibrates resting poses if needed,
        //  then builds the field (deferred a few frames while probes settle).
        // =========================================================================================
        internal static bool Setup(int n, Vector3 center)
        {
            if (n < 0) n = 0;

            // Respawn safety: tear down the Virtualizer (empties _real, demotes+destroys its real items) and
            // halt the OLD field BEFORE we reallocate, so no stale _real index can outlive the array it points
            // into. Count=0 also makes Virtualizer.Tick no-op during the calibration window (no materialize of
            // the dying field).
            Virtualizer.ClearAll();
            _count = 0;
            _active = 0;

            if (!EnsurePalette())
            {
                Core.Log?.Warning("[inst] could not build a trash palette (no prefab meshes).");
                return false;
            }
            _pending = true;
            _pendingN = n;
            _pendingCenter = center;

            if (!Calibration.Begin(_types))
            {
                // nothing to calibrate (already done) -> build immediately
                BuildPending();
            }
            else
            {
                Core.Log?.Msg($"[inst] calibrating resting pose for {Calibration.Outstanding} type(s) before spawning {n}...");
            }
            return true;
        }

        internal static void Tick(float dt)
        {
            // 1) drive calibration; when it finishes, build the pending field.
            if (Calibration.Active)
            {
                if (Calibration.Tick())
                {
                    BuildPending();
                }
                return; // don't run the sim until the field exists
            }

            // 2) gravity + terrain-ground sim (only Y animates; rotation is fixed at the resting pose).
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
                if (y <= _restY[i])
                {
                    y = _restY[i];
                    _settled[i] = true;
                }
                else
                {
                    active++;
                }
                _py[i] = y;
                _matrices[i].m13 = _baseColY[i] + (y - _restY[i]); // fall offset adds directly to the world Y column
            }
            _active = active;
        }

        internal static void Render()
        {
            if (_count <= 0 || _types == null || _batch == null)
            {
                return;
            }
            try
            {
                ShadowCastingMode sc = Shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                // Unity does NOT frustum-cull DrawMeshInstanced instances - so we do it ourselves. Only instances
                // inside the view frustum are copied into the batch and drawn; at 100k most of the field is
                // off-screen every frame, so this is by far the biggest render win. Also skips hidden (materialized)
                // and dead (picked-up) instances entirely instead of drawing zeroed matrices.
                float[] planes = Frustum.Compute(Frustum.Cam(), _renderPlanes) ? _renderPlanes : null;
                int drawn = 0;
                for (int t = 0; t < _types.Length; t++)
                {
                    TType ty = _types[t];
                    if (ty.Mesh == null || ty.Mat == null || ty.Len <= 0)
                    {
                        continue;
                    }
                    int end = ty.Start + ty.Len;
                    int count = 0;
                    for (int i = ty.Start; i < end; i++)
                    {
                        if (_hidden[i] || _dead[i]) continue;
                        if (planes != null && !Frustum.Contains(planes, _px[i], _py[i], _pz[i], RenderCullMargin)) continue;
                        _batch[count++] = _matrices[i];
                        if (count == BatchSize)
                        {
                            Graphics.DrawMeshInstanced(ty.Mesh, 0, ty.Mat, _batch, count, null, sc, Shadows, 0);
                            drawn += count;
                            count = 0;
                        }
                    }
                    if (count > 0)
                    {
                        Graphics.DrawMeshInstanced(ty.Mesh, 0, ty.Mat, _batch, count, null, sc, Shadows, 0);
                        drawn += count;
                    }
                }
                _lastDrawn = drawn;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[inst] render error (disabling): " + e.Message);
                _count = 0;
            }
        }

        // =========================================================================================
        //  Field build (runs after calibration). Assigns types in contiguous blocks, samples terrain
        //  ground per item, and bakes the resting matrix; items start above the ground and fall in.
        // =========================================================================================
        private static void BuildPending()
        {
            if (!_pending)
            {
                return;
            }
            _pending = false;
            int n = _pendingN;
            Vector3 center = _pendingCenter;

            // Spread over a large area so density is realistic (~1/m^2 at 100k) - you walk through a littered
            // map, not a wall, and only a handful are ever within reach to materialize.
            float radius = Mathf.Clamp((float)Math.Sqrt(n) * 0.5f, 20f, 220f);

            _px = new float[n]; _pz = new float[n]; _py = new float[n]; _vy = new float[n];
            _restY = new float[n]; _baseColY = new float[n];
            _qx = new float[n]; _qy = new float[n]; _qz = new float[n]; _qw = new float[n];
            _type = new byte[n];
            _settled = new bool[n]; _hidden = new bool[n]; _dead = new bool[n];
            _matrices = new Matrix4x4[n];

            AssignTypeRanges(n);

            var rng = new System.Random(12345);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int navHits = 0;
            for (int t = 0; t < _types.Length; t++)
            {
                TType ty = _types[t];
                for (int k = 0; k < ty.Len; k++)
                {
                    int i = ty.Start + k;
                    _type[i] = (byte)t;

                    double ang = rng.NextDouble() * Math.PI * 2.0;
                    double r = Math.Sqrt(rng.NextDouble()) * radius;
                    float x = center.x + (float)(Math.Cos(ang) * r);
                    float z = center.z + (float)(Math.Sin(ang) * r);

                    Ground gnd = SampleGround(x, z, center.y);
                    if (gnd.Hit) navHits++;
                    float restY = gnd.Y + ty.Clearance;

                    // resting root rotation: slope-align, spin a random yaw, then the type's calibrated rest pose.
                    float yaw = (float)(rng.NextDouble() * 360.0);
                    Quaternion slope = Quaternion.FromToRotation(Vector3.up, gnd.N);
                    Quaternion rot = slope * Quaternion.AngleAxis(yaw, Vector3.up) * ty.RestRot;

                    float fallH = 8f + (float)(rng.NextDouble() * 26.0);

                    _px[i] = x; _pz[i] = z; _restY[i] = restY; _py[i] = restY + fallH; _vy[i] = 0f;
                    _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;

                    Matrix4x4 m = BuildInstanceMatrix(x, restY, z, rot.x, rot.y, rot.z, rot.w, ty.MeshLocal);
                    _baseColY[i] = m.m13;            // world Y of the resting translation column
                    m.m13 = _baseColY[i] + fallH;    // start up in the air
                    _matrices[i] = m;
                }
            }
            sw.Stop();

            _count = n;
            _active = n;
            if (_batch == null)
            {
                _batch = new Il2CppStructArray<Matrix4x4>(BatchSize);
            }
            Core.Log?.Msg($"[inst] {n} falling instances across {_types.Length} type(s), radius={radius:F0}, " +
                          $"ground: {navHits}/{n} navmesh hits, sampled in {sw.ElapsedMilliseconds}ms (cache={_groundCache.Count}).");
        }

        private static void AssignTypeRanges(int n)
        {
            float wsum = 0f;
            for (int t = 0; t < _types.Length; t++) wsum += _types[t].Weight;
            int acc = 0;
            for (int t = 0; t < _types.Length; t++)
            {
                int len = (t == _types.Length - 1) ? (n - acc) : Mathf.RoundToInt(n * (_types[t].Weight / wsum));
                if (len < 0) len = 0;
                if (acc + len > n) len = n - acc;
                _types[t].Start = acc;
                _types[t].Len = len;
                acc += len;
            }
        }

        // ----- navmesh ground (cached by cell; tree/roof-proof) -----
        private static Ground SampleGround(float x, float z, float fallbackY)
        {
            long key = ((long)Mathf.FloorToInt(x / GroundCellSize) << 32) ^ (uint)Mathf.FloorToInt(z / GroundCellSize);
            if (_groundCache.TryGetValue(key, out Ground g))
            {
                return g;
            }
            g.Y = fallbackY; g.N = Vector3.up; g.Hit = false;
            try
            {
                Vector3 src = new Vector3(x, fallbackY + 3f, z);
                if (NavMesh.SamplePosition(src, out NavMeshHit hit, NavSampleMaxDist, -1))
                {
                    Vector3 p = hit.position;
                    // reject absurd results (NaN / wildly off) just in case
                    if (!float.IsNaN(p.y) && Mathf.Abs(p.y - fallbackY) < 200f)
                    {
                        g.Y = p.y;
                        Vector3 nrm = hit.normal;
                        if (nrm.sqrMagnitude > 0.01f && nrm.y > 0.3f) g.N = nrm.normalized;
                        g.Hit = true;
                    }
                }
            }
            catch { /* navmesh not ready -> flat fallback */ }
            _groundCache[key] = g;
            return g;
        }

        // =========================================================================================
        //  Materialization support (used by the Virtualizer)
        // =========================================================================================
        private static bool InRange(int i) => _px != null && i >= 0 && i < _count;

        internal static Vector3 GetPosition(int i) => InRange(i) ? new Vector3(_px[i], _py[i], _pz[i]) : Vector3.zero;
        internal static Quaternion GetRotation(int i) => InRange(i) ? new Quaternion(_qx[i], _qy[i], _qz[i], _qw[i]) : Quaternion.identity;
        internal static string GetTypeId(int i) => (InRange(i) && _types != null) ? _types[_type[i]].Id : null;
        internal static float GetClearance(int i) => (InRange(i) && _types != null) ? _types[_type[i]].Clearance : 0f;
        internal static bool IsSettled(int i) => InRange(i) && _settled[i];

        /// <summary>Materialized -> hide the virtual copy (zero matrix renders nothing).</summary>
        internal static void Hide(int i)
        {
            if (!InRange(i)) return;
            _hidden[i] = true;
            _matrices[i] = default;
        }

        /// <summary>Demoted back to virtual at the real item's resting pose (seamless).</summary>
        internal static void Restore(int i, Vector3 pos, Quaternion rot)
        {
            if (!InRange(i)) return;
            _hidden[i] = false;
            _settled[i] = true;
            _px[i] = pos.x; _py[i] = pos.y; _pz[i] = pos.z; _restY[i] = pos.y; _vy[i] = 0f;
            _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;
            Matrix4x4 m = BuildInstanceMatrix(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, _types[_type[i]].MeshLocal);
            _baseColY[i] = m.m13;
            _matrices[i] = m;
        }

        /// <summary>Picked up -> remove the virtual permanently (invisible, never re-materialized).</summary>
        internal static void Kill(int i)
        {
            if (!InRange(i)) return;
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

        // =========================================================================================
        //  Matrix helpers (hand-rolled, pure managed - avoids 100k native TRS/multiply interop calls)
        // =========================================================================================

        /// <summary>world = TRS(pos, rot, 1) * meshLocal, built without any il2cpp interop.</summary>
        /// <summary>Fill outIdx with SETTLED instance indices to materialize, NEAREST/critical first. Pass 1 is
        /// the anti-glitch ring around the PREDICTED player position backCenter (must be real before the player
        /// reaches it). Pass 2 is anything inside the current OR predicted (extrapolated-turn) frustum within
        /// viewDist of p. The 2D pre-filter keeps the frustum tests to the few thousand near instances.</summary>
        internal static int CollectVisible(float[] cur, float[] pred, Vector3 backCenter, Vector3 p,
            float backRadius, float viewDist, float margin, int[] outIdx, int max)
        {
            if (_count <= 0 || _px == null) return 0;
            float br2 = backRadius * backRadius;
            float vd2 = viewDist * viewDist;
            int n = 0;
            // Pass 1: anti-glitch ring (predicted player pos) - PRIORITY, at the front so the per-frame budget
            // fills it first; collision must exist before the player walks/backs into the item.
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || !_settled[i]) continue;
                float dx = _px[i] - backCenter.x, dz = _pz[i] - backCenter.z;
                if (dx * dx + dz * dz <= br2) outIdx[n++] = i;
            }
            // Pass 2: inside the current OR predicted frustum, within viewDist of the player.
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || !_settled[i]) continue;
                float bx = _px[i] - backCenter.x, bz = _pz[i] - backCenter.z;
                if (bx * bx + bz * bz <= br2) continue;   // already taken in pass 1
                float dx = _px[i] - p.x, dz = _pz[i] - p.z;
                if (dx * dx + dz * dz > vd2) continue;
                if (Frustum.Contains(cur, _px[i], _py[i], _pz[i], margin) ||
                    Frustum.Contains(pred, _px[i], _py[i], _pz[i], margin)) outIdx[n++] = i;
            }
            return n;
        }

        private static Matrix4x4 BuildInstanceMatrix(float tx, float ty, float tz,
            float qx, float qy, float qz, float qw, Matrix4x4 meshLocal)
        {
            // rotation+translation matrix from the quaternion
            float xx = qx * qx, yy = qy * qy, zz = qz * qz;
            float xy = qx * qy, xz = qx * qz, yz = qy * qz;
            float wx = qw * qx, wy = qw * qy, wz = qw * qz;

            float r00 = 1f - 2f * (yy + zz), r01 = 2f * (xy - wz), r02 = 2f * (xz + wy);
            float r10 = 2f * (xy + wz), r11 = 1f - 2f * (xx + zz), r12 = 2f * (yz - wx);
            float r20 = 2f * (xz - wy), r21 = 2f * (yz + wx), r22 = 1f - 2f * (xx + yy);

            // full = R(+t) * meshLocal   (meshLocal columns m0=col0 ... m3=translation)
            Matrix4x4 ml = meshLocal;
            Matrix4x4 m = default;
            // column 0
            m.m00 = r00 * ml.m00 + r01 * ml.m10 + r02 * ml.m20;
            m.m10 = r10 * ml.m00 + r11 * ml.m10 + r12 * ml.m20;
            m.m20 = r20 * ml.m00 + r21 * ml.m10 + r22 * ml.m20;
            // column 1
            m.m01 = r00 * ml.m01 + r01 * ml.m11 + r02 * ml.m21;
            m.m11 = r10 * ml.m01 + r11 * ml.m11 + r12 * ml.m21;
            m.m21 = r20 * ml.m01 + r21 * ml.m11 + r22 * ml.m21;
            // column 2
            m.m02 = r00 * ml.m02 + r01 * ml.m12 + r02 * ml.m22;
            m.m12 = r10 * ml.m02 + r11 * ml.m12 + r12 * ml.m22;
            m.m22 = r20 * ml.m02 + r21 * ml.m12 + r22 * ml.m22;
            // column 3 (translation): R * ml_translation + t
            m.m03 = r00 * ml.m03 + r01 * ml.m13 + r02 * ml.m23 + tx;
            m.m13 = r10 * ml.m03 + r11 * ml.m13 + r12 * ml.m23 + ty;
            m.m23 = r20 * ml.m03 + r21 * ml.m13 + r22 * ml.m23 + tz;
            m.m33 = 1f;
            return m;
        }

        // =========================================================================================
        //  Palette construction (lift a cheap mesh + material from each prefab once)
        // =========================================================================================
        private static bool EnsurePalette()
        {
            int want = Mathf.Clamp(MaxTypes, 1, Palette.Length);
            if (_types != null && _types.Length == want)
            {
                return true;
            }

            var tm = Spawning.TrashSpawner.TrashManagerOrNull();
            if (tm == null)
            {
                return false;
            }

            var built = new List<TType>(want);
            for (int p = 0; p < Palette.Length && built.Count < want; p++)
            {
                TType ty = BuildType(tm, Palette[p].id, Palette[p].w);
                if (ty != null)
                {
                    built.Add(ty);
                }
            }
            if (built.Count == 0)
            {
                // fall back to scanning every prefab for the first usable mesh
                TType any = BuildFirstUsable(tm);
                if (any != null) built.Add(any);
            }
            if (built.Count == 0)
            {
                return false;
            }
            _types = built.ToArray();
            foreach (TType ty in _types)
            {
                Core.Log?.Msg($"[inst] type '{ty.Id}' mesh='{(ty.Mesh != null ? ty.Mesh.name : "?")}' verts={(ty.Mesh != null ? ty.Mesh.vertexCount : 0)} w={ty.Weight}");
            }
            return true;
        }

        private static TType BuildType(TrashManager tm, string id, float weight)
        {
            try
            {
                TrashItem prefab = tm.GetTrashPrefab(id);
                if (prefab == null) return null;
                return BuildFromPrefab(prefab, weight);
            }
            catch { return null; }
        }

        private static TType BuildFirstUsable(TrashManager tm)
        {
            try
            {
                var prefabs = tm.TrashPrefabs;
                if (prefabs == null) return null;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    TType t = BuildFromPrefab(prefabs[i], 1f);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        private static TType BuildFromPrefab(TrashItem prefab, float weight)
        {
            if (prefab == null) return null;
            GameObject go = prefab.gameObject;
            Il2CppArrayBase<MeshFilter> mfs = go.GetComponentsInChildren<MeshFilter>(true);
            if (mfs == null || mfs.Length == 0) return null;

            // Pass 1: find the largest renderable part in WORLD size - that is the item's main body
            // (a multi-part prefab like a bottle has body+lid+label; we must not pick the tiny lid).
            float maxSize = 0f;
            for (int j = 0; j < mfs.Length; j++)
            {
                MeshFilter mf = mfs[j];
                if (mf == null) continue;
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                MeshRenderer mr0 = mf.GetComponent<MeshRenderer>();
                if (mr0 == null || mr0.sharedMaterial == null) continue;
                float s = Vector3.Scale(m.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > maxSize) maxSize = s;
            }
            if (maxSize <= 0f) return null;

            // Pass 2: among parts that are the body (>= 50% of the largest), pick the cheapest LOD.
            MeshFilter bestMf = null;
            Mesh best = null;
            Material bestMat = null;
            int bestV = int.MaxValue;
            for (int j = 0; j < mfs.Length; j++)
            {
                MeshFilter mf = mfs[j];
                if (mf == null) continue;
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterial == null) continue;
                float s = Vector3.Scale(m.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s < 0.5f * maxSize) continue;            // skip small parts (lids, labels, caps)
                if (m.vertexCount >= bestV) continue;        // among body LODs, prefer the cheapest
                bestMf = mf; best = m; bestMat = mr.sharedMaterial; bestV = m.vertexCount;
            }
            if (bestMf == null || best == null || bestMat == null) return null;

            var ty = new TType
            {
                Id = prefab.ID,
                Mesh = best,
                Mat = new Material(bestMat),
                Weight = weight,
                MeshLocal = go.transform.worldToLocalMatrix * bestMf.transform.localToWorldMatrix,
            };
            ty.Mat.enableInstancing = true;
            return ty;
        }

        internal static void Clear()
        {
            _count = 0;
            _active = 0;
            _pending = false;
            Calibration.Reset();
            _px = _pz = _py = _vy = _restY = _baseColY = _qx = _qy = _qz = _qw = null;
            _type = null;
            _settled = _hidden = _dead = null;
            _matrices = null;
            // keep _types (palette + calibration) cached across respawns; _groundCache too
        }

        internal static void ResetPalette()
        {
            _types = null;
            _groundCache.Clear();
        }

        // =========================================================================================
        //  Empirical resting-pose calibration: drop one real probe per uncalibrated type, let it
        //  settle, capture its real resting rotation + ground clearance. Makes virtual == real.
        // =========================================================================================
        private static class Calibration
        {
            private const int MinSettleFrames = 45;     // never accept a "settle" before the probe has had time to fall
            private const int SettleTimeoutFrames = 220; // hard cap (~3.5s) - capture whatever pose it is in
            private const float SettleVel2 = 0.0025f;   // velocity^2 below which we treat it as settled
            private const float MinDescent = 0.4f;       // must have dropped at least this far from spawn
            private const float ProbeDropHeight = 4.5f;

            internal static bool Active { get; private set; }
            internal static int Outstanding { get; private set; }

            private static readonly List<TType> _pendingTypes = new List<TType>();
            private static readonly List<TrashItem> _probes = new List<TrashItem>();
            private static readonly List<float> _spawnY = new List<float>();
            private static int _frames;

            /// <summary>Spawn probes for every uncalibrated type. Returns true if calibration started.</summary>
            internal static bool Begin(TType[] types)
            {
                if (Active) return true;
                _pendingTypes.Clear();
                _probes.Clear();
                _spawnY.Clear();
                _frames = 0;

                var tm = Spawning.TrashSpawner.TrashManagerOrNull();
                if (tm == null) return false;
                if (!Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) return false;

                for (int t = 0; t < types.Length; t++)
                {
                    if (types[t].Calibrated) continue;
                    _pendingTypes.Add(types[t]);
                }
                if (_pendingTypes.Count == 0) return false;

                for (int t = 0; t < _pendingTypes.Count; t++)
                {
                    TType ty = _pendingTypes[t];
                    // spread probes well apart on a ring so they never land on each other (that gave a bad,
                    // too-high resting clearance); dropped from a few metres up.
                    float a = (float)(t) / Math.Max(1, _pendingTypes.Count) * Mathf.PI * 2f;
                    float pr = 3f + 0.6f * _pendingTypes.Count;
                    Vector3 pos = pp + new Vector3(Mathf.Cos(a) * pr, ProbeDropHeight, Mathf.Sin(a) * pr);
                    TrashItem probe = null;
                    try
                    {
                        probe = tm.CreateTrashItem(ty.Id, pos, UnityEngine.Random.rotation, Vector3.zero,
                            System.Guid.NewGuid().ToString(), false);
                    }
                    catch (Exception e) { Core.Log?.Warning("[inst] probe spawn failed for " + ty.Id + ": " + e.Message); }
                    if (probe != null) MarkRealCreated();   // a real Saveable TrashItem now exists -> save guard must sweep
                    _probes.Add(probe);
                    _spawnY.Add(pos.y);
                }

                Active = true;
                Outstanding = _pendingTypes.Count;
                return true;
            }

            /// <summary>Poll probes; capture pose when settled or on timeout. Returns true when all done.</summary>
            internal static bool Tick()
            {
                if (!Active) return true;
                _frames++;
                bool timeout = _frames >= SettleTimeoutFrames;

                bool allSettled = true;
                for (int t = 0; t < _pendingTypes.Count; t++)
                {
                    TType ty = _pendingTypes[t];
                    if (ty.Calibrated) continue;
                    TrashItem probe = _probes[t];
                    if (probe == null) { ty.Calibrated = true; continue; } // spawn failed -> keep identity defaults

                    bool settled = false;
                    if (_frames >= MinSettleFrames)
                    {
                        try
                        {
                            // require it to have actually dropped, then to have (nearly) stopped moving
                            float descended = _spawnY[t] - probe.transform.position.y;
                            Rigidbody rb = probe.GetComponentInChildren<Rigidbody>();
                            bool slow = rb == null || rb.velocity.sqrMagnitude < SettleVel2;
                            if (descended >= MinDescent && slow) settled = true;
                        }
                        catch { settled = true; }
                    }

                    if (!settled) { allSettled = false; continue; }
                    Capture(ty, probe, true);
                }

                if (allSettled || timeout)
                {
                    // capture any not yet captured (timeout path). A probe that reached the GROUND has a valid
                    // resting rotation even if still slowly rolling (cans/bottles); only a probe that never fell
                    // keeps identity - so we never bake a genuinely mid-air pose.
                    for (int t = 0; t < _pendingTypes.Count; t++)
                    {
                        TType ty = _pendingTypes[t];
                        if (ty.Calibrated || _probes[t] == null) continue;
                        bool descended = false;
                        try { descended = (_spawnY[t] - _probes[t].transform.position.y) >= MinDescent; } catch { }
                        Capture(ty, _probes[t], descended);
                    }
                    DestroyProbes();
                    Active = false;
                    Outstanding = 0;
                    return true;
                }
                return false;
            }

            private static void Capture(TType ty, TrashItem probe, bool genuine)
            {
                try
                {
                    Transform tr = probe.transform;
                    if (genuine) ty.RestRot = tr.rotation;   // only ever bake a REAL settled rotation, never mid-air
                    // clearance = pivot height above the NAVMESH ground - the SAME ground source the virtual field
                    // uses - so a field item rests at navmeshY+clearance AND a materialized item spawned at that
                    // exact spot needs no correction (no height jump when virtual -> real).
                    Ground g = SampleGround(tr.position.x, tr.position.z, tr.position.y);
                    ty.Clearance = Mathf.Clamp(tr.position.y - g.Y, -0.3f, 0.6f);
                    Core.Log?.Msg($"[inst] calibrated '{ty.Id}': clearance={ty.Clearance:F2} rot={(genuine ? tr.rotation.eulerAngles.ToString() : "identity(timeout)")}");
                }
                catch (Exception e) { Core.Log?.Warning("[inst] calibrate capture failed for " + ty.Id + ": " + e.Message); }
                ty.Calibrated = true;
            }

            private static void DestroyProbes()
            {
                for (int t = 0; t < _probes.Count; t++)
                {
                    TrashItem p = _probes[t];
                    if (p == null) continue;
                    try { p.DestroyTrash(); } catch { }
                    // belt-and-suspenders: if DestroyTrash threw/no-op'd and the object is still alive, force it.
                    try { if (p != null) UnityEngine.Object.Destroy(p.gameObject); } catch { }
                }
                _probes.Clear();
                _pendingTypes.Clear();
            }

            internal static void Reset()
            {
                DestroyProbes();
                Active = false;
                Outstanding = 0;
                _frames = 0;
            }
        }
    }
}
