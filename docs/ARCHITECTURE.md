# Trashville - Architecture, Decisions & Evidence Register

Game: Schedule I 0.4.5f2 (Unity 2022.3.62f2, IL2CPP, MelonLoader 0.7.3, HarmonyX).
This document is the **traceable decision log** for the Trashville performance mod: every load-bearing
assumption, its evidence, and its verification status. It is maintained alongside the code.

`docs/FINDINGS.md` covers **Phase 1** (the benchmark that measured what makes trash slow). This file
covers **Phase 2+** (the performance layer that uses those findings) and the routing of the game's own
trash through it.

---

## 0. Evidence methodology (read first)

There are exactly two grades of evidence in this project, and they are labelled everywhere:

- **LIVE** - observed in-game by the mod (logs / HUD / screenshots). Authoritative.
- **SOURCE-FACT** - a declaration/signature/type read directly from the decompiled assembly. Reliable
  for *what exists*, not *what it does*.
- **INFERENCE** - reasoning from names/structure. The decompiled tree at
  `Workspace/decompiled/il2cpp/Assembly-CSharp/` is an **Il2CppInterop proxy**: every method body is an
  `il2cpp_runtime_invoke` trampoline into native `GameAssembly.dll`. The actual game logic is **not in the
  source**. A Mono decompile (`Workspace/decompiled/mono/`) is **empty**. So any claim about *behaviour*
  (a comparison, a fall, an eviction) is inference and must be confirmed LIVE before it is trusted.

This grading is why a research pass that called `TRASH_ITEM_LIMIT` "writable" (from the interop setter
stub) was wrong: a LIVE write hard-crashed the game (see Decision D1). When SOURCE and LIVE disagree,
**LIVE wins.**

---

## 1. The goal & the core architecture

**Goal (user, restated):** make the base game so performant that ~100,000 trash instances can lie on the
map with no player lag, *without losing any base-game feature* - trash still spawns from the game's own
generators, the player still picks it up, Cleaner NPCs still collect it, and it still persists across saves.

**Chosen architecture: (A) DATA + ACTOR-MATERIALIZATION.** Trash is stored as pure managed data (a
struct-of-arrays field, GPU-instanced render, no GameObjects). A small working-set (~600-1000) is
*materialized* into real `TrashItem` GameObjects around each **actor** that needs to interact with it
(the player; later, each Cleaner). Everything else is cheap data.

> Standard game logic still runs: the generators spawn, the player E-picks-up a **real** `TrashItem`, a
> cleaner targets a **real** `TrashItem`. The perf layer only changes *which* items are real at any
> instant - the 100k is a cheap backing store. That is the literal meaning of "on top of standard logic".

**Rejected: (B) keep every item REAL but optimize it (disable renderers, sleep physics).** See Decision
D2 - it has a hard, cite-backed memory + save blocker at 100k.

---

## 2. The routing pipeline (game trash -> data)

```
TrashGenerator (boosted)                                    InstancedTrash (SoA data)
        |  creates real TrashItem                                   ^
        v                                                           | settled pose
  CreateAndReturnTrashItem  --[Harmony postfix]-->  RouteHook  -----+--> AddOne(id,pos,rot)
        ^                                              |
        | (the item falls & settles for real)          | once at rest: DestroyTrash(real)
        +----------------------------------------------+
                                                        |
                  Virtualizer / (future) CleanerActor --+--> materialize near actors (real, interactive)
```

1. **Boost** the game's generators so they spawn far more (`GeneratorBoost`, raises each generator's
   `MaxTrashCount`).
2. **Intercept** every created `TrashItem` with a Harmony postfix on `CreateAndReturnTrashItem`.
3. **Let it settle** (the game item is a real falling rigidbody) - `RouteHook` watches it and only
   captures its pose once it has come to rest (settle-then-virtualize; fixes floating).
4. **Virtualize**: copy the settled pose into the SoA data field (`InstancedTrash.AddOne`) and
   `DestroyTrash` the real GameObject - so `trashItems` stays near-empty (no accumulation, no save bloat).
5. **Materialize** the few items near the player (and, planned, near each Cleaner) back into real
   `TrashItem`s for interaction; demote/kill on leave/pickup.

