using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Audio;   // RBImpactSounds

namespace Trashville.Spawning
{
    /// <summary>
    /// Batched trash spawner. Discovers the real prefab ids from TrashManager.TrashPrefabs,
    /// raises TRASH_ITEM_LIMIT so the manager will not silently drop our spawns, and pumps a
    /// bounded number of spawns per frame (adaptively throttled) so a 10k request does not
    /// freeze the game in one frame.
    /// </summary>
    internal static class TrashSpawner
    {
        private static readonly List<string> _prefabIds = new List<string>();
        private static int _pending;
        private static int _sinceLog;
        private static int _consecutiveNulls;
        private static System.Random _rng = new System.Random(1234);

        internal static int Pending => _pending;
        internal static bool IsBusy => _pending > 0;

        /// <summary>Queue N items to spawn around the player over the coming frames.</summary>
        internal static void RequestSpawn(int count)
        {
            if (!Config.Preferences.ArmBenchmark)
            {
                Core.Log?.Warning("[Spawn] Ignored - benchmark is DISARMED. Arm it first (F5 or the ArmBenchmark toggle).");
                return;
            }
            if (count <= 0)
            {
                return;
            }

            DiagLog.Note($"REQUEST count={count} kinematic={Config.Preferences.SpawnKinematic} mute={Config.Preferences.MuteImpactSounds} perFrame={Config.Preferences.SpawnPerFrame}");

            var tm = TrashManagerOrNull();
            if (tm == null)
            {
                Core.Log?.Warning("[Spawn] TrashManager not available yet (load a save and be in the world first).");
                DiagLog.Note("ABORT: TrashManager null");
                return;
            }

            EnsurePrefabIds(tm);
            DiagLog.Note($"discovered prefabIds={_prefabIds.Count}");
            if (_prefabIds.Count == 0)
            {
                Core.Log?.Warning("[Spawn] No trash prefab ids discovered - cannot spawn.");
                DiagLog.Note("ABORT: no prefab ids");
                return;
            }

            // Phase 2 cap-bypass (Strategy B): spawn direct clones that never touch TrashManager, so the
            // 2000 cap / eviction does not apply. No clamp - queue the full count.
            if (Config.Preferences.BypassCap)
            {
                _consecutiveNulls = 0;
                _pending += count;
                Core.Log?.Msg($"[Spawn] Queued {count} CLONES (pending {_pending}). Cap BYPASSED. Mode: {(Config.Preferences.SpawnKinematic ? "kinematic" : "dynamic")}.");
                DiagLog.Note($"BYPASS QUEUED {count}, pending={_pending}");
                return;
            }

            // The game hard-caps LIVE trash at the const TRASH_ITEM_LIMIT (=2000) and evicts the oldest
            // when you create more - that churn is the "trash keeps despawning". So we cap our request to
            // fit under the limit: a stable pile, no eviction. Exceeding 2000 needs a Phase-2 limit bypass.
            int limit = SafeLimit();
            int existing = TrashItemCount(tm);
            // Account for items already queued (_pending) so repeated F9 presses don't over-queue past the cap.
            int room = limit > 0 ? Math.Max(0, limit - existing - _pending - 16) : count;
            int toSpawn = Math.Min(count, room);
            DiagLog.Note($"limit={limit} existing={existing} room={room} requested={count} -> spawning={toSpawn}");

            if (toSpawn <= 0)
            {
                Core.Log?.Warning($"[Spawn] Already at the game's trash limit ({limit}); existing={existing}. Purge first (Shift+F10) to make room.");
                return;
            }
            if (toSpawn < count)
            {
                Core.Log?.Warning($"[Spawn] Game caps LIVE trash at {limit} (existing {existing}). Spawning {toSpawn}; more would just evict older trash. 10000 needs a TRASH_ITEM_LIMIT bypass (Phase 2).");
            }

            _consecutiveNulls = 0;
            _pending += toSpawn;
            Core.Log?.Msg($"[Spawn] Queued {toSpawn} (pending now {_pending}). Mode: {(Config.Preferences.SpawnKinematic ? "kinematic" : "dynamic")}.");
            DiagLog.Note($"QUEUED {toSpawn}, pending={_pending}, limit={limit}, mgrTrash={existing}");
        }

