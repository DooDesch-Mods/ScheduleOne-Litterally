using System;
using System.Text;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Litterally.Spawning;

namespace Litterally.Profiling
{
    /// <summary>
    /// Dumps the ACTUAL runtime physics config of a real game trash item + the global Physics settings,
    /// so Phase-2 optimization levers are chosen from ground truth (which knobs have headroom), not guesses.
    /// Trigger: F2. Spawns one item if none exist, then logs everything.
    /// </summary>
    internal static class PhysicsConfigDump
    {
        internal static void Dump()
        {
            try
            {
                TrashRegistry.Compact();
                TrashItem item = null;
                foreach (TrashItem t in TrashRegistry.Spawned)
                {
                    if (t != null) { item = t; break; }
                }

                var sb = new StringBuilder(1024);
                sb.AppendLine("[PhysCfg] ===== Trash physics config dump =====");

                DumpGlobal(sb);

                if (item == null)
                {
                    sb.AppendLine("[PhysCfg] No live trash item to inspect - spawn some first (F7), then press F2 again.");
                    Flush(sb);
                    return;
                }

                DumpItem(sb, item);
                sb.AppendLine("[PhysCfg] =====================================");
                Flush(sb);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[PhysCfg] dump failed: " + e.Message);
            }
        }

        private static void DumpGlobal(StringBuilder sb)
        {
            try { sb.AppendLine($"[PhysCfg] GLOBAL  gravity={Physics.gravity}  fixedDeltaTime={Time.fixedDeltaTime}"); } catch { }
            try { sb.AppendLine($"[PhysCfg] GLOBAL  defaultSolverIterations={Physics.defaultSolverIterations}  defaultSolverVelocityIterations={Physics.defaultSolverVelocityIterations}"); } catch { }
            try { sb.AppendLine($"[PhysCfg] GLOBAL  sleepThreshold={Physics.sleepThreshold}  defaultContactOffset={Physics.defaultContactOffset}  bounceThreshold={Physics.bounceThreshold}"); } catch { }
            try { sb.AppendLine($"[PhysCfg] GLOBAL  defaultMaxAngularSpeed={Physics.defaultMaxAngularSpeed}  defaultMaxDepenetrationVelocity={Physics.defaultMaxDepenetrationVelocity}"); } catch { }
            try { sb.AppendLine($"[PhysCfg] GLOBAL  simulationMode={Physics.simulationMode}"); } catch { try { sb.AppendLine($"[PhysCfg] GLOBAL  autoSimulation={Physics.autoSimulation}"); } catch { } }
        }

        private static void DumpItem(StringBuilder sb, TrashItem item)
        {
            GameObject go = null;
            try { go = item.Rigidbody != null ? item.Rigidbody.gameObject : item.gameObject; } catch { try { go = item.gameObject; } catch { } }
            if (go == null) { sb.AppendLine("[PhysCfg] item gameObject null"); return; }

            try { sb.AppendLine($"[PhysCfg] ITEM '{go.name}'  layer={go.layer} ({LayerMask.LayerToName(go.layer)})  selfCollide={!Physics.GetIgnoreLayerCollision(go.layer, go.layer)}"); } catch { }

            try
            {
                Rigidbody rb = item.Rigidbody;
                if (rb != null)
                {
                    sb.AppendLine($"[PhysCfg] RB  mass={rb.mass}  drag={rb.drag}  angularDrag={rb.angularDrag}  useGravity={rb.useGravity}  isKinematic={rb.isKinematic}");
                    sb.AppendLine($"[PhysCfg] RB  collisionDetection={rb.collisionDetectionMode}  interpolation={rb.interpolation}  constraints={rb.constraints}");
                    sb.AppendLine($"[PhysCfg] RB  sleepThreshold={rb.sleepThreshold}  solverIterations={rb.solverIterations}  solverVelocityIterations={rb.solverVelocityIterations}  maxAngularVelocity={rb.maxAngularVelocity}");
                }
                else
                {
                    sb.AppendLine("[PhysCfg] RB  (none)");
                }
            }
            catch (Exception e) { sb.AppendLine("[PhysCfg] RB read error: " + e.Message); }

            try
            {
                Il2CppArrayBase<Collider> cols = go.GetComponentsInChildren<Collider>(true);
                int n = cols != null ? cols.Length : 0;
                sb.AppendLine($"[PhysCfg] COLLIDERS count={n}");
                for (int i = 0; i < n && i < 12; i++)
                {
                    Collider c = cols[i];
                    if (c == null) continue;
                    string kind = ColliderKind(c);
                    string extra = "";
                    try { var mc = c.TryCast<MeshCollider>(); if (mc != null) extra = $" convex={mc.convex} sharedMeshVerts={(mc.sharedMesh != null ? mc.sharedMesh.vertexCount : -1)}"; } catch { }
                    sb.AppendLine($"[PhysCfg]   [{i}] {kind}  isTrigger={c.isTrigger}  enabled={c.enabled}{extra}");
                }
            }
            catch (Exception e) { sb.AppendLine("[PhysCfg] colliders read error: " + e.Message); }

            try
            {
                int rbCount = go.GetComponentsInChildren<Rigidbody>(true)?.Length ?? 0;
                int rendCount = go.GetComponentsInChildren<Renderer>(true)?.Length ?? 0;
                int mbCount = go.GetComponentsInChildren<MonoBehaviour>(true)?.Length ?? 0;
                sb.AppendLine($"[PhysCfg] COMPONENTS rigidbodies={rbCount} renderers={rendCount} monoBehaviours={mbCount}");
            }
            catch { }
        }

        private static string ColliderKind(Collider c)
        {
            try { if (c.TryCast<BoxCollider>() != null) return "BoxCollider"; } catch { }
            try { if (c.TryCast<SphereCollider>() != null) return "SphereCollider"; } catch { }
            try { if (c.TryCast<CapsuleCollider>() != null) return "CapsuleCollider"; } catch { }
            try { if (c.TryCast<MeshCollider>() != null) return "MeshCollider"; } catch { }
            return "Collider(other)";
        }

        private static void Flush(StringBuilder sb)
        {
            string s = sb.ToString();
            Core.Log?.Msg(s);
            DiagLog.Note(s);
        }
    }
}