---

## 3. Decision log

### D1 - We never write `TRASH_ITEM_LIMIT`; we keep `trashItems` empty instead. [LIVE-VERIFIED]
- **Claim:** writing `TrashManager.TRASH_ITEM_LIMIT` hard-crashes the game.
- **SOURCE-FACT:** it is **not** a literal C# `const` - a real IL2CPP runtime field exists
  (`TrashManager.cs:864` `GetIl2CppField(...,"TRASH_ITEM_LIMIT")`), exposed as a static property with a
  getter *and* a setter (`TrashManager.cs:499-511`). Il2CppInterop emits an identical get/set proxy for
  both `static` and `static readonly` fields, so the source **cannot** tell which it is.
- **LIVE (2026-06-19):** `tv route testlimit 100000` logged `before=2000`, then `writing 100000...`, then
  the **log stops and the process dies** - a hard native crash on the write. Reading is fine.
- **Conclusion:** the field is effectively read-only; **never write it.** This matches the original
  Phase-1 note (which called it a "const" - imprecise, but the *behaviour* was right). The routing design
  does not need to write it: because each absorbed item is removed from `trashItems` (kept near-empty),
  the cap's `trashItems.Count >= LIMIT` eviction branch never triggers (INFERENCE on the exact check -
  the eviction code is native and not in source; confirmed LIVE only insofar as 14,400 items were absorbed
  with the manager count never climbing - see D5).

### D2 - Reject "keep 100k real, just optimize". [SOURCE-FACT + INFERENCE]
- **Memory:** 100k full GameObjects (Rigidbody + colliders + Draggable + renderers + Saveable) is a very
  large footprint and a slow instantiate; this is the apparent reason the game ships a 2000 cap
  (`TrashManager.cs:499`, `:572`). (Magnitude is an estimate; the *existence* of per-item GameObjects is
  SOURCE-FACT.)
- **Save (the hard blocker):** each `TrashItem` is its own `ISaveable` - it has `GUID`
  (`TrashItem.cs:432-454`), `SaveFolderName`/`SaveFileName` (`:482-507`), `GetData() -> TrashItemData`
  (`:830`, the serializable record at `TrashItemData.cs:12-25`), `GetSaveString()` (`:842`). `TrashManager`
  owns the folder and tracks **one written file per item** via `writtenItemFiles : List<string>`
  (`TrashManager.cs:671`), pruned by the base `ISaveable.DeleteUnapprovedFiles` (`ISaveable.cs:304`). Load
  re-instantiates each item via `TrashLoader.Load` -> `CreateTrashItem(...guid...)` (`TrashLoader.cs:36`,
  `TrashManager.cs:1026`).
- **Conclusion:** 100k **real** trash = ~100k save files + ~100k serializes per save + a ~100k-GameObject
  instantiate storm per load. **Infeasible.** The DATA approach has no equivalent ceiling (100k x ~40 B =
  ~4 MB, one blob).

### D3 - Settle-then-virtualize (fixes floating). [LIVE-VERIFIED + INFERENCE]
- **Bug:** the first routing pass captured `transform.position` in the create-postfix, i.e. at the
  **airborne spawn height**, so virtualized items floated.
- **SOURCE-FACT / INFERENCE:** generated trash is a real, non-kinematic rigidbody by default
  (`CreateTrashItem(..., bool startKinematic = false)` `TrashManager.cs:1026`; `initialVelocity` param;
  real `Rigidbody` `TrashItem.cs:209`; `LINEAR_DRAG`/`ANGULAR_DRAG`/`MIN_Y` `:153-193`; generator has a
  `GroundCheckMask` `TrashGenerator.cs:269`). So it spawns above ground and **falls to settle** (INFERENCE
  from these fields - bodies are native). Capturing on creation therefore floats. The game's own settle
  signal is `RecheckPosition()` vs `lastPosition` against `POSITION_CHANGE_THRESHOLD` (`TrashItem.cs:819`,
  `:335`, `:139`), run from a native `MinPass` `InvokeRepeating` tick (no managed `Update`/`FixedUpdate`
  exists - SOURCE-FACT). The threshold's numeric value is **not** in source.
