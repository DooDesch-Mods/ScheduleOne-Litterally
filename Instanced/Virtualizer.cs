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
        internal static bool Predict = true;        // anticipate camera turn / player movement and pre-materialize
        // Collide=true (default): materialize DYNAMIC + raycast onto the real ground -> blocks the player + grounded.
        // Collide=false (performance): materialize KINEMATIC frozen at the virtual pose -> seamless + cheapest, but
        // sits at NavMesh height (floats a little) and does NOT collide with the player.
        internal static bool Collide = true;
        internal static float ViewDist = 32f;       // materialize instances this far AHEAD, inside the view frustum
        internal static float BackRadius = 6f;      // ...plus this radius around the player (anti-glitch when turning/backing)
        internal static int MaxReal = 600;          // cap on simultaneous real items - THE perf/range dial (each real item costs ~0.004ms)
        internal static int NewPerFrame = 100;      // materializations/frame - cheap now spawns are kinematic; high so the real layer keeps up with movement (no trailing "loading" wave)
        private const float Margin = 2.5f;          // frustum expansion (m) so items materialize just before on-screen
        private const int DemoteDelayFrames = 20;   // keep an item real this many frames after it leaves view (anti-churn)
        private const float DemoteVel2 = 0.06f;     // only demote once the item has (nearly) stopped moving
        private const float SettleGraceFrames = 10; // min frames a fresh real item must live before it can demote (settle first)

        // CreateTrashItem PARSES this string as a real System.Guid, so it must be valid GUID format.
        private static string NextId() => System.Guid.NewGuid().ToString();

        // ----- predictive look-ahead (extrapolate camera turn + player movement; clamped so a flick can't over-predict) -----
        private const float PredictFrames = 9f;     // how many frames ahead to extrapolate
        private const float MaxPredictAngle = 40f;  // clamp the predicted turn (deg) so an abrupt flick can't sweep the whole map
        private const float MaxPredictMove = 3.5f;  // clamp the predicted forward displacement (m)
        private static Vector3 _lastPlayer;
        private static Quaternion _lastCamRot = Quaternion.identity;
        private static bool _havePrev;

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
        private static readonly float[] _planes = new float[24];      // current frustum planes
        private static readonly float[] _predPlanes = new float[24];  // predicted (extrapolated) frustum planes

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

            Camera cam = Frustum.Cam();
            float[] cur = Frustum.Compute(cam, _planes) ? _planes : null;   // null => no camera: plain ViewDist radius

            // ----- predictive look-ahead: extrapolate player movement + camera turn (clamped so a flick can't
            // over-predict and sweep the whole map). The anti-glitch ring is pushed forward into the movement
            // path; the predicted frustum is rotated toward where the camera is turning, so items "unfreeze"
            // BEFORE they come into view / before you reach them.
            Vector3 predCenter = pp;   // predicted player position for the anti-glitch ring
            float[] pred = cur;        // predicted frustum (defaults to the current frustum)
            if (Predict && cam != null && _havePrev)
            {
                Vector3 move = pp - _lastPlayer;
                predCenter = pp + Vector3.ClampMagnitude(move * PredictFrames, MaxPredictMove);

                Quaternion curRot = cam.transform.rotation;
                Quaternion delta = curRot * Quaternion.Inverse(_lastCamRot);
                delta.ToAngleAxis(out float ang, out Vector3 axis);
                if (ang > 180f) ang -= 360f;   // shortest arc
                if (!float.IsNaN(ang) && !float.IsInfinity(axis.x) && Mathf.Abs(ang) > 0.01f)
                {
                    float predAng = Mathf.Clamp(ang * PredictFrames, -MaxPredictAngle, MaxPredictAngle);
                    if (Mathf.Abs(predAng) > 0.5f)
                    {
                        Quaternion predRot = Quaternion.AngleAxis(predAng, axis) * curRot;
                        Matrix4x4 vp = Frustum.ViewProjection(cam.projectionMatrix, cam.transform.position, predRot);
                        if (Frustum.ComputeFromVP(vp, _predPlanes)) pred = _predPlanes;
                    }
                }
            }
            if (cam != null)
            {
                _lastPlayer = pp;
                _lastCamRot = cam.transform.rotation;
                _havePrev = true;
            }

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
                    float dxb = ip.x - predCenter.x, dzb = ip.z - predCenter.z;
                    float dxv = ip.x - pp.x, dzv = ip.z - pp.z;
                    bool inKeep = (dxb * dxb + dzb * dzb) <= back2 ||
                        ((dxv * dxv + dzv * dzv) <= view2 &&
                         (Frustum.Contains(cur, ip.x, ip.y, ip.z, Margin) || Frustum.Contains(pred, ip.x, ip.y, ip.z, Margin)));
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
            int found = InstancedTrash.CollectVisible(cur, pred, predCenter, pp, BackRadius, ViewDist, Margin, _scan, _scan.Length);
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
                    // The field is already grounded (SampleGround refines NavMesh with a short ground raycast), so
                    // spawn at the EXACT virtual pose - virtual and real coincide (seamless). Collide => DYNAMIC so
                    // the colliders block the player; performance mode => KINEMATIC (cheapest, no player collision).
                    Vector3 pos = InstancedTrash.GetPosition(idx);
                    Quaternion rot = InstancedTrash.GetRotation(idx);
                    TrashItem item = tm.CreateTrashItem(InstancedTrash.GetTypeId(idx), pos, rot, Vector3.zero,
                        NextId(), !Collide);
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
            _havePrev = false;   // forget last-frame camera/player so a respawn/teleport can't seed a huge prediction delta
        }
    }
}
