using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trashville.Instanced
{
    /// <summary>
    /// Makes the virtual 100k behave 1:1 like base-game trash WITHOUT paying for 100k GameObjects: each frame
    /// it MATERIALIZES the settled instances within reach of the player into real game TrashItems (full native
    /// interaction - E-pickup, the grabber, collision, throw, sell value) and hides their virtual copy; when the
    /// player walks away it demotes them back to virtual at their resting pose; when the player picks one up
    /// (the real TrashItem is destroyed) it removes the virtual permanently. Only the items in a generous radius
    /// around the player are ever real, so it stays at instanced-render framerate.
    /// </summary>
    internal static class Virtualizer
    {
        internal static bool Enabled = false;
        internal static float Radius = 14f;           // materialize settled instances within this 2D distance
        private const float DemoteHysteresis = 2f;    // demote only once this far BEYOND Radius (no flicker)
        private const int MaxReal = 600;              // safety cap on simultaneous real items
        private const int NewPerFrame = 24;           // throttle materializations/frame so a big radius doesn't hitch
        private const float DemoteVel2 = 0.06f;       // only demote once the item has (nearly) stopped moving

        private sealed class Real
        {
            public TrashItem Item;
            public Rigidbody Rb;
        }

        private static readonly Dictionary<int, Real> _real = new Dictionary<int, Real>();
        private static readonly int[] _scan = new int[4096];
        private static readonly List<int> _remove = new List<int>();

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

            // 1) Pickup-detect + demote currently-real items.
            _remove.Clear();
            float far = Radius + DemoteHysteresis;
            float far2 = far * far;
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
                    if (dx * dx + dz * dz > far2)
                    {
                        // CRITICAL: only demote an item that has come to REST. A thrown/falling item that
                        // crosses the radius while airborne must stay real until it lands - otherwise we would
                        // freeze it as a settled virtual mid-air at the radius edge.
                        if (real.Rb != null && real.Rb.velocity.sqrMagnitude > DemoteVel2)
                        {
                            continue;
                        }
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

            // 2) Materialize settled in-range instances that are not real yet (CollectNear skips hidden/dead).
            //    Throttled per frame so enabling a large radius ramps up smoothly instead of hitching.
            if (_real.Count >= MaxReal || !InstancedTrash.Ready)
            {
                return;
            }
            int found = InstancedTrash.CollectNear(pp, Radius, _scan, _scan.Length);
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
                        _real[idx] = new Real { Item = item, Rb = rb };
                        made++;
                    }
                }
                catch (Exception e)
                {
                    Core.Log?.Warning("[virt] materialize error: " + e.Message);
                }
            }
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