        /// <summary>Pumped every frame from Core.OnUpdate. Spawns a bounded batch.</summary>
        internal static void Tick()
        {
            if (_pending <= 0)
            {
                return;
            }

            var tm = TrashManagerOrNull();
            if (tm == null)
            {
                return;
            }

            if (!TryGetPlayerPosition(out Vector3 center))
            {
                return;   // wait until the player exists
            }

            // Drop the bypass rain a few metres in front of where the player is looking, so it falls into view.
            if (Config.Preferences.BypassCap && !Config.Preferences.SpawnKinematic)
            {
                center += PlayerForwardFlat() * 8f;
            }


            // Staggered awake-budget lever (the key one): while too many clones are already falling, hold
            // new spawns until earlier ones land and sleep. Bounds the active set -> fps stays high while
            // building up to 10000 piled+asleep. Cheap once most are asleep is the concern, so it's gated
            // behind BypassCap (clones only) and a configurable budget.
            if (Config.Preferences.BypassCap && Config.Preferences.MaxAwakeBudget > 0)
            {
                if (CloneRegistry.CountAwake() >= Config.Preferences.MaxAwakeBudget)
                {
                    return;   // hold this frame; resume next frame as the airborne ones settle
                }
            }

            // Adaptive throttle: if the last frame was already heavy, spawn fewer this frame.
            int budget = Config.Preferences.SpawnPerFrame;
            float lastMs = Time.unscaledDeltaTime * 1000f;
            if (lastMs > 25f)
            {
                budget = Math.Max(3, budget / 4);
            }

            int done = 0;
            for (int i = 0; i < budget && _pending > 0; i++)
            {
                if (TrySpawnOne(tm, center))
                {
                    done++;
                    _consecutiveNulls = 0;
                }
                else
                {
                    _consecutiveNulls++;
                }
                _pending--;

                // Persistent failure detection: capped path = the game refused (cap hit); bypass = clone error.
                if (_consecutiveNulls >= 150)
                {
                    int reg = RegCount();
                    _pending = 0;
                    if (Config.Preferences.BypassCap)
                    {
                        Core.Log?.Warning($"[Spawn] Clone spawning is failing repeatedly at clones={reg} - aborting (see earlier [Clone] errors).");
                        DiagLog.Note($"BYPASS ABORT persistent clone failure at clones={reg}");
                    }
                    else if (reg == 0)
                    {
                        Core.Log?.Warning("[Spawn] CreateTrashItem returned null repeatedly with nothing registered - are you the host? Aborting.");
                        DiagLog.Note("ABORT: persistent null, nothing registered (not host?)");
                    }
                    else
                    {
                        Core.Log?.Warning($"[Spawn] Reached the game's trash cap at registered={reg} (mgr={TrashItemCount(tm)}, limit={SafeLimit()}). Higher counts need BypassCap.");
                        DiagLog.Note($"CAP HIT registered={reg} mgr={TrashItemCount(tm)} limit={SafeLimit()}");
                    }
                    break;
                }
            }

            // Periodic flushed checkpoint so a hard crash still leaves a trail of how far we got.
            _sinceLog += done;
            if (_sinceLog >= 250)
            {
                _sinceLog = 0;
                Core.Log?.Msg($"[Spawn] progress: registered {RegCount()}, pending {_pending}.");
            }

            if (_pending == 0)
            {
                if (Config.Preferences.BypassCap)
                {
                    CloneRegistry.Compact();
                    Core.Log?.Msg($"[Spawn] Batch complete (BYPASS). Clones: {CloneRegistry.Count} (game cap not involved).");
                    DiagLog.Note($"BATCH COMPLETE bypass clones={CloneRegistry.Count}");
                    return;
                }
                DiagLog.Note($"BATCH spawn loop done, registered={TrashRegistry.Count} mgr={TrashItemCount(tm)} - starting global mute sweep");
                if (Config.Preferences.MuteImpactSounds)
                {
                    int muted = MuteAllImpactsGlobal();
                    Core.Log?.Msg($"[Spawn] Global impact-sound sweep disabled {muted} RBImpactSounds.");
                    DiagLog.Note($"global mute sweep disabled {muted}");
                }
                TrashRegistry.Compact();   // drop any refs the game evicted, so measurements see only live trash
                Core.Log?.Msg($"[Spawn] Batch complete. Live registered: {TrashRegistry.Count}. Manager trashItems: {TrashItemCount(tm)}.");
                DiagLog.Note($"BATCH COMPLETE liveRegistered={TrashRegistry.Count} mgr={TrashItemCount(tm)}");
            }
        }

