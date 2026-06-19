using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Instanced
{
    /// <summary>
    /// The 100k path: trash as pure DATA (flat managed arrays), NO GameObjects. Gravity sim + per-item terrain
    /// ground in plain C#; GPU-instanced rendering via Graphics.DrawMeshInstanced, frustum-culled on the CPU.
    ///
    /// To look + behave 1:1 like base-game trash while staying cheap:
    ///  - TERRAIN: ground height from NavMesh.SamplePosition (walkable only, tree/roof-safe) refined by a short
    ///    raycast to the exact surface; the slope normal tilts the resting pose.
    ///  - SEAM: per-type resting pose is CALIBRATED empirically (drop a real probe, capture its settled rotation +
    ///    ground clearance) so a materialized real item is indistinguishable from its instanced neighbours.
    ///  - FULL MODEL: each type renders ALL its distinct prefab parts (body + lid + label + ...), LOD-deduped by
    ///    name, so a virtual instance looks identical to the real prefab (no missing/invisible parts, no pop on
    ///    materialize). Per instance we store ONE root matrix; each part draws root * partLocal.
    ///  - INTERACTION: the Virtualizer materializes the few near instances into real TrashItems and hides the
    ///    virtual copy; on demote it Restores the virtual at the resting pose; on pickup it Kills it permanently.
    /// </summary>
    internal static class InstancedTrash
    {
        private const int BatchSize = 1023;
        private const float Gravity = -22f;
        private const float GroundCellSize = 5f;     // navmesh sample cache cell (m); bigger = fewer one-time samples
        private const float NavSampleMaxDist = 25f;  // how far up/down NavMesh.SamplePosition searches
        private static readonly int GroundRayMask = ~(1 << 10);   // ground-refine raycast: everything EXCEPT the Trash layer (10)

        internal static bool Shadows = false;
        internal static int MaxTypes = 8;            // runtime-switchable (tv maxtypes N): 1 = single type, 8 = variety
        internal static string OnlyType = null;      // debug: if set, build the palette with ONLY this type id

        // ----- type palette (one render set per distinct mesh PART) -----
        private sealed class Part
        {
            public Mesh Mesh;
            public Material[] Mats;                         // ONE material per submesh; Mats.Length == Mesh.subMeshCount
            public Matrix4x4 Local = Matrix4x4.identity;   // part transform relative to the prefab root
        }

        // One physics-settled rest captured by dropping a real probe on flat ground: its settled root rotation
        // plus the exact height its root rested above the ground. Each instance picks one at random (+ random yaw)
        // so the field looks like real trash that fell - varied stable orientations, flush on the ground, no float.
        private struct Pose
        {
            public Quaternion Rot;
            public float Clearance;   // root.y - groundY at the physics rest
        }

        private sealed class TType
        {
            public string Id;
            public Part[] Parts;                              // every distinct renderable part of the prefab (LOD-deduped)
            public Pose[] Poses;                              // K physics-settled rests (replaces single RestRot+Clearance)
            public bool Calibrated;
            public float Weight = 1f;                         // relative share of instances
            public int Start, Len;                            // contiguous instance range [Start, Start+Len)
        }

        // Curated common litter (id, weight).
        private static readonly (string id, float w)[] Palette =
        {
            ("trashbag", 3f), ("glassbottle", 2f), ("waterbottle", 2f), ("coffeecup", 1.8f),
            ("energydrink", 1.5f), ("cigarettebox", 1.4f), ("litter1", 1.2f), ("soilbag", 1f),
        };

        private static TType[] _types;
        private static Il2CppStructArray<Matrix4x4> _batch;
        private static readonly float[] _renderPlanes = new float[24];
        private const float RenderCullMargin = 6f;   // expand the cull frustum so big/edge items never pop at the screen edge
        private static int _lastDrawn;               // visible instances drawn last frame (after culling)

        // ----- incremental-add model (routing the GAME's generated trash in one item at a time) -----
        // The benchmark path (Setup/BuildPending) builds a whole field at once with contiguous per-type
        // [Start,Len) ranges. The router instead appends items of mixed types in arbitrary order via AddOne, so
        // Render is driven by PER-TYPE index buckets (filled by both paths) rather than contiguous ranges.
        private static int _capacity;                          // allocated length of the SoA arrays
        private static List<int>[] _typeBuckets;               // _typeBuckets[t] = instance indices of type t (for contiguous-ish render)
        private static readonly Dictionary<string, int> _typeIndex = new Dictionary<string, int>();   // trash id -> index into _types
        internal const int CapacityCeiling = 2_000_000;        // hard managed cap so the router can never grow arrays unbounded
        // True once AddOne has stored ROUTED game trash (vs the benchmark Setup field). Gates persistence: only
        // routed trash is saved to the blob; the benchmark `tv inst` field stays ephemeral.
        internal static bool RoutedDataPresent;

        // ----- struct-of-arrays instance state (pure managed) -----
        private static float[] _px, _pz, _py, _vy, _restY;
        private static float[] _qx, _qy, _qz, _qw;   // per-item ROOT rotation (for materialize)
        private static byte[] _type;                 // index into _types
        private static bool[] _settled, _hidden, _dead;
        // _realized = a real TrashItem has been materialized for this index. Decoupled from _hidden so a materialized
        // item KEEPS drawing as an instance (no shading swap) while it sits at its rest pose; it is only Hidden once
        // it actually diverges (gets grabbed/thrown). CollectVisible skips _realized so we never re-materialize it.
        private static bool[] _realized;
        private static Matrix4x4[] _matrices;        // per-item ROOT matrix; only m13 (world Y) animates during the fall
        private static int _count, _active;

        // ----- navmesh ground cache -----
        private struct Ground { public float Y; public Vector3 N; public bool Hit; }
        private static readonly Dictionary<long, Ground> _groundCache = new Dictionary<long, Ground>(8192);

        // ----- deferred build (we may need to calibrate first) -----
        private static bool _pending;
        private static int _pendingN;
        private static Vector3 _pendingCenter;

        internal static int Count => _count;
        // Live = not yet collected/removed. _count keeps dead slots (Kill only flags _dead), so this is the real
        // "trash still on the map" number - it drops as the player or a cleaner collects items.
        internal static int LiveCount { get { if (_dead == null) return _count; int n = 0; for (int i = 0; i < _count; i++) if (!_dead[i]) n++; return n; } }
        internal static int Active => _active;
        internal static int Drawn => _lastDrawn;
        internal static int TypeCount => _types != null ? _types.Length : 0;
        internal static bool Ready => _types != null && _types.Length > 0;

        // Save-safety: set the moment the instanced path creates ANY real TrashItem (probe OR materialized item).
        internal static bool EverMaterialized { get; private set; }
        internal static void MarkRealCreated() => EverMaterialized = true;

        /// <summary>Destroy in-flight calibration probes + cancel a pending build, WITHOUT touching the field.</summary>
        internal static void AbortCalibration()
        {
            Calibration.Reset();
            _pending = false;
        }

        // ----- ground-drift self-test: sample-activate instances as DYNAMIC real items, wait, then measure how far
        // they moved from their virtual rest pose. Large drift = they were NOT actually resting on the ground. -----
        internal static bool DriftActive => GroundDrift.Active;
        internal static void BeginDrift(int count) => GroundDrift.Begin(count);
        internal static void DriftTick(float dt) => GroundDrift.Tick(dt);
        internal static void AbortDrift() => GroundDrift.Abort();

        // =========================================================================================
        internal static bool Setup(int n, Vector3 center)
        {
            if (n < 0) n = 0;

            // Respawn safety: tear down the Virtualizer + halt the OLD field BEFORE we reallocate, so no stale
            // _real index can outlive its array, and Virtualizer.Tick no-ops during the calibration window.
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
            if (Calibration.Active)
            {
                if (Calibration.Tick()) BuildPending();
                return;
            }

            if (_active <= 0 || _count <= 0 || dt <= 0f) return;

            float g = Gravity * dt;
            int active = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_settled[i] || _hidden[i] || _dead[i]) continue;
                _vy[i] += g;
                float y = _py[i] + _vy[i] * dt;
                if (y <= _restY[i]) { y = _restY[i]; _settled[i] = true; }
                else active++;
                _py[i] = y;
                _matrices[i].m13 = y;   // root world Y = py
            }
            _active = active;
        }

        internal static void Render()
        {
            if (_count <= 0 || _types == null || _batch == null) return;
            try
            {
                ShadowCastingMode sc = Shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                // Unity does NOT frustum-cull DrawMeshInstanced instances - we do it ourselves. For each type we
                // draw every distinct PART (root matrix * part-local) so the instanced model matches the full prefab.
                float[] planes = Frustum.Compute(Frustum.Cam(), _renderPlanes) ? _renderPlanes : null;
                int vis = 0;
                for (int t = 0; t < _types.Length; t++)
                {
                    TType ty = _types[t];
                    List<int> bucket = (_typeBuckets != null && t < _typeBuckets.Length) ? _typeBuckets[t] : null;
                    if (ty.Parts == null || bucket == null || bucket.Count == 0) continue;
                    int bn = bucket.Count;
                    for (int pi = 0; pi < ty.Parts.Length; pi++)
                    {
                        Part part = ty.Parts[pi];
                        if (part.Mesh == null || part.Mats == null || part.Mats.Length == 0) continue;
                        int subCount = part.Mats.Length;
                        int count = 0;
                        for (int bi = 0; bi < bn; bi++)
                        {
                            int i = bucket[bi];
                            if (_hidden[i] || _dead[i]) continue;
                            if (planes != null && !Frustum.Contains(planes, _px[i], _py[i], _pz[i], RenderCullMargin)) continue;
                            if (pi == 0) vis++;   // count each visible instance once (NOT per submesh)
                            _batch[count++] = Mul(_matrices[i], part.Local);
                            if (count == BatchSize)
                            {
                                // The matrix batch is per-mesh; draw EVERY submesh of the mesh with its own
                                // material (DrawMeshInstanced defaults to submesh 0, which is why multi-submesh
                                // meshes - glass body, can exterior+label, cup handle - used to vanish). All
                                // submeshes consume _batch BEFORE the next refill resets count, so reuse is safe
                                // (DrawMeshInstanced copies the matrices synchronously). NOTE: instanced
                                // transparent submeshes are not per-instance depth-sorted (cosmetic; the real
                                // near renderer covers close-up correctness).
                                for (int s = 0; s < subCount; s++)
                                {
                                    Material m = part.Mats[s]; if (m == null) continue;
                                    Graphics.DrawMeshInstanced(part.Mesh, s, m, _batch, count, null, sc, Shadows, 0);
                                }
                                count = 0;
                            }
                        }
                        if (count > 0)
                        {
                            for (int s = 0; s < subCount; s++)
                            {
                                Material m = part.Mats[s]; if (m == null) continue;
                                Graphics.DrawMeshInstanced(part.Mesh, s, m, _batch, count, null, sc, Shadows, 0);
                            }
                        }
                    }
                }
                _lastDrawn = vis;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[inst] render error (disabling): " + e.Message);
                _count = 0;
            }
        }

        // =========================================================================================
        private static void BuildPending()
        {
            if (!_pending) return;
            _pending = false;
            int n = _pendingN;
            Vector3 center = _pendingCenter;

            float radius = Mathf.Clamp((float)Math.Sqrt(n) * 0.5f, 20f, 220f);

            _px = new float[n]; _pz = new float[n]; _py = new float[n]; _vy = new float[n];
            _restY = new float[n];
            _qx = new float[n]; _qy = new float[n]; _qz = new float[n]; _qw = new float[n];
            _type = new byte[n];
            _settled = new bool[n]; _hidden = new bool[n]; _dead = new bool[n];
            _realized = new bool[n];
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

                    // Pick one physics-settled rest at random + a random yaw about world-up. NO slope tilt: the pose
                    // already encodes a stable flat-ground rest, and tilting it to the local slope is exactly the
                    // "perched at an odd angle" look. Yaw about up doesn't change the lowest point, so the captured
                    // clearance stays exact -> the item sits flush on the ground, like real trash that fell there.
                    Pose pose = (ty.Poses != null && ty.Poses.Length > 0)
                        ? ty.Poses[rng.Next(ty.Poses.Length)]
                        : new Pose { Rot = Quaternion.identity, Clearance = 0.1f };
                    float restY = gnd.Y + pose.Clearance;

                    float yaw = (float)(rng.NextDouble() * 360.0);
                    Quaternion rot = Quaternion.AngleAxis(yaw, Vector3.up) * pose.Rot;

                    float fallH = 8f + (float)(rng.NextDouble() * 26.0);

                    _px[i] = x; _pz[i] = z; _restY[i] = restY; _py[i] = restY + fallH; _vy[i] = 0f;
                    _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;
                    _matrices[i] = BuildRootMatrix(x, _py[i], z, rot.x, rot.y, rot.z, rot.w);
                }
            }
            sw.Stop();

            _count = n;
            _active = n;
            _capacity = n;
            // Render is bucket-driven; fill one bucket per type from the contiguous ranges this path just laid out.
            RebuildBuckets();
            if (_batch == null) _batch = new Il2CppStructArray<Matrix4x4>(BatchSize);

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

        // =========================================================================================
        //  Incremental add (router): feed ONE game-generated trash item into the instanced field as data.
        //  Returns the type index (>=0) if recorded, or -1 if the id has no renderable mesh (caller then leaves
        //  the real item alive instead of destroying it). Uses the generator's EXACT pose (already ground-correct),
        //  so no calibration/ground-snap is needed and the data matches where the real item would have sat.
        // =========================================================================================
        internal static int AddOne(string id, Vector3 pos, Quaternion rot, bool groundSnap = false)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            int t = TypeIndexFor(id);
            if (t < 0) return -1;                         // unrenderable id -> don't store a ghost
            if (_count >= CapacityCeiling) return -2;     // managed hard cap reached -> signal "at cap" (caller leaves item real)

            EnsureCapacity(_count + 1);
            int i = _count;

            float restY = pos.y;
            if (groundSnap)
            {
                // fallback when we could NOT let the item settle (e.g. burst overflow): raycast to the ground and
                // sit the mesh on it via geometric clearance, so it doesn't float at its airborne spawn height.
                float gy = pos.y;
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit rh, 60f, GroundRayMask, QueryTriggerInteraction.Ignore))
                    gy = rh.point.y;
                restY = gy + ComputeClearance(_types[t], rot);
            }

            _type[i] = (byte)t;
            _px[i] = pos.x; _py[i] = restY; _pz[i] = pos.z; _restY[i] = restY; _vy[i] = 0f;
            _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;
            _settled[i] = true;                           // placed where the game put it -> no fall anim, renders immediately
            _hidden[i] = _dead[i] = _realized[i] = false;
            _matrices[i] = BuildRootMatrix(pos.x, restY, pos.z, rot.x, rot.y, rot.z, rot.w);

            _count++;
            _active = _count;
            RoutedDataPresent = true;
            (_typeBuckets[t] ?? (_typeBuckets[t] = new List<int>(1024))).Add(i);
            if (_batch == null) _batch = new Il2CppStructArray<Matrix4x4>(BatchSize);
            return t;
        }

        // ----- compact persistence blob (routed game trash survives save/reload; never touches the game's
        // per-item Saveable path). Format: magic, version, typeCount, [type id strings], itemCount,
        // per item { byte typeIndex; float px,py,pz; float qx,qy,qz,qw } (~30 B/item). -----
        private const int BlobMagic = 0x54565631; // "TVB1"

        internal static int WriteBlob(string path)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                bw.Write(BlobMagic);
                bw.Write(1);
                int tc = _types != null ? _types.Length : 0;
                bw.Write(tc);
                for (int t = 0; t < tc; t++) bw.Write(_types[t].Id ?? "");
                int live = 0;
                for (int i = 0; i < _count; i++) if (!_dead[i]) live++;
                bw.Write(live);
                for (int i = 0; i < _count; i++)
                {
                    if (_dead[i]) continue;
                    bw.Write(_type[i]);
                    bw.Write(_px[i]); bw.Write(_py[i]); bw.Write(_pz[i]);
                    bw.Write(_qx[i]); bw.Write(_qy[i]); bw.Write(_qz[i]); bw.Write(_qw[i]);
                }
                return live;
            }
        }

        internal static int ReadBlob(string path)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var br = new System.IO.BinaryReader(fs))
            {
                if (br.ReadInt32() != BlobMagic) return 0;
                br.ReadInt32(); // version
                int tc = br.ReadInt32();
                var ids = new string[tc];
                for (int t = 0; t < tc; t++) ids[t] = br.ReadString();
                int n = br.ReadInt32();
                int added = 0;
                for (int i = 0; i < n; i++)
                {
                    byte ti = br.ReadByte();
                    float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                    float qx = br.ReadSingle(), qy = br.ReadSingle(), qz = br.ReadSingle(), qw = br.ReadSingle();
                    string id = (ti < ids.Length) ? ids[ti] : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (AddOne(id, new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw)) >= 0) added++;
                }
                return added;
            }
        }

        // Resolve a trash id to a type index, lazily building (and appending) a new type for ids the game spawns
        // that are not in our curated Palette (pipe, crushedcuke, cigarette, cuke, ...). -1 if it has no mesh.
        private static int TypeIndexFor(string id)
        {
            if (_typeIndex.TryGetValue(id, out int idx)) return idx;
            var tm = Spawning.TrashSpawner.TrashManagerOrNull();
            if (tm == null) return -1;
            TType ty = BuildType(tm, id, 1f);
            if (ty == null) { _typeIndex[id] = -1; return -1; }   // cache the negative so we don't rebuild every spawn
            int newIdx = (_types == null) ? 0 : _types.Length;
            if (_types == null) _types = new TType[] { ty };
            else { Array.Resize(ref _types, _types.Length + 1); _types[newIdx] = ty; }
            EnsureBuckets();
            _typeIndex[id] = newIdx;
            Core.Log?.Msg($"[route] new instanced type '{id}' (#{newIdx}, parts={ty.Parts.Length})");
            return newIdx;
        }

        private static void EnsureCapacity(int need)
        {
            if (_px != null && need <= _capacity) return;
            int target;
            if (_px == null) target = Math.Max(65536, need);
            else target = _capacity + 65536;                 // grow in fixed 64k chunks (bounded copy), never doubling
            if (target < need) target = need;
            if (target > CapacityCeiling) target = CapacityCeiling;

            Array.Resize(ref _px, target); Array.Resize(ref _pz, target); Array.Resize(ref _py, target);
            Array.Resize(ref _vy, target); Array.Resize(ref _restY, target);
            Array.Resize(ref _qx, target); Array.Resize(ref _qy, target); Array.Resize(ref _qz, target); Array.Resize(ref _qw, target);
            Array.Resize(ref _type, target);
            Array.Resize(ref _settled, target); Array.Resize(ref _hidden, target); Array.Resize(ref _dead, target); Array.Resize(ref _realized, target);
            Array.Resize(ref _matrices, target);
            _capacity = target;
        }

        private static void EnsureBuckets()
        {
            int n = _types != null ? _types.Length : 0;
            if (_typeBuckets == null) _typeBuckets = new List<int>[Math.Max(8, n)];
            else if (_typeBuckets.Length < n) Array.Resize(ref _typeBuckets, n);
        }

        // Rebuild all per-type buckets from _type[0.._count) (used by the benchmark contiguous path).
        private static void RebuildBuckets()
        {
            EnsureBuckets();
            for (int t = 0; t < _typeBuckets.Length; t++) _typeBuckets[t]?.Clear();
            for (int i = 0; i < _count; i++)
            {
                if (_dead[i]) continue;
                int t = _type[i];
                if (t < 0 || t >= _typeBuckets.Length) continue;
                (_typeBuckets[t] ?? (_typeBuckets[t] = new List<int>(1024))).Add(i);
            }
        }

        // =========================================================================================
        //  Uniform spatial-hash grid (x,z) over the field, so an ACTOR (player, cleaner) can find the items
        //  near it in O(neighbours) instead of scanning all _count every frame. Cells are keyed by integer
        //  cell coords packed into a long. Rebuilt periodically (the routed field is mostly static); the query
        //  applies the same dynamic filter as CollectNear, so a slightly stale grid is still correct for the
        //  flag part (it only misses items added since the last rebuild, which appear at the next rebuild).
        // =========================================================================================
        private const float GridCell = 8f;
        private static readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>(4096);

        private static int CellOf(float v) => Mathf.FloorToInt(v / GridCell);
        private static long CellKey(int cx, int cz) => ((long)(uint)cx << 32) | (uint)cz;
        // An item is an actor candidate iff it is live, not hidden, not already materialized, and settled
        // (identical predicate to CollectNear / CollectVisible).
        private static bool ActorCandidate(int i) => !_dead[i] && !_hidden[i] && !_realized[i] && _settled[i];

        internal static void BuildGrid()
        {
            foreach (var kv in _grid) kv.Value.Clear();   // reuse the per-cell lists
            for (int i = 0; i < _count; i++)
            {
                if (_dead[i]) continue;                    // dead = permanently gone; everything else is placed by position
                long key = CellKey(CellOf(_px[i]), CellOf(_pz[i]));
                if (!_grid.TryGetValue(key, out List<int> list)) { list = new List<int>(8); _grid[key] = list; }
                list.Add(i);
            }
        }

        /// <summary>Actor candidates within `radius` (2D) of (x,z), via the grid. Same result as a brute-force
        /// scan but only touches the ~(2*ceil(r/cell)+1)^2 cells around the point.</summary>
        internal static int QueryGrid(float x, float z, float radius, int[] outIdx, int max)
        {
            if (_px == null) return 0;
            float r2 = radius * radius;
            int cR = Mathf.CeilToInt(radius / GridCell);
            int qcx = CellOf(x), qcz = CellOf(z);
            int n = 0;
            for (int cx = qcx - cR; cx <= qcx + cR && n < max; cx++)
                for (int cz = qcz - cR; cz <= qcz + cR && n < max; cz++)
                {
                    if (!_grid.TryGetValue(CellKey(cx, cz), out List<int> list)) continue;
                    for (int li = 0; li < list.Count && n < max; li++)
                    {
                        int i = list[li];
                        if (!ActorCandidate(i)) continue;
                        float dx = _px[i] - x, dz = _pz[i] - z;
                        if (dx * dx + dz * dz <= r2) outIdx[n++] = i;
                    }
                }
            return n;
        }

        private static int BruteNear(float x, float z, float radius, int[] outIdx, int max)
        {
            float r2 = radius * radius; int n = 0;
            for (int i = 0; i < _count && n < max; i++)
            {
                if (!ActorCandidate(i)) continue;
                float dx = _px[i] - x, dz = _pz[i] - z;
                if (dx * dx + dz * dz <= r2) outIdx[n++] = i;
            }
            return n;
        }

        /// <summary>FAILABLE self-test (tv gridtest): the grid query must return EXACTLY the same index set as a
        /// brute-force scan for many random query points/radii. Logs PASS/FAIL.</summary>
        internal static void GridSelfTest()
        {
            if (_count <= 0) { Core.Log?.Warning("[grid] no field; spawn (tv inst) or route first"); return; }
            BuildGrid();
            var rng = new System.Random(777);
            int[] g = new int[16384], b = new int[16384];
            int tests = 16, pass = 0, maxN = 0;
            for (int t = 0; t < tests; t++)
            {
                int pick = rng.Next(_count);
                float x = _px[pick] + (float)(rng.NextDouble() * 30.0 - 15.0);
                float z = _pz[pick] + (float)(rng.NextDouble() * 30.0 - 15.0);
                float r = 6f + (float)(rng.NextDouble() * 26.0);   // mix of radii 6..32m
                int gn = QueryGrid(x, z, r, g, g.Length);
                int bn = BruteNear(x, z, r, b, b.Length);
                if (gn > maxN) maxN = gn;
                bool ok = gn == bn;
                if (ok)
                {
                    Array.Sort(g, 0, gn); Array.Sort(b, 0, bn);
                    for (int k = 0; k < gn; k++) if (g[k] != b[k]) { ok = false; break; }
                }
                if (ok) pass++;
                else Core.Log?.Warning($"[grid] MISMATCH at ({x:F0},{z:F0}) r={r:F0}: grid={gn} brute={bn}");
            }
            Core.Log?.Msg($"[grid] SELF-TEST {(pass == tests ? "PASS" : "FAIL")}: {pass}/{tests} query points match brute-force " +
                          $"(cell={GridCell}m, field={_count}, maxHits={maxN}).");
        }

        // ----- navmesh ground (cached by cell; tree/roof-safe), refined to the exact surface -----
        private static Ground SampleGround(float x, float z, float fallbackY)
        {
            long key = ((long)Mathf.FloorToInt(x / GroundCellSize) << 32) ^ (uint)Mathf.FloorToInt(z / GroundCellSize);
            if (_groundCache.TryGetValue(key, out Ground g)) return g;

            g.Y = fallbackY; g.N = Vector3.up; g.Hit = false;
            try
            {
                Vector3 src = new Vector3(x, fallbackY + 3f, z);
                if (NavMesh.SamplePosition(src, out NavMeshHit hit, NavSampleMaxDist, -1))
                {
                    Vector3 p = hit.position;
                    if (!float.IsNaN(p.y) && Mathf.Abs(p.y - fallbackY) < 200f)
                    {
                        g.Y = p.y;
                        Vector3 nrm = hit.normal;
                        if (nrm.sqrMagnitude > 0.01f && nrm.y > 0.3f) g.N = nrm.normalized;
                        g.Hit = true;

                        // NavMesh sits a little ABOVE the visual ground -> refine to the EXACT surface with a SHORT
                        // ray from just above it (too low to hit tree canopies/roofs, so it stays tree-safe).
                        if (Physics.Raycast(new Vector3(x, g.Y + 1.5f, z), Vector3.down,
                                out RaycastHit rh, 4f, GroundRayMask, QueryTriggerInteraction.Ignore))
                        {
                            g.Y = rh.point.y;
                            if (rh.normal.y > 0.3f) g.N = rh.normal.normalized;
                        }
                    }
                }
            }
            catch { }
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
        internal static bool IsSettled(int i) => InRange(i) && _settled[i];

        // Mark/unmark an index as having a live real TrashItem (so CollectVisible won't re-materialize it). Does NOT
        // touch _hidden, so the instance keeps rendering at its rest pose until the real item actually diverges.
        internal static void SetRealized(int i, bool v)
        {
            if (!InRange(i)) return;
            _realized[i] = v;
        }

        internal static void Hide(int i)
        {
            if (!InRange(i)) return;
            _hidden[i] = true;
        }

        internal static void Restore(int i, Vector3 pos, Quaternion rot)
        {
            if (!InRange(i)) return;
            _hidden[i] = false;
            _realized[i] = false;
            _settled[i] = true;
            _px[i] = pos.x; _py[i] = pos.y; _pz[i] = pos.z; _restY[i] = pos.y; _vy[i] = 0f;
            _qx[i] = rot.x; _qy[i] = rot.y; _qz[i] = rot.z; _qw[i] = rot.w;
            _matrices[i] = BuildRootMatrix(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w);
        }

        internal static void Kill(int i)
        {
            if (!InRange(i)) return;
            _dead[i] = true;
            _hidden[i] = true;
            _realized[i] = false;
        }

        /// <summary>Up to max SETTLED, non-hidden, non-dead instance indices within radius (2D) of p.</summary>
        internal static int CollectNear(Vector3 p, float radius, int[] outIdx, int max)
        {
            if (_count <= 0 || _px == null) return 0;
            float r2 = radius * radius;
            int n = 0;
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || _realized[i] || !_settled[i]) continue;
                float dx = _px[i] - p.x, dz = _pz[i] - p.z;
                if (dx * dx + dz * dz <= r2) outIdx[n++] = i;
            }
            return n;
        }

        /// <summary>Pass 1 = anti-glitch ring around the PREDICTED player pos backCenter (front of the list, so the
        /// per-frame budget fills it first). Pass 2 = inside the current OR predicted frustum within viewDist.</summary>
        internal static int CollectVisible(float[] cur, float[] pred, Vector3 backCenter, Vector3 p,
            float backRadius, float viewDist, float margin, int[] outIdx, int max)
        {
            if (_count <= 0 || _px == null) return 0;
            float br2 = backRadius * backRadius;
            float vd2 = viewDist * viewDist;
            int n = 0;
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || _realized[i] || !_settled[i]) continue;
                float dx = _px[i] - backCenter.x, dz = _pz[i] - backCenter.z;
                if (dx * dx + dz * dz <= br2) outIdx[n++] = i;
            }
            for (int i = 0; i < _count && n < max; i++)
            {
                if (_dead[i] || _hidden[i] || _realized[i] || !_settled[i]) continue;
                float bx = _px[i] - backCenter.x, bz = _pz[i] - backCenter.z;
                if (bx * bx + bz * bz <= br2) continue;
                float dx = _px[i] - p.x, dz = _pz[i] - p.z;
                if (dx * dx + dz * dz > vd2) continue;
                if (Frustum.Contains(cur, _px[i], _py[i], _pz[i], margin) ||
                    Frustum.Contains(pred, _px[i], _py[i], _pz[i], margin)) outIdx[n++] = i;
            }
            return n;
        }

        // =========================================================================================
        //  Matrix helpers (hand-rolled, pure managed - no per-item interop in the hot loop)
        // =========================================================================================

        /// <summary>TRS(pos, rot, 1) as a world ROOT matrix, no interop.</summary>
        private static Matrix4x4 BuildRootMatrix(float tx, float ty, float tz, float qx, float qy, float qz, float qw)
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

        /// <summary>a * b (4x4), hand-rolled - used per visible instance+part to place each part in world space.</summary>
        private static Matrix4x4 Mul(in Matrix4x4 a, in Matrix4x4 b)
        {
            Matrix4x4 m = default;
            m.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
            m.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
            m.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
            m.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
            m.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
            m.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
            m.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
            m.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
            m.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
            m.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
            m.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
            m.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
            m.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
            m.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
            m.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
            m.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
            return m;
        }

        /// <summary>Ground clearance computed from GEOMETRY (deterministic, no probe-settle noise): how high the
        /// root must sit so the LOWEST point of the rotated mesh (all parts) just touches the ground.</summary>
        private static float ComputeClearance(TType ty, Quaternion restRot)
        {
            if (ty == null || ty.Parts == null || ty.Parts.Length == 0) return 0.1f;
            Matrix4x4 rot = Matrix4x4.Rotate(restRot);
            float minY = float.MaxValue;
            for (int pi = 0; pi < ty.Parts.Length; pi++)
            {
                Part part = ty.Parts[pi];
                if (part.Mesh == null) continue;
                Bounds b = part.Mesh.bounds;
                Matrix4x4 m = rot * part.Local;   // mesh-local -> root -> rotated to the resting orientation
                Vector3 c = b.center, e = b.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 w = m.MultiplyPoint3x4(new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z));
                            if (w.y < minY) minY = w.y;
                        }
            }
            if (minY == float.MaxValue || float.IsNaN(minY)) return 0.1f;
            return Mathf.Clamp(-minY, -0.2f, 1.0f);
        }

        // =========================================================================================
        //  Palette construction - render EVERY distinct part of the prefab (LOD-deduped by name)
        // =========================================================================================
        private static bool EnsurePalette()
        {
            var tm = Spawning.TrashSpawner.TrashManagerOrNull();
            if (tm == null) return false;

            // debug: isolate a single type so we can see exactly how ONE type renders instanced.
            if (!string.IsNullOrEmpty(OnlyType))
            {
                if (_types != null && _types.Length == 1 && _types[0].Id == OnlyType) return true;
                TType only = BuildType(tm, OnlyType, 1f);
                if (only == null) { Core.Log?.Warning($"[inst] onlytype '{OnlyType}' has no mesh."); return false; }
                DestroyPaletteMaterials();
                _types = new[] { only };
                Core.Log?.Msg($"[inst] type '{only.Id}' parts={only.Parts.Length} (ONLYTYPE)");
                return true;
            }

            int want = Mathf.Clamp(MaxTypes, 1, Palette.Length);
            if (_types != null && _types.Length == want) return true;

            var built = new List<TType>(want);
            for (int p = 0; p < Palette.Length && built.Count < want; p++)
            {
                TType ty = BuildType(tm, Palette[p].id, Palette[p].w);
                if (ty != null) built.Add(ty);
            }
            if (built.Count == 0)
            {
                TType any = BuildFirstUsable(tm);
                if (any != null) built.Add(any);
            }
            if (built.Count == 0) return false;

            DestroyPaletteMaterials();
            _types = built.ToArray();
            foreach (TType ty in _types)
            {
                Core.Log?.Msg($"[inst] type '{ty.Id}' parts={ty.Parts.Length} w={ty.Weight}");
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

        /// <summary>Build a type by collecting EVERY renderable part of the prefab, grouping LOD variants by base
        /// name and keeping the cheapest LOD per group, so the instanced model has all parts (body+lid+label+...)
        /// exactly like the real prefab and nothing renders invisibly/incompletely.</summary>
        private static TType BuildFromPrefab(TrashItem prefab, float weight)
        {
            if (prefab == null) return null;
            GameObject go = prefab.gameObject;
            Il2CppArrayBase<MeshFilter> mfs = go.GetComponentsInChildren<MeshFilter>(true);
            if (mfs == null || mfs.Length == 0) return null;

            Matrix4x4 worldToRoot = go.transform.worldToLocalMatrix;
            // Group LOD variants by base name and keep the HIGHEST-DETAIL LOD (smallest _LOD index; no suffix =
            // LOD0). Deep-check finding: keeping the CHEAPEST LOD dropped glassbottle's label (LOD0 has
            // 'glass bottle label mat', LOD2 the plain mat) and gave energydrink the coarse Interior_LOD1 instead
            // of Interior LOD0. LOD0 = exactly what the player sees on the real prefab up close.
            var groups = new Dictionary<string, (MeshFilter mf, Mesh m, MeshRenderer mr, int lod, int verts)>();
            for (int j = 0; j < mfs.Length; j++)
            {
                MeshFilter mf = mfs[j]; if (mf == null) continue;
                Mesh m = mf.sharedMesh; if (m == null) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterial == null) continue;
                string baseName = StripLod(m.name);
                int lod = ParseLod(m.name);
                if (!groups.TryGetValue(baseName, out var cur) || lod < cur.lod || (lod == cur.lod && m.vertexCount > cur.verts))
                {
                    groups[baseName] = (mf, m, mr, lod, m.vertexCount);
                }
            }
            if (groups.Count == 0) return null;

            // One Part per chosen mesh, with ONE cloned material PER SUBMESH (mesh.subMeshCount). A multi-submesh
            // mesh (glass label+body, can interior+exterior+label, cup wall+handle, cig lid+main) must draw every
            // submesh with its own material - drawing only submesh 0 is what made parts disappear.
            var parts = new List<Part>(groups.Count);
            foreach (var kv in groups)
            {
                var grp = kv.Value;
                Mesh mesh = grp.m;
                int subCount = Mathf.Max(1, mesh.subMeshCount);
                Il2CppArrayBase<Material> src = null;
                try { src = grp.mr.sharedMaterials; } catch { }
                int srcLen = src != null ? src.Length : 0;
                var mats = new Material[subCount];
                for (int s = 0; s < subCount; s++)
                {
                    Material srcMat = null;
                    if (srcLen > 0) srcMat = src[s < srcLen ? s : srcLen - 1];   // clamp: more submeshes than mats -> reuse last (Unity behaviour)
                    if (srcMat == null) srcMat = grp.mr.sharedMaterial;          // never-null fallback
                    Material clone = new Material(srcMat);
                    clone.enableInstancing = true;
                    mats[s] = clone;
                }
                parts.Add(new Part
                {
                    Mesh = mesh,
                    Mats = mats,
                    Local = worldToRoot * grp.mf.transform.localToWorldMatrix,
                });
            }
            return new TType { Id = prefab.ID, Parts = parts.ToArray(), Weight = weight };
        }

        /// <summary>Strip a trailing "_LOD&lt;digits&gt;" so LOD variants of the same part group together.</summary>
        private static string StripLod(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int idx = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx < name.Length - 4)
            {
                bool digits = true;
                for (int k = idx + 4; k < name.Length; k++) if (!char.IsDigit(name[k])) { digits = false; break; }
                if (digits) return name.Substring(0, idx);
            }
            return name;
        }

        /// <summary>Trailing "_LOD&lt;n&gt;" index; a name with no suffix is the highest detail (LOD 0).</summary>
        private static int ParseLod(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int idx = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx < name.Length - 4)
            {
                int n = 0; bool any = false, digits = true;
                for (int k = idx + 4; k < name.Length; k++)
                {
                    if (char.IsDigit(name[k])) { n = n * 10 + (name[k] - '0'); any = true; }
                    else { digits = false; break; }
                }
                if (digits && any) return n;
            }
            return 0;
        }

        /// <summary>Debug: dump every renderable mesh part of each palette prefab + which parts the instancer renders.</summary>
        internal static void DumpMeshParts()
        {
            var tm = Spawning.TrashSpawner.TrashManagerOrNull();
            if (tm == null) { Core.Log?.Warning("[meshdiag] no TrashManager"); return; }
            foreach (var pe in Palette)
            {
                TrashItem prefab = null;
                try { prefab = tm.GetTrashPrefab(pe.id); } catch { }
                if (prefab == null) { Core.Log?.Msg($"[meshdiag] '{pe.id}': prefab NULL"); continue; }
                GameObject go = prefab.gameObject;
                Il2CppArrayBase<MeshFilter> mfs = go.GetComponentsInChildren<MeshFilter>(true);
                TType t = BuildFromPrefab(prefab, 1f);
                string picked = "(none)";
                if (t != null && t.Parts != null)
                {
                    var names = new string[t.Parts.Length];
                    for (int q = 0; q < t.Parts.Length; q++) names[q] = t.Parts[q].Mesh != null ? t.Parts[q].Mesh.name : "?";
                    picked = string.Join(" + ", names);
                    // this inspection-only TType is discarded; free its cloned submesh materials so repeated
                    // meshdiag calls don't leak.
                    foreach (var p in t.Parts)
                        if (p != null && p.Mats != null)
                            foreach (var mm in p.Mats) if (mm != null) try { UnityEngine.Object.Destroy(mm); } catch { }
                }
                // Scan ALL renderers (not just MeshFilters) so a SkinnedMeshRenderer body (which our MeshFilter-only
                // instancer would miss) shows up - and report each material's render queue / surface type so a
                // transparent glass material (which DrawMeshInstanced renders wrong) is obvious.
                Il2CppArrayBase<Renderer> rends = go.GetComponentsInChildren<Renderer>(true);
                Core.Log?.Msg($"[meshdiag] '{pe.id}': {(mfs == null ? 0 : mfs.Length)} meshfilters, {(rends == null ? 0 : rends.Length)} renderers -> instancer renders [{picked}]");
                if (rends == null) continue;
                for (int j = 0; j < rends.Length; j++)
                {
                    Renderer r = rends[j]; if (r == null) continue;
                    string rtype = "Renderer";
                    Mesh m = null;
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null) { rtype = "SKINNED"; m = smr.sharedMesh; }
                    else
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null) { rtype = "mesh"; m = mf.sharedMesh; }
                    }
                    Vector3 wb = m != null ? Vector3.Scale(m.bounds.size, r.transform.lossyScale) : Vector3.zero;
                    Core.Log?.Msg($"   [{j}] {rtype} '{(m != null ? m.name : "null")}' verts={(m != null ? m.vertexCount : 0)} size=({wb.x:F2},{wb.y:F2},{wb.z:F2}) enabled={r.enabled}");
                    Il2CppArrayBase<Material> mats = null;
                    try { mats = r.sharedMaterials; } catch { }
                    if (mats == null) continue;
                    for (int s = 0; s < mats.Length; s++)
                    {
                        Material mat = mats[s]; if (mat == null) { Core.Log?.Msg($"        submat[{s}] NULL"); continue; }
                        string shader = "?"; int q = -1; float surf = -1f; bool twoSided = false;
                        try { shader = mat.shader != null ? mat.shader.name : "null"; } catch { }
                        try { q = mat.renderQueue; } catch { }
                        try { if (mat.HasProperty("_Surface")) surf = mat.GetFloat("_Surface"); } catch { }
                        try { if (mat.HasProperty("_Cull")) twoSided = mat.GetFloat("_Cull") == 0f; } catch { }
                        string kind = (q >= 2900 || surf == 1f) ? "TRANSPARENT" : "opaque";
                        Core.Log?.Msg($"        submat[{s}] '{mat.name}' shader='{shader}' queue={q} surface={surf} {kind}{(twoSided ? " TWO-SIDED" : "")}");
                    }
                }
            }
        }

        internal static void Clear()
        {
            _count = 0;
            _active = 0;
            _capacity = 0;
            _pending = false;
            RoutedDataPresent = false;
            Calibration.Reset();
            _px = _pz = _py = _vy = _restY = _qx = _qy = _qz = _qw = null;
            _type = null;
            _settled = _hidden = _dead = _realized = null;
            _matrices = null;
            if (_typeBuckets != null) for (int t = 0; t < _typeBuckets.Length; t++) _typeBuckets[t]?.Clear();
            _typeIndex.Clear();
        }

        internal static void ResetPalette()
        {
            DestroyPaletteMaterials();
            _types = null;
            _groundCache.Clear();
        }

        // Each Part now owns subMeshCount cloned materials; destroy them before dropping _types so repeated
        // palette rebuilds (tv maxtypes / tv onlytype, and DumpMeshParts which builds-and-discards) don't leak.
        private static void DestroyPaletteMaterials()
        {
            if (_types == null) return;
            foreach (var ty in _types)
            {
                if (ty == null || ty.Parts == null) continue;
                foreach (var p in ty.Parts)
                {
                    if (p == null || p.Mats == null) continue;
                    foreach (var m in p.Mats)
                        if (m != null) try { UnityEngine.Object.Destroy(m); } catch { }
                }
            }
        }

        // =========================================================================================
        //  Physics-settled pose bake: per type, drop K real probes on flat ground and capture each settled
        //  rotation + its exact physics rest height. Baked ONE TYPE AT A TIME (few probes alive, no cross-type
        //  rolling). Each instance later picks one captured pose at random + a random yaw -> the field looks
        //  like real trash that fell: varied stable orientations, flush on the ground, never perched/floating.
        // =========================================================================================
        private static class Calibration
        {
            private const int PosesPerType = 16;        // K probes dropped per type
            // Wide spacing so a rolling cylinder/bag can NEVER reach a neighbour and settle leaning on it (that
            // captured a too-high clearance + an unstable pose -> the field then floated/slid). Each probe must
            // land alone on free flat ground. Low drop keeps roll small; the random START rotation still varies the
            // captured rest face, so we don't need a high tumble.
            private const float ProbeSpacing = 4.0f;
            private const float ProbeDropHeight = 1.0f;
            private const int MinSettleFrames = 25;     // earliest a probe may be judged settled
            private const int TypeTimeoutFrames = 150;  // per-type hard cap (force-capture whatever stands)
            private const float SettleVel2 = 0.0025f;
            private const float MinDescent = 0.3f;      // must actually have fallen (not spawned inside geometry)
            private const float FlatNormalY = 0.985f;   // only accept poses settled on ~flat ground (no baked tilt)

            internal static bool Active { get; private set; }
            internal static int Outstanding { get; private set; }   // types left to bake (startup log)

            private static readonly List<TType> _pendingTypes = new List<TType>();
            private static int _cursor;                 // index of the type currently baking
            private static int _typeFrames;             // frames since the current type's probes were dropped
            private static readonly List<TrashItem> _probes = new List<TrashItem>();   // current type's live probes
            private static readonly List<float> _spawnY = new List<float>();
            private static readonly List<Pose> _caps = new List<Pose>();               // current type's captured poses

            internal static bool Begin(TType[] types)
            {
                if (Active) return true;
                _pendingTypes.Clear();
                for (int t = 0; t < types.Length; t++)
                    if (!types[t].Calibrated) _pendingTypes.Add(types[t]);
                if (_pendingTypes.Count == 0) return false;

                var tm = Spawning.TrashSpawner.TrashManagerOrNull();
                if (tm == null) return false;
                if (!Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) return false;

                // log if the bake ground is sloped (would tilt poses) so it's visible rather than silent.
                if (Physics.Raycast(pp + Vector3.up, Vector3.down, out RaycastHit rh, 5f, GroundRayMask, QueryTriggerInteraction.Ignore)
                    && rh.normal.y < 0.99f)
                    Core.Log?.Warning($"[inst] calibration ground not flat (normal.y={rh.normal.y:F3}); stand on flat ground for best poses.");

                _cursor = 0;
                Active = true;
                Outstanding = _pendingTypes.Count;
                SpawnProbesFor(_pendingTypes[0]);
                return true;
            }

            private static bool SpawnProbesFor(TType ty)
            {
                DestroyProbes();
                _caps.Clear();
                _typeFrames = 0;
                var tm = Spawning.TrashSpawner.TrashManagerOrNull();
                if (tm == null) return false;
                if (!Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) return false;

                for (int k = 0; k < PosesPerType; k++)
                {
                    int gx = k % 4, gz = k / 4;
                    Vector3 pos = pp + new Vector3((gx - 1.5f) * ProbeSpacing, ProbeDropHeight, (gz - 1.5f) * ProbeSpacing);
                    TrashItem probe = null;
                    Spawning.RouteHook.Suppress = true;
                    try { probe = tm.CreateTrashItem(ty.Id, pos, UnityEngine.Random.rotation, Vector3.zero, System.Guid.NewGuid().ToString(), false); }
                    catch (Exception e) { Core.Log?.Warning("[inst] probe spawn failed for " + ty.Id + ": " + e.Message); }
                    finally { Spawning.RouteHook.Suppress = false; }
                    if (probe != null) MarkRealCreated();
                    _probes.Add(probe);
                    _spawnY.Add(pos.y);
                }
                return true;
            }

            internal static bool Tick()
            {
                if (!Active) return true;
                _typeFrames++;
                TType ty = _cursor < _pendingTypes.Count ? _pendingTypes[_cursor] : null;
                if (ty == null) { Finish(); return true; }

                bool timeout = _typeFrames >= TypeTimeoutFrames;
                bool allDone = true;
                for (int p = 0; p < _probes.Count; p++)
                {
                    TrashItem probe = _probes[p];
                    if (probe == null) continue;        // already captured (or failed spawn)
                    bool settled = false;
                    if (_typeFrames >= MinSettleFrames)
                    {
                        try
                        {
                            float descended = _spawnY[p] - probe.transform.position.y;
                            Rigidbody rb = probe.GetComponentInChildren<Rigidbody>();
                            bool slow = rb == null || rb.velocity.sqrMagnitude < SettleVel2;
                            if (descended >= MinDescent && slow) settled = true;
                        }
                        catch { settled = true; }
                    }
                    if (!settled) { allDone = false; continue; }
                    CaptureProbe(ty, probe, false);
                    DestroyOne(probe);          // destroy NOW - nulling the slot would make DestroyProbes skip it (leak)
                    _probes[p] = null;
                }

                if (allDone || timeout)
                {
                    if (timeout)
                        for (int p = 0; p < _probes.Count; p++)
                            if (_probes[p] != null) { CaptureProbe(ty, _probes[p], true); DestroyOne(_probes[p]); _probes[p] = null; }

                    FinalizeType(ty);
                    _cursor++;
                    if (_cursor < _pendingTypes.Count) { SpawnProbesFor(_pendingTypes[_cursor]); return false; }
                    Finish();
                    return true;
                }
                return false;
            }

            // Capture one settled probe: its rotation + the EXACT height its root rested above the ground under it
            // (physics rest height = where a real item rests = no float, and matches materialized real items). Reject
            // poses that settled on a slope (unless forced by timeout) so we never bake a tilt into a whole type.
            private static void CaptureProbe(TType ty, TrashItem probe, bool force)
            {
                try
                {
                    Vector3 rp = probe.transform.position;
                    float groundY = rp.y, ny = 1f;
                    if (Physics.Raycast(rp + Vector3.up * 0.6f, Vector3.down, out RaycastHit rh, 3f, GroundRayMask, QueryTriggerInteraction.Ignore))
                    { groundY = rh.point.y; ny = rh.normal.y; }
                    if (!force && ny < FlatNormalY) return;   // settled on a slope -> skip (don't bake tilt)
                    float clr = Mathf.Clamp(rp.y - groundY, -0.3f, 1.2f);
                    _caps.Add(new Pose { Rot = probe.transform.rotation, Clearance = clr });
                }
                catch { }
            }

            // Dedup near-identical rest faces (same down-face, differing only by yaw which is randomized at runtime),
            // guarantee >= 1 pose, store on the type.
            private static void FinalizeType(TType ty)
            {
                var poses = new List<Pose>(_caps.Count);
                for (int i = 0; i < _caps.Count; i++)
                {
                    Vector3 down = Quaternion.Inverse(_caps[i].Rot) * Vector3.down;   // local face pointing down (yaw-invariant)
                    bool dup = false;
                    for (int j = 0; j < poses.Count; j++)
                        if (Vector3.Dot(Quaternion.Inverse(poses[j].Rot) * Vector3.down, down) > 0.985f) { dup = true; break; }
                    if (!dup) poses.Add(_caps[i]);
                }
                if (poses.Count == 0)
                {
                    // nothing usable (all on slopes / spawns failed) -> upright AABB clearance so the type still
                    // renders grounded instead of vanishing.
                    poses.Add(new Pose { Rot = Quaternion.identity, Clearance = ComputeClearance(ty, Quaternion.identity) });
                }
                ty.Poses = poses.ToArray();
                ty.Calibrated = true;
                Outstanding = Math.Max(0, Outstanding - 1);
                Core.Log?.Msg($"[inst] calibrated '{ty.Id}': {ty.Poses.Length} settled pose(s) from {_caps.Count} drop(s)" +
                              (ty.Poses.Length < 4 ? " (low variety - relying on random yaw)" : ""));
            }

            private static void Finish()
            {
                DestroyProbes();
                _caps.Clear();
                Active = false;
                Outstanding = 0;
                _cursor = 0;
            }

            private static void DestroyOne(TrashItem pr)
            {
                if (pr == null) return;
                try { pr.DestroyTrash(); } catch { }
                try { if (pr != null) UnityEngine.Object.Destroy(pr.gameObject); } catch { }
            }

            private static void DestroyProbes()
            {
                for (int p = 0; p < _probes.Count; p++) DestroyOne(_probes[p]);
                _probes.Clear();
                _spawnY.Clear();
            }

            internal static void Reset()
            {
                DestroyProbes();
                _caps.Clear();
                _pendingTypes.Clear();
                Active = false;
                Outstanding = 0;
                _cursor = 0;
                _typeFrames = 0;
            }
        }

        // =========================================================================================
        //  Ground-drift self-test (the "activate a sample, wait 5s, did it move?" check)
        //  Materializes a sample of instances as DYNAMIC real items at their virtual rest pose, lets physics run for
        //  a few seconds, then reports how far each drifted: drop>0 = it fell (was floating above the ground),
        //  drop<0 = it was pushed up (was penetrating), ~0 = it was resting correctly. Restores the field afterwards.
        // =========================================================================================
        private static class GroundDrift
        {
            private const float WaitSeconds = 5f;
            private const float SampleRadius = 18f;
            private const float DriftThreshold = 0.15f;   // total displacement (m) above which it clearly wasn't on the ground

            internal static bool Active { get; private set; }

            private static readonly List<int> _idx = new List<int>();
            private static readonly List<TrashItem> _items = new List<TrashItem>();
            private static readonly List<Vector3> _start = new List<Vector3>();
            private static readonly List<Quaternion> _startRot = new List<Quaternion>();
            private static readonly int[] _scan = new int[512];
            private static float _elapsed;

            internal static void Begin(int count)
            {
                if (Active) { Core.Log?.Msg("[drift] already running"); return; }
                var tm = Spawning.TrashSpawner.TrashManagerOrNull();
                if (tm == null) { Core.Log?.Warning("[drift] no TrashManager"); return; }
                if (!Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp)) { Core.Log?.Warning("[drift] no player"); return; }
                if (!Ready || _count <= 0) { Core.Log?.Warning("[drift] no instanced field to sample"); return; }

                _idx.Clear(); _items.Clear(); _start.Clear(); _startRot.Clear(); _elapsed = 0f;

                int found = CollectNear(pp, SampleRadius, _scan, _scan.Length);
                if (found == 0) { Core.Log?.Warning("[drift] no settled instances within " + SampleRadius + "m"); return; }
                int want = Mathf.Clamp(count, 1, found);
                int step = Mathf.Max(1, found / want);   // even spread so the sample isn't all one corner/type

                for (int s = 0; s < found && _idx.Count < want; s += step)
                {
                    int i = _scan[s];
                    string id = GetTypeId(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    Vector3 vpos = GetPosition(i);
                    Quaternion vrot = GetRotation(i);
                    TrashItem real = null;
                    Spawning.RouteHook.Suppress = true;
                    try { real = tm.CreateTrashItem(id, vpos, vrot, Vector3.zero, System.Guid.NewGuid().ToString(), false); } // DYNAMIC
                    catch (Exception e) { Core.Log?.Warning("[drift] spawn failed: " + e.Message); }
                    finally { Spawning.RouteHook.Suppress = false; }
                    if (real == null) continue;
                    MarkRealCreated();
                    Hide(i);   // hide the instanced copy so we watch the real dynamic item fall/settle
                    _idx.Add(i); _items.Add(real); _start.Add(vpos); _startRot.Add(vrot);
                }

                if (_idx.Count == 0) { Core.Log?.Warning("[drift] no probes materialized"); return; }
                Active = true;
                Core.Log?.Msg($"[drift] activated {_idx.Count} DYNAMIC probe(s) within {SampleRadius:F0}m; measuring drift over {WaitSeconds:F0}s...");
            }

            internal static void Tick(float dt)
            {
                if (!Active) return;
                _elapsed += dt;
                if (_elapsed < WaitSeconds) return;
                Measure();
                Cleanup();
                Active = false;
            }

            private static void Measure()
            {
                const float LostDist = 5f;   // moved this far = a dynamic probe rolled off / fell through the world; exclude as an outlier, not a grounding signal
                int n = _idx.Count;
                float sumH = 0f, maxH = 0f, sumDrop = 0f, maxDrop = 0f;
                int floated = 0, valid = 0, lost = 0;
                var perType = new Dictionary<string, int[]>();          // id -> [count]
                var perTypeF = new Dictionary<string, float[]>();        // id -> [sumHoriz, sumDrop, maxAbsDrop]
                for (int k = 0; k < n; k++)
                {
                    TrashItem it = _items[k];
                    if (it == null) continue;
                    Vector3 s = _start[k];
                    Vector3 c;
                    try { c = it.transform.position; } catch { continue; }
                    float dx = c.x - s.x, dy = c.y - s.y, dz = c.z - s.z;
                    float total = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (total > LostDist) { lost++; continue; }   // fell through the world / rolled away - not a grounding measurement
                    valid++;
                    float h = Mathf.Sqrt(dx * dx + dz * dz);
                    float drop = -dy;                                    // +down (fell), -up (pushed out of ground)
                    sumH += h; if (h > maxH) maxH = h;
                    sumDrop += drop; if (Mathf.Abs(drop) > Mathf.Abs(maxDrop)) maxDrop = drop;
                    if (total > DriftThreshold) floated++;
                    string id = GetTypeId(_idx[k]) ?? "?";
                    if (!perType.ContainsKey(id)) { perType[id] = new int[1]; perTypeF[id] = new float[3]; }
                    perType[id][0]++;
                    perTypeF[id][0] += h; perTypeF[id][1] += drop;
                    if (Mathf.Abs(drop) > Mathf.Abs(perTypeF[id][2])) perTypeF[id][2] = drop;
                }
                int d = Math.Max(1, valid);
                Core.Log?.Msg($"[drift] RESULT n={valid} (lost {lost})  moved>{DriftThreshold:F2}m: {floated}/{valid}  " +
                    $"horiz mean={sumH / d:F3} max={maxH:F3}  vert(drop+) mean={sumDrop / d:F3} max={maxDrop:F3} (m)");
                foreach (var kv in perType)
                {
                    string id = kv.Key; int cnt = kv.Value[0];
                    float[] f = perTypeF[id];
                    Core.Log?.Msg($"[drift]   {id}: n={cnt} horiz={f[0] / cnt:F3} drop(mean)={f[1] / cnt:F3} drop(maxAbs)={f[2]:F3}");
                }
                Core.Log?.Msg("[drift] read: drop>0 fell (floated above ground); drop<0 pushed up (penetrated); ~0 = resting correctly.");
            }

            private static void Cleanup()
            {
                for (int k = 0; k < _idx.Count; k++)
                {
                    TrashItem it = _items[k];
                    if (it != null)
                    {
                        try { it.DestroyTrash(); } catch { }
                        try { if (it != null) UnityEngine.Object.Destroy(it.gameObject); } catch { }
                    }
                    Restore(_idx[k], _start[k], _startRot[k]);   // put the instanced copy back exactly where it was
                }
                _idx.Clear(); _items.Clear(); _start.Clear(); _startRot.Clear();
            }

            internal static void Abort()
            {
                Cleanup();
                Active = false;
                _elapsed = 0f;
            }
        }
    }
}