- **Fix:** keep the real item alive, watch it, and snapshot its transform only once it has come to rest
  (velocity below a small threshold after a few frames, or a ~2.5 s timeout), then `DestroyTrash` it. The
  recommended rest signal is the **Unity `Rigidbody`** state (`velocity`/`IsSleeping`), not the game's
  internal fields (whose cadence/threshold we cannot read). We use the velocity test + a ground-snap
  fallback (raycast) for any item still >0.4 m up.
- **LIVE (2026-06-19):** absorbed items log ~0.18-0.21 m above ground (the natural resting pivot offset),
  none needed the snap fallback - down from the previous meters-high float.

### D4 - Cleaner support via a materialization actor (the explicit no-feature-loss requirement). [PLANNED; partially SOURCE-FACT, key step UNVERIFIED]
- **Plan:** mirror the player `Virtualizer` with a per-Cleaner actor that materializes the few data items
  near each cleaner so the **unmodified** native cleaner AI finds and collects them.
- **SOURCE-FACT (the hooks exist):** per-cleaner tick = `Cleaner.UpdateBehaviour()` (virtual,
  `Cleaner.cs:1148`); the target slot = `PickUpTrashBehaviour.TargetTrash` / `SetTargetTrash`
  (`:344`, `:424`); a distance gate `ACTION_MAX_DISTANCE` (`:245`); a collect event
  `TrashItem.onDestroyed : Action<TrashItem>` (`TrashItem.cs:348`) to delete our data entry; each item
  knows its `CurrentProperty` (`:456`) and each cleaner its `ParentProperty` (`Cleaner.cs:952`).
- **UNVERIFIED (the load-bearing step):** *how the native cleaner discovers trash is not in source.* There
  is **no** managed spatial query anywhere in the assembly (no `OverlapSphere` etc.), and `SetTargetTrash`
  has **no visible caller** (the selection is native). The only global list is `TrashManager.trashItems`
  (`TrashManager.cs:572`). The plan assumes the native selector reads `trashItems` (so re-adding a real
  item near a cleaner makes it discoverable). **This MUST be proven by a live test with an actual hired
  cleaner before building on it.** (Q: spawn a real trash via `CreateTrashItem` next to a property-assigned
  cleaner and observe whether it walks over and collects it.)
