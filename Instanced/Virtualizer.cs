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
        // ON by default: whenever an instanced field exists, materialize real (interactable) items near the player
        // so trash can always be picked up/thrown out of the box. A no-op when there is no field. (`tv real off`
        // disables it for a pure-render benchmark.)
        internal static bool Enabled = true;
        // Research finding: Schedule I trash never blocks the player (player is a CharacterController; trash
        // colliders are non-blocking / walkable-through BY DESIGN), so DYNAMIC buys no collision - it only makes
        // fresh items visibly settle/jiggle. So default = KINEMATIC (frozen at the grounded virtual pose: seamless,
        // no fall/jiggle, cheapest). 'tv collide on' = DYNAMIC physics (items react/can be shoved) if ever wanted.
        internal static bool Collide = false;
        internal static float ViewDist = 32f;       // MAX interactable radius (the adaptive _matRadius never grows past this)
        internal static int MaxReal = 600;          // cap on simultaneous real items - THE perf/range dial (each real item costs ~0.004ms)
        internal static int NewPerFrame = 100;      // materializations/frame - cheap now spawns are kinematic; high so the real layer keeps up with movement (no trailing "loading" wave)

        // ----- nearest-first interactable set -----
        // The MaxReal real items are always the instances NEAREST the player (what you can actually reach), not
        // whatever the camera happens to frame. _matRadius adapts each tick so ~MaxReal instances fall inside it:
        // it shrinks where trash is dense and grows (up to ViewDist) where it is sparse - so the real items track
        // the closest trash around the player, 360 degrees, regardless of where the camera looks.
        internal static float MatRadius => _matRadius;
        private static float _matRadius = 32f;
        private const float MinMatRadius = 3f;       // floor for very dense areas
        private const float RadiusShrink = 0.95f;    // per-tick step when too many instances are in range
        private const float RadiusGrow = 1.04f;      // per-tick step when there is spare budget
        private const int DemoteDelayFrames = 20;   // keep an item real this many frames after it leaves view (anti-churn)
        private const float DemoteVel2 = 0.06f;     // only demote once the item has (nearly) stopped moving
        private const float SettleGraceFrames = 10; // min frames a fresh real item must live before it can demote (settle first)
        private const float DivergePos2 = 0.0025f;  // (0.05 m)^2: once a materialized item moves this far from its rest pose it has been grabbed/thrown -> reveal its real renderer

        // CreateTrashItem PARSES this string as a real System.Guid, so it must be valid GUID format.
        private static string NextId() => System.Guid.NewGuid().ToString();

        private sealed class Real
        {
            public TrashItem Item;
            public Rigidbody Rb;
            public Il2CppArrayBase<Renderer> Rends;   // the real item's renderers (kept OFF while the instanced copy draws it)
            public bool Diverged;   // true once the item left its rest pose (grabbed/thrown) -> its real renderer is shown, the instance hidden
            public int OutFrames;   // consecutive frames outside the keep-set (hysteresis before demote)
            public int Age;         // frames since materialized (let it settle before it may be frozen back)
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
            var tm = Spawning.GameTrash.TrashManagerOrNull();
            if (tm == null || !Spawning.GameTrash.TryGetPlayerPosition(out Vector3 pp))
            {
                return;
            }

            // Interactable set = the NEAREST instances to the player (so you can always grab/throw the trash right
            // around you, whatever the camera frames). _matRadius adapts further down so ~MaxReal instances fit.
            float keepR = _matRadius + 1.5f;   // small hysteresis so an item hovering at the edge doesn't churn
            float keep2 = keepR * keepR;

            // 1) Pickup-detect + demote items that have left the interactable set (now the FARTHEST, since the
            //    radius tracks the nearest ~MaxReal).
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

                    // Render-decouple handoff: while the real item rests at its virtual pose the INSTANCED copy draws
                    // it (identical shading, so merely looking at it never changes its appearance). The instant it
                    // diverges - grabbed, thrown or shoved - reveal its real renderer and hide the instance so the
                    // moving item is what you see. Latched: once revealed it stays real until it demotes / is picked up.
                    if (!real.Diverged)
                    {
                        Vector3 vp = InstancedTrash.GetPosition(idx);
                        float mdx = ip.x - vp.x, mdy = ip.y - vp.y, mdz = ip.z - vp.z;
                        if ((mdx * mdx + mdy * mdy + mdz * mdz) > DivergePos2 ||
                            (real.Rb != null && real.Rb.velocity.sqrMagnitude > DemoteVel2))
                        {
                            real.Diverged = true;
                            if (real.Rends != null)
                            {
                                for (int r = 0; r < real.Rends.Length; r++)
                                {
                                    if (real.Rends[r] != null) real.Rends[r].enabled = true;
                                }
                            }
                            InstancedTrash.Hide(idx);
                        }
                    }

                    float dxv = ip.x - pp.x, dzv = ip.z - pp.z;
                    if ((dxv * dxv + dzv * dzv) <= keep2)   // still among the nearest -> keep it real
                    {
                        real.OutFrames = 0;
                        continue;
                    }
                    real.OutFrames++;
                    // CRITICAL: never demote an airborne/thrown item (it would freeze as a settled virtual mid-air);
                    // give a fresh item time to settle first; and wait a few frames after it leaves the radius so a
                    // small wobble at the edge doesn't churn materialize/demote.
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
            if (!InstancedTrash.Ready)
            {
                return;
            }
            // 2) Materialize the NEAREST not-yet-real instances within the current radius (throttled per frame), and
            //    count how many settled instances fall in the radius so we can adapt it toward ~MaxReal.
            int collectMax = _real.Count < MaxReal ? _scan.Length : 0;
            int found = InstancedTrash.CollectWithinRadius(pp, _matRadius, _scan, collectMax, out int inRadius);
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
                    // Suppress so the route-hook never re-absorbs our OWN near-player interaction items.
                    Spawning.RouteHook.Suppress = true;
                    TrashItem item;
                    try { item = tm.CreateTrashItem(InstancedTrash.GetTypeId(idx), pos, rot, Vector3.zero, NextId(), !Collide); }
                    finally { Spawning.RouteHook.Suppress = false; }
                    if (item != null)
                    {
                        InstancedTrash.MarkRealCreated();   // a real Saveable TrashItem now exists -> save guard must sweep
                        // Render-decouple: claim the index (so it is not re-materialized) but DO NOT hide the instance -
                        // the real item exists purely for interaction (collider/pickup/throw) with its renderer OFF, so
                        // the instanced copy keeps providing the visuals at the exact same pose (no shading swap on
                        // look). The instance is only hidden once the item diverges (see the handoff above).
                        InstancedTrash.SetRealized(idx, true);
                        Il2CppArrayBase<Renderer> rends = null;
                        try
                        {
                            rends = item.GetComponentsInChildren<Renderer>(true);
                            if (rends != null)
                            {
                                for (int r = 0; r < rends.Length; r++)
                                {
                                    if (rends[r] == null) continue;
                                    // Match the instanced field's shadow setting (no shadow-pop on the eventual reveal,
                                    // and no extra shadow pass while it is hidden).
                                    if (!InstancedTrash.Shadows) rends[r].shadowCastingMode = ShadowCastingMode.Off;
                                    rends[r].enabled = false;   // invisible until the item diverges; the instance draws it
                                }
                            }
                        }
                        catch { }
                        Rigidbody rb = null;
                        try { rb = item.GetComponentInChildren<Rigidbody>(); } catch { }
                        _real[idx] = new Real { Item = item, Rb = rb, Rends = rends, Diverged = false, OutFrames = 0, Age = 0 };
                        made++;
                    }
                }
                catch (Exception e)
                {
                    Core.Log?.Warning("[virt] materialize error: " + e.Message);
                }
            }

            // 3) Adapt the radius so the interactable set stays the NEAREST ~MaxReal instances: shrink where trash
            //    is dense (over budget), grow (up to ViewDist) where it is sparse. A deadband below MaxReal holds
            //    the radius steady once it fits (no per-frame random-walk / boundary churn).
            if (inRadius > MaxReal) _matRadius = Mathf.Max(MinMatRadius, _matRadius * RadiusShrink);
            else if (inRadius < MaxReal * 0.9f) _matRadius = Mathf.Min(ViewDist, _matRadius * RadiusGrow);
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
            _matRadius = ViewDist;   // reset the adaptive radius so a teleport/respawn re-converges from the max
        }
    }
}
