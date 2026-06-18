using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Spawning
{
    /// <summary>
    /// Applies the per-object physics-optimization levers to a bypass clone at spawn time. Step 1: no-op
    /// (clones fall with the prefab's default physics). Step 2 adds the measured levers (collider swap,
    /// discrete CD, no interpolation, sleep threshold, drag, solver iterations) gated by Preferences toggles.
    /// </summary>
    internal static class OptimizationLevers
    {
        /// <summary>
        /// Configure a freshly-instantiated clone so it actually FALLS. The trash prefab's Rigidbody
        /// defaults to kinematic (the game only enables physics via TrashItem/SetPhysicsActive, which we
        /// stripped) - so without this the clones hang frozen in the air. We force it dynamic + gravity.
        /// Returns the resolved Rigidbody (may live on a child of the root).
        /// </summary>
        private const int TrashLayer = 10;   // game "Trash" layer; renders, and (10,10) is ignored by the game
        private static bool _selfCollisionOff;

        /// <summary>Disable clone-vs-clone collisions: even though the root is on layer 10 (which the game
        /// already ignores against itself), the COLLIDER sits on a child with a different layer, so they were
        /// stacking into a hugely expensive dense pile. Putting the whole hierarchy on layer 10 makes the
        /// collider layer 10 too -> no self-collision -> a cheap flat carpet even when dense. Still lands on
        /// the world (10-vs-world stays enabled). Called once per template.</summary>
        internal static void SetupNoSelfCollision(GameObject template)
        {
            if (!_selfCollisionOff)
            {
                try { Physics.IgnoreLayerCollision(TrashLayer, TrashLayer, true); } catch { }
                _selfCollisionOff = true;
            }
            SetLayerRecursive(template, TrashLayer);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            if (go == null) return;
            try
            {
                go.layer = layer;
                Transform t = go.transform;
                int n = t.childCount;
                for (int i = 0; i < n; i++)
                {
                    SetLayerRecursive(t.GetChild(i).gameObject, layer);
                }
            }
            catch { }
        }

        internal static Rigidbody ConfigureClone(GameObject go, Rigidbody rb)
        {
            if (rb == null && go != null)
            {
                rb = go.GetComponentInChildren<Rigidbody>(true);
            }
            if (rb == null)
            {
                return null;
            }
            try
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.constraints = RigidbodyConstraints.None;   // clear any frozen-position constraint from the prefab

                if (Config.Preferences.OptimizeClones)
                {
                    // Cheap per-body physics levers (from the F2 dump: prefab uses Continuous CCD + Interpolate,
                    // both expensive). The collider simplification is baked into the TEMPLATE once (not here)
                    // to keep per-clone cost minimal. All safe, per-object, never global.
                    try { rb.collisionDetectionMode = CollisionDetectionMode.Discrete; } catch { }
                    try { rb.interpolation = RigidbodyInterpolation.None; } catch { }
                    try { rb.solverIterations = 2; rb.solverVelocityIterations = 1; } catch { }
                    try { rb.sleepThreshold = 0.14f; } catch { }   // sleep after landing, but not so high it sleeps mid-air
                }

                // Kick it downward so it is moving above the sleep threshold at spawn and actually falls
                // (a body spawned at v=0 can immediately re-sleep before gravity accelerates it).
                rb.velocity = new Vector3(0f, -2f, 0f);
                rb.WakeUp();
            }
            catch { }
            return rb;
        }

        /// <summary>Disable the convex MeshCollider(s) (344-vert hull per the F2 dump) and keep the primitive
        /// BoxCollider - much cheaper ground contacts. Only disable if a non-mesh collider remains. Applied
        /// ONCE to each template (clones inherit it) to keep per-clone spawn cost low.</summary>
        internal static void DropMeshColliders(GameObject go)
        {
            try
            {
                Il2CppArrayBase<Collider> cols = go.GetComponentsInChildren<Collider>(true);
                if (cols == null) return;
                bool hasPrimitive = false;
                for (int i = 0; i < cols.Length; i++)
                {
                    if (cols[i] != null && cols[i].TryCast<MeshCollider>() == null) { hasPrimitive = true; break; }
                }
                if (!hasPrimitive) return;   // don't leave it with no collider
                for (int i = 0; i < cols.Length; i++)
                {
                    if (cols[i] != null && cols[i].TryCast<MeshCollider>() != null)
                    {
                        cols[i].enabled = false;
                    }
                }
            }
            catch { }
        }
    }
}