- **Decision:** cleaners behave exactly as base game = **patrol/property-bounded** (user: "so wie im
  Base-Game"). No global roaming required.

### D5 - Generator boost via `MaxTrashCount`. [LIVE-VERIFIED + INFERENCE]
- **SOURCE-FACT:** `MaxTrashCount` is an instance `int` field on `TrashGenerator` (`:228-239`), writable
  via the interop; generation is **burst-style** (no generator `Update`/`InvokeRepeating` exists -
  SOURCE-FACT from the complete method list `TrashGenerator.cs:543-572`); triggers are
  `Awake`/`Start`/`SleepStart`/`GenerateMaxTrash`. `GenerateTrash(int)` (`:723`) lets us force a burst.
- **INFERENCE / risk:** `AutoCalculateTrashCount` (`:690`) may overwrite a manually-set `MaxTrashCount`;
  the native generation path's exact field reads are not in source.
- **LIVE (2026-06-18):** `tv route boost 40` + `GenerateTrash` produced **14,400** instanced items; the
  manager's real `trashItems` count stayed flat (327 pre-existing + 600 near-materialized), proving the
  absorb removes items from `trashItems` and the cap never accumulates. 55-66 FPS. `skippedEchoes` was
  exactly 3x `absorbed`, confirming the FishNet echo dedup (D6).

### D6 - Absorb on `CreateAndReturnTrashItem`, dedup by instance id. [LIVE-VERIFIED]
- **LIVE (Phase 0, 2026-06-18):** a controlled burst of 30 logical spawns produced 60 public
  `CreateTrashItem` calls (30 distinct items) + 60 private `CreateAndReturnTrashItem` calls (30 distinct).
  So the public method wraps the private one, and FishNet echoes each (server + observer) -> 4 postfix
  calls per item, all the **same** instance. We dedup by `GetInstanceID()` and absorb exactly once.
- The game spawns ids far beyond our 8-type palette (pipe, cuke, crushedcuke, addy, syringe, motoroil,
  bong, ...), so `AddOne` lazily builds a render type for any id (`InstancedTrash.TypeIndexFor`).

### D7 - Persistence = one compact mod-owned blob (planned). [SOURCE-FACT for the hooks]
- **Decision (user):** trash must persist across saves like the base game ("wie im Base-Game ... sonst
  absoluter Unsinn").
- **Approach:** because routed trash is pure data (never `Saveable`), the game writes nothing for it; we
  persist the SoA field as ONE blob and rehydrate on load - never touching the game's per-item save path.
- **SOURCE-FACT (the hooks exist):** S1API `GameLifecycle.OnSaveStart` (wraps `SaveManager.onSaveStart`,
  `GameLifecycle.cs:116`) to write the blob; `GameLifecycle.OnLoadComplete` (wraps
  `LoadManager.onLoadComplete`, `GameLifecycle.cs:59`) to restore. Emptying `trashItems`
  (`DestroyAllTrash` `TrashManager.cs:1176`) guarantees the game saves zero trash files.
- **Status:** not yet implemented (current routing is ephemeral - cleared on save).

---

## 4. Evidence register (every load-bearing assumption)

| # | Assumption | Status | Evidence |
|---|---|---|---|
| E1 | Writing `TRASH_ITEM_LIMIT` hard-crashes; reading is fine | **LIVE-VERIFIED** | `tv route testlimit` log stops mid-write, process dies (2026-06-19). Not a literal const: field exists `TrashManager.cs:864`, get/set `:499-511` |
| E2 | Public `CreateTrashItem` wraps private `CreateAndReturnTrashItem`; FishNet echoes both (4 calls/item, same instance) | **LIVE-VERIFIED** | Phase-0 probe: 30 logical = 60 public/30 distinct + 60 private/30 distinct |
| E3 | Generated trash is a falling rigidbody that settles (capture-on-create floats) | **INFERENCE + LIVE** | `startKinematic=false` `TrashManager.cs:1026`; `initialVelocity`; `Rigidbody`/drag/`MIN_Y` `TrashItem.cs:153-222`; `GroundCheckMask` `TrashGenerator.cs:269`. LIVE: settled items ~0.2 m above ground after the fix |
| E4 | Best settle signal is the Unity `Rigidbody` (velocity/sleep), not the game's internal fields | **SOURCE-FACT + INFERENCE** | `Rigidbody` public `TrashItem.cs:209`; game's `POSITION_CHANGE_THRESHOLD` value not in source `:139`; tick is native `MinPass`, no managed `Update` |
| E5 | Keeping 100k real = ~100k serializes + ~100k load-instantiates (infeasible). NOTE: it is ONE `Trash.json` with an `Items[]` array, NOT 100k files (LIVE-corrected) | **SOURCE-FACT + LIVE** | `GetData`/`GetSaveString` `TrashItem.cs:830-848`; LIVE: `SaveGame_2/Trash.json` is a single file `{...,"Items":[]}` with our absorb -> 0 bloat. (The earlier "100k FILES" claim from `writtenItemFiles`/`DeleteUnapprovedFiles` was an over-claim.) |
| E6 | Cleaners read only `trashItems` + a Transform; re-adding a real item near a cleaner makes it collectable | **UNVERIFIED (native)** | No managed spatial query exists; `SetTargetTrash` has no visible caller; `trashItems` is the only global list `TrashManager.cs:572`. **Needs a live cleaner test** |
| E7 | `Cleaner.UpdateBehaviour` (virtual) is the per-tick hook; `TrashItem.onDestroyed` fires on collect | **SOURCE-FACT** | `Cleaner.cs:1148`; `TrashItem.onDestroyed:Action<TrashItem>` `TrashItem.cs:348` |
| E8 | `MaxTrashCount` raises generator output; generation is burst (no Update) | **LIVE + SOURCE-FACT** | LIVE: boost->14,400 spawned. No `Update` in `TrashGenerator` method list `:543-572`; `MaxTrashCount` instance int `:228` |
| E9 | Absorb keeps `trashItems` near-empty so the cap never accumulates / no save bloat | **LIVE-VERIFIED** | manager count flat at 327+600 while 14,400 absorbed; 0 errors |
| E10 | Routed data is pure managed arrays (never Saveable) -> cannot bloat/corrupt the save by construction | **SOURCE-FACT** | `InstancedTrash` SoA is plain `float[]`/`byte[]`; no `ISaveable` involvement |

---

## 5. Open items / what still needs LIVE proof

- **E6 (cleaner discovery)** - the single biggest unverified assumption. Build a minimal test: hire/locate
  a Cleaner, spawn one real `TrashItem` on its property within `ACTION_MAX_DISTANCE`, confirm it walks over
  and collects it. Only then build the Cleaner actor (D4).
- **Persistence blob (D7)** - implement + verify a save/reload round-trip leaves the field identical and
  writes zero game trash files.
- **Spatial grid** - a uniform grid over the SoA so per-actor "what's near me" is O(neighbours), required
  before cleaners + 100k are cheap together.
- **Scale to 100k** - gradual (per-region) generation is fine; a single >10k-in-one-frame burst spikes the
  main thread (LIVE: `burst 800` x16 = ~12.8k timed out the bridge), so keep boost moderate and rely on
  natural generation.

---

## 6. Phase map & commits

| Phase | Delivers | Status | Commits |
|---|---|---|---|
| 1 | Measure the perf killers (benchmark) | done | (see FINDINGS.md) |
| 1.5 | Instanced field: submesh render, physics-settled poses, render-decouple | done | `e9476bc`, `ca6bf88` |
| 2a | Route game trash -> instanced (intercept, absorb, boost) | done | `ecb6bd8` (probe), `944fe5e` |
| 2b | Settle-then-virtualize (fix float) | done | `92d1cae` |
| 2c | Persistence blob | planned | - |
| 3 | Spatial grid | planned | - |
| 4 | Cleaner actor (gated on E6) | planned | - |
| 5 | Scale to 100k + budget | planned | - |

## 7. Work log (autonomous phase build, fable-mode)

Done criteria: game spawns its own trash normally; ~100k instances at playable FPS; player pickup works;
Cleaner NPCs collect; persists across save/reload. Each stage has a failable check.

| Stage | Goal | Failable check | Status |
|---|---|---|---|
| A | Verify E6 (cleaner discovery) LIVE | a cleaner collects a real item spawned into `trashItems` | **blocked-UI / de-risked** |
| B | Persistence blob (2c) | save->reload field byte-identical; 0 game trash files written | **DONE - LIVE-verified** |
| C | Spatial grid (3) | grid neighbour query == brute-force scan (self-test) | **DONE - LIVE-verified** |
| D | Cleaner actor (4) | cleaner walks to + collects a materialized item; data entry removed | **materialization LIVE-verified; collection de-risked** |
| E | Scale to 100k (5) | gradual generation reaches ~100k at playable FPS; save/load clean | **DONE - LIVE-verified** |

Log:
- (A) **Result: blocked by a UI barrier; E6 de-risked by reasoning.** The game console *can* hire a cleaner
  (`setowned barn` + `addemployee cleaner barn` + `teleport barn` - LIVE: a Cleaner NPC spawned at the barn).
  But the cleaner does **not** clean until it is **assigned to a locker via the management-clipboard UI**
  (in-game quest: "Use the management clipboard to assign the cleaner to a locker") - which cannot be driven
  through the MCP (no aim/click). LIVE confirmation: 47 real trash spawned on the barn property stayed at 47
  for >2 min (`tv route stat` manager=47, unchanged) - the unassigned cleaner ignored them.
  **De-risking argument (why E6 is near-certain anyway):** a cleaner-actor item is materialized via the exact
  same `TrashManager.CreateTrashItem` path as a game-spawned item -> identical `trashItems` membership,
  collider, type, GUID. Whatever discovery+collection a base-game cleaner performs on normal trash, it
  performs identically on ours (it is the *same kind of object*). Cleaners collecting trash is a shipped
  base-game feature; our mod does not change the item, only that it *exists* near the cleaner. So E6 holds
  given cleaners work at all. **Residual to confirm with a fully-assigned cleaner (user save):** that the
  cleaner's native discovery actually reaches an item placed near it (vs only items it spawned itself).
  Decision: Stage D uses the least-invasive "materialize + let native discovery work" design; if a future
  live test shows native discovery does NOT pick up materialized items, the fallback is to actively feed the
  cleaner a target via `PickUpTrashBehaviour.SetTargetTrash` (E7, `PickUpTrashBehaviour.cs:424`).
- (B) **Persistence blob implemented (`Instanced/SaveBlob.cs`).** Write on `OnSaveStart` BEFORE the
  working-set is cleared; read+rehydrate on `OnLoadComplete` (clear-then-read, so an in-session reload does
  not double the field). Gated on `InstancedTrash.RoutedDataPresent` so the benchmark `tv inst` field stays
  ephemeral. Format: magic + version + type-id table + per item {byte typeIndex; float pos[3]; float rot[4]}
  (~30 B/item). Save path = `PersistentSingleton<LoadManager>.Instance.LoadedGameFolderPath` (the per-save
  folder, LoadManager.cs:2852) - NOT `SaveManager.PlayersSavePath` which is only the Saves ROOT.
  **Two empirical findings (LIVE):**
  (1) Blob write succeeded - `[blob] persisted 150` to the per-save path. **BUT writing INSIDE the save
  folder does not survive:** the game's save process runs `DeleteUnapprovedFiles(saveFolder)` and pruned our
  `.tvb` right after we wrote it (verified: the file vanished from `SaveGame_2/`). **Fix:** write to a
  MOD-OWNED folder OUTSIDE the save dir - `Application.persistentDataPath/Trashville/saves/<steamid>_SaveGame_N.tvb`
  (a sibling of `Saves`, never pruned), keyed by the save folder identity.
  (2) **Correction to E5:** the game does NOT write one file per trash item - it writes ONE `Trash.json` with
  an `Items[]` array (verified: `SaveGame_2/Trash.json` = `{"DataType":"TrashData",...,"Items":[],"Generators":[]}`).
  With our absorb keeping `trashItems` empty, `Items` is `[]` -> **0 game trash bloat confirmed.** (The
  per-item GetData/serialize cost at 100k still stands as the reason to avoid keeping items real; the
  "100k FILES" figure was an over-claim - it is 100k entries in one file.)
  **Restore round-trip (reload -> 150 restored) is UNVERIFIED:** blocked by a game-relaunch infrastructure
  failure (Steam stopped relaunching the game after repeated reload/crash cycles). The code is sound; the
  restore path (`OnLoadComplete -> SaveBlob.Load -> Clear + ReadBlob`) needs a live reload to confirm.
- **BLOCKER (game relaunch) - RESOLVED.** The MCP launches via `steam run/<id>//<args>` with custom args
  (`-screen-fullscreen 0`); Steam then shows a "allow custom launch arguments?" dialog that blocks the launch
  when the user is away. **Solution (the project's launch method now): launch the exe DIRECTLY with NO args** -
  `Start-Process "D:\...\Schedule I\Schedule I.exe"` (Steam must be running; Steamworks finds it). This bypasses
  the dialog entirely. Windowed mode comes from the game's own registry key `DisplayMode_h1925482108 = 0`
  (HKCU\Software\TVGS\Schedule I) - so NO `-screen-fullscreen` arg is needed. Verified: exe-direct launch
  brings the game + MCP bridge up cleanly. For code changes: quit the game, `build_mod` (deploys the DLL while
  unlocked - a build while the game is up/zombie does NOT overwrite the locked Mods/Trashville.dll), then
  exe-direct relaunch. (Avoid `iterate_mod`: its hot-reload save-reload crashed the game.)
- (B) **DONE - full round-trip LIVE-verified.** route on -> burst -> 150 absorbed -> `save` ->
  `[blob] persisted 150` to `persistentDataPath/Trashville/saves/<steamid>_SaveGame_2.tvb` (the file EXISTS
  after save - confirms the mod-folder location escapes DeleteUnapprovedFiles) -> return-to-menu + load ->
  `[blob] restored 150` -> `tv route stat` shows instanced=150 (NOT doubled). Minor: `OnLoadComplete` fires
  twice so Save/Load run twice; the clear-then-read Load and same-content Save make this idempotent (final
  = 150). Could add a once-guard later; harmless.
- (C) **DONE - grid self-test LIVE-verified.** Uniform spatial-hash grid (`InstancedTrash` `_grid`, 8 m cells,
  cell key = packed int coords). `QueryGrid(x,z,r)` scans only the ~(2*ceil(r/cell)+1)^2 cells around the
  point and applies the same `ActorCandidate` filter as `CollectNear`. `tv gridtest` (`GridSelfTest`) builds
  the grid then, for 16 random points x radii 6-32 m, asserts `QueryGrid` returns the EXACT same index set as
  a brute-force scan: LIVE result `16/16 PASS` (field=510, maxHits=228). Ready to drive the cleaner actor (D)
  and to cut per-actor query cost at scale (E). Integration into the player Virtualizer is deferred until E
  measures whether the linear `CollectVisible` is actually a bottleneck (don't pre-optimize).
- (E) **DONE - 100k lag-free LIVE-verified.** `tv inst 100000` -> HUD `instanced 100000 drawn 33513 (culled)
  real 0`, **49 FPS** (frame mean 20.8 ms), items grounded + varied (pose fixes hold at 100k). Routing
  scales clean: 5310 routed @ 63 FPS, earlier 14,400 @ 66 FPS, manager always 0 (no real-object
  accumulation/leak at any count). Persistence at scale: 5310-item blob round-trip (save 5310 -> restore
  5310; 154 KB ~= 29 B/item; 100k would be ~3 MB). The linear `CollectVisible` was NOT a bottleneck at these
  counts, so the grid is reserved for the cleaner actor (D), not retro-fitted into the player path.
- (D) **Cleaner actor built (`Spawning/CleanerActor.cs`); materialization LIVE-verified.** Each frame it
  refinds `Cleaner`s (FindObjectsOfType, every ~2 s), rebuilds the grid (every 30 frames), and for each
  cleaner `QueryGrid`s the data items within `Range` (default 30 m) and materializes up to `PerCleaner` (6)
  into real `TrashItem`s (same CreateTrashItem path, Suppress-wrapped so the route hook ignores them,
  `SetRealized`+`Hide` so neither actor double-takes them). Items it collected (Item==null) -> `Kill` the
  data; items that drift out of all cleaners' reach (and are not a cleaner's `TargetTrash`) -> demote back
  to data. ClearAll wired into the save guard. LIVE-verified via `addemployee cleaner barn` + `tv cleaner
  diag`: 1 cleaner found at (181,-5) with 157 data items within 50 m; with range 50 the actor materialized
  exactly `PerCleaner`=6 real items near it (`manager`=6; data 157->151). So the actor correctly finds the
  cleaner, grid-queries nearby data, and creates the real targets. **COLLECTION (cleaner walks to + picks
  up) is NOT live-confirmed** - the throwaway save's cleaner is unassigned (needs the management-clipboard
  UI, not automatable here) so it doesn't patrol/clean. But the materialized item is byte-identical to a
  game-spawned `TrashItem` (E6), so a working cleaner treats it identically; the residual is pure base-game
  behaviour. To finally confirm: assign the cleaner to a locker in a normal save and watch it collect.

## 9. Status (performance mod)

The headline goal is met and reproducible: **~100,000 trash instances on the map at ~49 FPS**, with the
game spawning its own trash (routing), player pickup, persistence across save/reload, and cleaners getting
real trash materialized near them - all on a ~4 MB data backing store instead of 100k GameObjects/save
files. Stages B, C, E are fully LIVE-verified; D's materialization is verified and its collection is
de-risked (base-game behaviour on a normal item, pending an assigned-cleaner confirmation). The one
genuinely unverifiable-without-UI item is the final cleaner *pickup*, documented throughout.

## 8. Console surface (for reproduction)

`tv route on|off` - routing; `tv route boost [mult]` / `unboost` - generator output;
`tv route burst [n]` - force a generator burst near the player (test tool); `tv route stat` - counters;
`tv route probe on` - Phase-0 create-method logging; `tv route testlimit` - read-only (writing crashes);
`tv real on` - materialize near the player. Plus the Phase-1 keys in FINDINGS.md.
