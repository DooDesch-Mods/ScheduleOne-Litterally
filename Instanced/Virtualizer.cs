using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trashville.Instanced
{
    /// <summary>
    /// Makes the virtual field behave 1:1 like base-game trash WITHOUT paying for 100k GameObjects: each frame it
    /// MATERIALIZES the settled instances the player can actually interact with into real game TrashItems (full
    /// native interaction - E-pickup, the grabber, collision, throw, sell value) and hides their virtual copy;
    /// when they leave that set it demotes them back to virtual at their resting pose; when the player picks one
    /// up (the real TrashItem is destroyed) it removes the virtual permanently.
    ///
    /// The "interactable set" is what the player is LOOKING AT: instances inside the camera view frustum within
    /// ViewDist, PLUS a small BackRadius ring around the player so turning around / walking backwards never
    /// glitches through un-materialized trash. This spends the whole real-item budget on what is on screen
    /// instead of wasting half of it behind the player.
    /// </summary>
    internal static class Virtualizer
    {
        internal static bool Enabled = false;
        internal static float ViewDist = 20f;       // materialize instances this far AHEAD, inside the view frustum
        internal static float BackRadius = 4f;      // ...plus this radius around the player (anti-glitch when turning)
        private const float Margin = 2.5f;          // frustum expansion (m) so items materialize just before on-screen
        private const int DemoteDelayFrames = 20;   // keep an item real this many frames after it leaves view (anti-churn)
        private const int MaxReal = 700;            // safety cap on simultaneous real items
        private const int NewPerFrame = 24;         // throttle materializations/frame so panning never hitches
        private const float DemoteVel2 = 0.06f;     // only demote once the item has (nearly) stopped moving

        private sealed class Real
        {
            public TrashItem Item;
            public Rigidbody Rb;
            public int OutFrames;   // consecutive frames outside the keep-set (hysteresis before demote)
        }

        private static readonly Dictionary<int, Real> _real = new Dictionary<int, Real>();
        private static readonly int[] _scan = new int[4096];
        private static readonly List<int> _remove = new List<int>();
        private static readonly float[] _planes = new float[24];   // 6 world-space frustum planes (nx,ny,nz,d)

        internal static int RealCount => _real.Count;

        internal static void Tick()
        {
            if (!Enabled || InstancedTrash.Count <= 0)
            {
                return;
            }
            var tm = Spawning.TrashSpawner.TrashManagerOrNull();
            if (tm == null || !Spawning.TrashSpawner.TryGetPlayerPosition(out Vector3 pp))
            {
                return;
            }

            float[] planes = ComputePlanes(Camera.main) ? _planes : null;   // null => no camera: fall back to plain ViewDist radius
            float back2 = BackRadius * BackRadius;
            float view2 = ViewDist * ViewDist;

            // 1) Pickup-detect + demote items that have left the interactable set.
            _remove.Clear();
            foreach (var kv in _real)
            {
                int idx = kv.Key;
                Real real = kv.Value;
                TrashItem item = real.Item;
                if (item == null)
                {
                    // real item gone = the player picked it up (or the game removed it) -> kill virtual.
                    InstancedTrash.Kill(idx);
                    _remove.Add(idx);
                    continue;
                }
                try
                {
                    Vector3 ip = item.transform.position;
                    float dx = ip.x - pp.x, dz = ip.z - pp.z;
                    float d2 = dx * dx + dz * dz;
                    bool inKeep = d2 <= back2 || (d2 <= view2 && InstancedTrash.PointInFrustum(ip.x, ip.y, ip.z, planes, Margin));
                    if (inKeep)
                    {
                        real.OutFrames = 0;
                        continue;
                    }
                    real.OutFrames++;
                    // CRITICAL: never demote an airborne/thrown item (it would freeze as a settled virtual mid-air);
                    // and wait a few frames after it leaves view so panning the camera doesn't churn materialize/demote.
                    if (real.OutFrames >= DemoteDelayFrames &&
                        (real.Rb == null || real.Rb.velocity.sqrMagnitude <= DemoteVel2))
                    {
                        InstancedTrash.Restore(idx, ip, item.transform.rotation);
                        item.DestroyTrash();
                        _remove.Add(idx);
                    }
                }
                catch
                {
                    InstancedTrash.Kill(idx);
                    _remove.Add(idx);
                }
            }
            for (int i = 0; i < _remove.Count; i++)
            {
                _real.Remove(_remove[i]);
            }

            // 2) Materialize settled in-view (or behind-ring) instances not real yet. Throttled per frame.
            if (_real.Count >= MaxReal || !InstancedTrash.Ready)
            {
                return;
            }
            int found = InstancedTrash.CollectVisible(planes, pp, BackRadius, ViewDist, Margin, _scan, _scan.Length);
            int made = 0;
            for (int k = 0; k < found && _real.Count < MaxReal && made < NewPerFrame; k++)
            {
                int idx = _scan[k];
                if (_real.ContainsKey(idx))
                {
                    continue;
                }
                try
                {
                    Vector3 pos = InstancedTrash.GetPosition(idx);
                    Quaternion rot = InstancedTrash.GetRotation(idx);
                    TrashItem item = tm.CreateTrashItem(InstancedTrash.GetTypeId(idx), pos, rot, Vector3.zero,
                        System.Guid.NewGuid().ToString(), false);
                    if (item != null)
                    {
                        InstancedTrash.MarkRealCreated();   // a real Saveable TrashItem now exists -> save guard must sweep
                        InstancedTrash.Hide(idx);
                        Rigidbody rb = null;
                        try { rb = item.GetComponentInChildren<Rigidbody>(); } catch { }
                        _real[idx] = new Real { Item = item, Rb = rb, OutFrames = 0 };
                        made++;
                    }
                }
                catch (Exception e)
                {
                    Core.Log?.Warning("[virt] materialize error: " + e.Message);
                }
            }
        }

        /// <summary>Extract the 6 world-space frustum planes from the camera's view-projection (Gribb-Hartmann),
        /// normalized so Margin is in metres. Pure managed maths off two Matrix4x4 reads - no per-point interop.</summary>
        private static bool ComputePlanes(Camera cam)
        {
            if (cam == null) return false;
            try
            {
                Matrix4x4 m = cam.projectionMatrix * cam.worldToCameraMatrix;
                SetPlane(0, m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03); // left
                SetPlane(1, m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03); // right
                SetPlane(2, m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13); // bottom
                SetPlane(3, m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13); // top
                SetPlane(4, m.m30 + m.m20, m.m31 + m.m21, m.m32 + m.m22, m.m33 + m.m23); // near
                SetPlane(5, m.m30 - m.m20, m.m31 - m.m21, m.m32 - m.m22, m.m33 - m.m23); // far
                return true;
            }
            catch { return false; }
        }

        private static void SetPlane(int k, float a, float b, float c, float d)
        {
            float len = Mathf.Sqrt(a * a + b * b + c * c);
            float inv = len > 1e-6f ? 1f / len : 0f;
            int o = k << 2;
            _planes[o] = a * inv; _planes[o + 1] = b * inv; _planes[o + 2] = c * inv; _planes[o + 3] = d * inv;
        }

        /// <summary>Demote every materialized item back to virtual and destroy the real ones (on clear/save/
        /// teardown) so no real TrashItem persists, while the virtual field stays intact + visible.</summary>
        internal static void ClearAll()
        {
            foreach (var kv in _real)
            {
                TrashItem item = kv.Value != null ? kv.Value.Item : null;
                if (item != null)
                {
                    try
                    {
                        InstancedTrash.Restore(kv.Key, item.transform.position, item.transform.rotation);
                        item.DestroyTrash();
                    }
                    catch { InstancedTrash.Kill(kv.Key); }
                }
                else
                {
                    InstancedTrash.Kill(kv.Key);
                }
            }
            _real.Clear();
        }
    }
}
