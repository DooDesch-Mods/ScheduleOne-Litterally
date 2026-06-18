using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

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
        internal static float ViewDist = 32f;       // materialize instances this far AHEAD, inside the view frustum
        internal static float BackRadius = 5f;      // ...plus this radius around the player (anti-glitch when turning)
        internal static int MaxReal = 600;          // cap on simultaneous real items - THE perf/range dial (each real item costs ~0.004ms)
        private const float Margin = 2.5f;          // frustum expansion (m) so items materialize just before on-screen
        private const int DemoteDelayFrames = 20;   // keep an item real this many frames after it leaves view (anti-churn)
        private const int NewPerFrame = 20;         // throttle materializations/frame so panning never hitches
        private const float DemoteVel2 = 0.06f;     // only demote once the item has (nearly) stopped moving
        private const float SettleGraceFrames = 10; // min frames a fresh real item must live before it can demote (settle first)
        private static readonly int _groundMask = ~(1 << 10);   // raycast everything EXCEPT the Trash layer (10) for placement

        private sealed class Real
        {
            public TrashItem Item;
            public Rigidbody Rb;
            public int OutFrames;   // consecutive frames outside the keep-set (hysteresis before demote)
            public int Age;         // frames since materialized (let it settle before it may be frozen back)
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

            float[] planes = Frustum.Compute(Camera.main, _planes) ? _planes : null;   // null => no camera: plain ViewDist radius
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
                real.Age++;
                try
                {
                    Vector3 ip = item.transform.position;
                    float dx = ip.x - pp.x, dz = ip.z - pp.z;
                    float d2 = dx * dx + dz * dz;
                    bool inKeep = d2 <= back2 || (d2 <= view2 && Frustum.Contains(planes, ip.x, ip.y, ip.z, Margin));
                    if (inKeep)
                    {
                        real.OutFrames = 0;
                        continue;
                    }
                    real.OutFrames++;
                    // CRITICAL: never demote an airborne/thrown item (it would freeze as a settled virtual mid-air);
                    // give a fresh item time to settle first; and wait a few frames after it leaves view so panning
                    // the camera doesn't churn materialize/demote.
                    if (real.OutFrames >= DemoteDelayFrames && real.Age >= SettleGraceFrames &&
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
                    // Place precisely on the REAL collision ground (raycast, excluding the Trash layer) so the item
                    // appears already settled instead of dropping/jiggling into place ("fresh fall").
                    try
                    {
                        if (Physics.Raycast(new Vector3(pos.x, pos.y + 1.5f, pos.z), Vector3.down,
                                out RaycastHit hit, 8f, _groundMask, QueryTriggerInteraction.Ignore))
                        {
                            pos.y = hit.point.y + InstancedTrash.GetClearance(idx);
                        }
                    }
                    catch { }
                    TrashItem item = tm.CreateTrashItem(InstancedTrash.GetTypeId(idx), pos, rot, Vector3.zero,
                        System.Guid.NewGuid().ToString(), false);
                    if (item != null)
                    {
                        InstancedTrash.MarkRealCreated();   // a real Saveable TrashItem now exists -> save guard must sweep
                        InstancedTrash.Hide(idx);
                        // Match the instanced field's shadow setting so there is no shadow-pop on materialize and
                        // we don't pay an extra shadow pass for hundreds of real items when the field has shadows off.
                        if (!InstancedTrash.Shadows)
                        {
                            try
                            {
                                Il2CppArrayBase<Renderer> rends = item.GetComponentsInChildren<Renderer>(true);
                                if (rends != null)
                                {
                                    for (int r = 0; r < rends.Length; r++)
                                    {
                                        if (rends[r] != null) rends[r].shadowCastingMode = ShadowCastingMode.Off;
                                    }
                                }
                            }
                            catch { }
                        }
                        Rigidbody rb = null;
                        try { rb = item.GetComponentInChildren<Rigidbody>(); } catch { }
                        _real[idx] = new Real { Item = item, Rb = rb, OutFrames = 0, Age = 0 };
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
