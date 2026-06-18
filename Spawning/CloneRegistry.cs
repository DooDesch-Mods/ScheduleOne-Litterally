using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Trashville.Spawning
{
    /// <summary>One spawned clone (Strategy B): a raw GameObject we own, plus its cached Rigidbody.</summary>
    internal sealed class Clone
    {
        public GameObject Go;
        public Rigidbody Rb;
        public bool Frozen;   // set when our force-sleep backstop has locked it kinematic
    }

    /// <summary>
    /// Registry for cap-bypass clones (direct Object.Instantiate). These are NOT in TrashManager.trashItems,
    /// so the game's DestroyAllTrash/DestroyTrash never touch them - WE must destroy them via Object.Destroy
    /// on every teardown/save path or 10k objects leak. Rigidbody is cached so the awake-budget check is cheap.
    /// </summary>
    internal static class CloneRegistry
    {
        internal static readonly List<Clone> Clones = new List<Clone>(16000);

        internal static bool EverSpawned { get; private set; }
        internal static int Count => Clones.Count;

        internal static void Add(GameObject go, Rigidbody rb)
        {
            if (go == null)
            {
                return;
            }
            Clones.Add(new Clone { Go = go, Rb = rb });
            EverSpawned = true;
        }

        internal static void Compact()
        {
            for (int i = Clones.Count - 1; i >= 0; i--)
            {
                if (Clones[i].Go == null)
                {
                    Clones.RemoveAt(i);
                }
            }
        }

        /// <summary>Count clones that are actively MOVING (drives the bounded-awake-set stagger lever).
        /// Uses velocity, NOT Rigidbody.IsSleeping() - the latter is unreliable via the il2cpp interop
        /// (returns True even for bodies with clear velocity). speed > ~0.5 m/s == falling.</summary>
        internal static int CountAwake()
        {
            // Only the most-recently-spawned clones can still be moving (they settle within ~1-2s, and we
            // spawn in order). Bounding the scan keeps this O(active) instead of O(10000) every frame, which
            // was a self-inflicted per-frame hitch.
            int n = 0;
            int start = Clones.Count > 3000 ? Clones.Count - 3000 : 0;
            for (int i = start; i < Clones.Count; i++)
            {
                Clone c = Clones[i];
                if (c.Go == null || c.Rb == null || c.Frozen)
                {
                    continue;
                }
                // > ~1.4 m/s = genuinely falling. A lower threshold also counts settled-pile depenetration
                // jitter, which made the stagger over-throttle and the build crawl.
                try { if (c.Rb.velocity.sqrMagnitude > 2f) n++; } catch { }
            }
            return n;
        }

        /// <summary>Diagnostic: log the awake breakdown + a sample clone's physics state (to debug awake=0
        /// and verify the optimization levers actually applied).</summary>
        internal static void DumpDiag()
        {
            Compact();
            int total = Clones.Count, nullGo = 0, nullRb = 0, sleeping = 0, moving = 0;
            Rigidbody sample = null;
            for (int i = 0; i < Clones.Count; i++)
            {
                Clone c = Clones[i];
                if (c.Go == null) { nullGo++; continue; }
                if (c.Rb == null) { nullRb++; continue; }
                try
                {
                    if (sample == null) sample = c.Rb;
                    if (c.Rb.velocity.sqrMagnitude > 0.25f) moving++; else sleeping++;
                }
                catch { }
            }
            Core.Log?.Msg($"[diag] total={total} nullGo={nullGo} nullRb={nullRb} sleeping={sleeping} moving={moving}");
            if (sample != null)
            {
                try
                {
                    Core.Log?.Msg($"[diag] sample rb: kinematic={sample.isKinematic} gravity={sample.useGravity} constraints={sample.constraints} vel={sample.velocity.magnitude:F2} sleeping={sample.IsSleeping()} cd={sample.collisionDetectionMode} interp={sample.interpolation} sleepThr={sample.sleepThreshold} solverIt={sample.solverIterations}");
                    GameObject go = sample.gameObject;
                    bool ignoreSelf = Physics.GetIgnoreLayerCollision(10, 10);
                    Core.Log?.Msg($"[diag] sample '{go.name}' rbGO.layer={go.layer} y={go.transform.position.y:F1} ignore(10,10)={ignoreSelf}");
                    Il2CppArrayBase<Collider> cols = go.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < cols.Length && i < 6; i++)
                    {
                        if (cols[i] == null) continue;
                        string kind = cols[i].TryCast<MeshCollider>() != null ? "Mesh" : (cols[i].TryCast<BoxCollider>() != null ? "Box" : "Other");
                        Core.Log?.Msg($"[diag]   collider[{i}] {kind} enabled={cols[i].enabled} layer={cols[i].gameObject.layer} trigger={cols[i].isTrigger}");
                    }
                }
                catch (Exception e) { Core.Log?.Warning("[diag] sample err: " + e.Message); }
            }
        }

        internal static void DestroyAll()
        {
            for (int i = 0; i < Clones.Count; i++)
            {
                if (Clones[i].Go != null)
                {
                    try { UnityEngine.Object.Destroy(Clones[i].Go); } catch { }
                }
            }
            Clones.Clear();
        }
    }
}