        /// <summary>Active registry count for the current mode (clones when bypassing, else game trash).</summary>
        internal static int RegCount() => Config.Preferences.BypassCap ? CloneRegistry.Count : TrashRegistry.Count;

        private static bool TrySpawnOne(TrashManager tm, Vector3 center)
        {
            try
            {
                string id = _prefabIds[_rng.Next(_prefabIds.Count)];
                Vector3 pos = RandomSpawnPoint(center);
                Quaternion rot = UnityEngine.Random.rotationUniform;

                // Cap-bypass path: our own clone, no TrashManager, no cap.
                if (Config.Preferences.BypassCap)
                {
                    return CloneSpawner.TrySpawn(tm, id, pos, rot);
                }

                string guid = System.Guid.NewGuid().ToString();
                RouteHook.Suppress = true;
                TrashItem item;
                try { item = tm.CreateTrashItem(id, pos, rot, Vector3.zero, guid, Config.Preferences.SpawnKinematic); }
                finally { RouteHook.Suppress = false; }
                if (item != null)
                {
                    if (Config.Preferences.MuteImpactSounds)
                    {
                        MuteImpacts(item);
                    }
                    TrashRegistry.Add(item, guid);
                    return true;
                }
                // Mark that we tried, so save-safety still treats the world as dirtied.
                TrashRegistry.Guids.Add(guid);
                return false;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Spawn] CreateTrashItem threw: " + e.Message);
                DiagLog.Note($"EXC during spawn at registered={TrashRegistry.Count}: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static int SafeLimit()
        {
            try { return TrashManager.TRASH_ITEM_LIMIT; } catch { return -1; }
        }

        private static Vector3 RandomSpawnPoint(Vector3 center)
        {
            // Bypass clones rain over a WIDE disc so the (self-colliding) pile stays thin - a dense pile of
            // 10k is very expensive (contact solving), a thin wide field is cheap.
            float radius = Config.Preferences.BypassCap ? 24f : Mathf.Max(1f, Config.Preferences.SpawnRadius);
            // Random point in a disc around the player, dropped from a small height so the pile settles.
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            double r = Math.Sqrt(_rng.NextDouble()) * radius;
            float x = center.x + (float)(Math.Cos(ang) * r);
            float z = center.z + (float)(Math.Sin(ang) * r);

            if (!Config.Preferences.SpawnKinematic)
            {
                // Dynamic: drop from a height, gravity makes it rain down. Bypass clones get a tall column
                // so 10k visibly rains over a few seconds instead of appearing in a thin band.
                float h = Config.Preferences.BypassCap
                    ? 8f + (float)(_rng.NextDouble() * 10.0)
                    : Config.Preferences.SpawnHeight + (float)(_rng.NextDouble() * 2.0);
                return new Vector3(x, center.y + h, z);
            }

            // Kinematic: rest on the ground. Raycast down to find the floor at this XZ; reject hits above
            // the player's feet (those are the player or already-spawned trash) and fall back to feet level.
            float groundY = center.y;
            if (Physics.Raycast(new Vector3(x, center.y + 4f, z), Vector3.down, out RaycastHit hit, 40f)
                && hit.point.y <= center.y + 0.5f)
            {
                groundY = hit.point.y;
            }
            float y = groundY + 0.1f + (float)(_rng.NextDouble() * 0.4);
            return new Vector3(x, y, z);
        }

        private static int _muteDiag;

        /// <summary>
        /// Disable the per-collision impact-sound component on a spawned trash item. The game's
        /// RBImpactSounds fires SFXManager.PlayImpactSound on every collision; with many trash
        /// colliding the audio pool is exhausted and the game spams thousands of warning logs per
        /// frame and CRASHES. (It is itself a top perf killer - see the crash evidence.)
        /// RBImpactSounds lives on the RIGIDBODY's GameObject (where Unity routes OnCollisionEnter),
        /// which may be a parent of the TrashItem - so we target the rigidbody GO and look up + down.
        /// </summary>
        private static void MuteImpacts(TrashItem item)
        {
            try
            {
                Rigidbody rb = item.Rigidbody;
                GameObject go = rb != null ? rb.gameObject : item.gameObject;
                if (go == null)
                {
                    return;
                }
                int n = DisableImpactsOn(go);
                if (_muteDiag < 3)
                {
                    _muteDiag++;
                    Core.Log?.Msg($"[Spawn] mute-diag: '{go.name}' rb={(rb != null)} disabled {n} RBImpactSounds.");
                }
            }
            catch (Exception e)
            {
                if (_muteDiag < 3)
                {
                    _muteDiag++;
                    Core.Log?.Warning("[Spawn] mute error: " + e.Message);
                }
            }
        }

        private static int DisableImpactsOn(GameObject go)
        {
            int n = 0;
            Il2CppArrayBase<RBImpactSounds> arr = go.GetComponentsInChildren<RBImpactSounds>(true);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] != null) { arr[i].enabled = false; n++; }
                }
            }
            RBImpactSounds par = go.GetComponentInParent<RBImpactSounds>();
            if (par != null) { par.enabled = false; n++; }
            return n;
        }

        /// <summary>
        /// Belt-and-suspenders global sweep: disable EVERY RBImpactSounds in the scene. Run once per
        /// spawn batch so any item whose component our per-item lookup missed is still silenced.
        /// </summary>
        internal static int MuteAllImpactsGlobal()
        {
            int n = 0;
            try
            {
                Il2CppArrayBase<RBImpactSounds> all = UnityEngine.Object.FindObjectsOfType<RBImpactSounds>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].enabled) { all[i].enabled = false; n++; }
                    }
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Spawn] global mute error: " + e.Message);
            }
            return n;
        }

        // ----- prefab id discovery -----

        private static void EnsurePrefabIds(TrashManager tm)
        {
            if (_prefabIds.Count > 0)
            {
                return;
            }
            try
            {
                Il2CppReferenceArray<TrashItem> prefabs = tm.TrashPrefabs;
                if (prefabs != null)
                {
                    for (int i = 0; i < prefabs.Length; i++)
                    {
                        TrashItem p = prefabs[i];
                        if (p == null)
                        {
                            continue;
                        }
                        string id = p.ID;
                        if (!string.IsNullOrEmpty(id) && !_prefabIds.Contains(id))
                        {
                            _prefabIds.Add(id);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Spawn] Failed reading TrashPrefabs: " + e.Message);
            }

            if (_prefabIds.Count == 0)
            {
                // Fallback: sample the random-generatable prefab a few times to learn some ids.
                try
                {
                    for (int i = 0; i < 8; i++)
                    {
                        TrashItem p = tm.GetRandomGeneratableTrashPrefab();
                        if (p != null && !string.IsNullOrEmpty(p.ID) && !_prefabIds.Contains(p.ID))
                        {
                            _prefabIds.Add(p.ID);
                        }
                    }
                }
                catch { /* best effort */ }
            }

            if (_prefabIds.Count > 0)
            {
                Core.Log?.Msg("[Spawn] Discovered trash prefab ids: " + string.Join(", ", _prefabIds));
            }
        }

        // ----- limit handling -----
        //
        // ROOT CAUSE of the crashes: TrashManager.TRASH_ITEM_LIMIT is a game `const` (= 2000), NOT a
        // writable field. The IL2CPP interop exposes a setter, but writing a const via
        // il2cpp_field_static_set_value writes to invalid memory => hard native crash. So we NEVER write
        // it (nor TRASH_REPLICATIONS_PER_SECOND). We only read it, and detect if the game caps spawns.

        /// <summary>No-op: we never modify the game's (const) trash limit, so nothing to restore.</summary>
        internal static void RestoreLimit()
        {
        }

        internal static void CancelPending()
        {
            _pending = 0;
        }

        // ----- helpers -----

        internal static TrashManager TrashManagerOrNull()
        {
            try
            {
                return NetworkSingleton<TrashManager>.Instance;
            }
            catch
            {
                return null;
            }
        }

        internal static int TrashItemCount(TrashManager tm)
        {
            try
            {
                return tm != null && tm.trashItems != null ? tm.trashItems.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static bool TryGetPlayerPosition(out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                Player p = Player.Local;
                if (p == null)
                {
                    return false;
                }
                pos = p.transform.position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The player's horizontal facing direction (for placing the rain in front of them).</summary>
        private static Vector3 PlayerForwardFlat()
        {
            try
            {
                Player p = Player.Local;
                if (p != null)
                {
                    Vector3 f = p.transform.forward;
                    f.y = 0f;
                    if (f.sqrMagnitude > 0.01f) return f.normalized;
                }
            }
            catch { }
            return Vector3.forward;
        }
    }
}
