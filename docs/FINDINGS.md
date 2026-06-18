# Trashville - Phase 1 Findings (Trash Performance Diagnosis)

Game: Schedule I 0.4.5f2 (Unity 2022.3.62f2, IL2CPP, MelonLoader 0.7.3). Test machine: AMD RX 7900 XTX.
All numbers are measured in-game by the Trashville benchmark mod, not assumed.

## TL;DR - the evidence-based killer ranking

1. **Active physics (falling / colliding rigidbodies) = THE killer.** Measured: **+12.8 ms per 1000 items**
   while settling (53 fps -> 32 fps). This is the lag.
2. **Per-collision impact sounds** (`ScheduleOne.Audio.RBImpactSounds` -> `SFXManager.PlayImpactSound`) =
   **crash-level**. When many trash collide at once the audio-source pool is exhausted and the game spams
   thousands of `Debug.LogWarning` per frame and hard-crashes. Only fires while trash is colliding.
3. **Static / sleeping trash is essentially free.** Rendering, colliders, scripts and *sleeping* rigidbodies
   of up to ~2000 items add <= ~5 ms - below the engine's own frame-time drift. A sleeping rigidbody costs
   ~0 over a kinematic one.

Structural fact: **the game hard-caps live trash at `TRASH_ITEM_LIMIT = 2000`** (a compile-time `const`),
enforced by evicting the oldest trash whenever a new one is created.

## How it was measured

- **Frame time** via `Time.unscaledDeltaTime` over a 120-frame window (mean/median/p95/p99/stddev), framerate
  uncapped during measurement, framerate-cap restored after.
- **Primary technique = controlled ablation / paired A-B** (build-independent). `ProfilerRecorder` counters are
  ALL `Unavailable` in this release IL2CPP build (verified), so they were never load-bearing - frame-time
  deltas are the evidence.
- **Stability gate**: a cell is only recorded once `stddev/mean <= 0.22`. Scene purged before every cell.

## Key measurements

### Physics A/B (same 1000 items, back-to-back -> drift-free)
```
FROZEN (kinematic)     18.9 ms  (53 fps)
SETTLED (asleep)       18.7 ms  (54 fps)   <- a sleeping rigidbody == a kinematic one
SETTLING (active)      31.7 ms  (32 fps), p95 37.2 ms
=> active-physics cost = +12.8 ms / 1000 ;  sleeping-body overhead = -0.3 ms (~0)
```

### Static (kinematic) sweep, 0..1950 items (purged, stable cells)
`all-on` cost over the empty-scene baseline (`deltaVsBaselineN0`): 250=+0.3, 500=+0.4, 1000=+3.3,
1500=-5.6, 1950=+4.3 ms. I.e. <= ~5 ms and sometimes negative -> below the ~+/-10 ms engine baseline drift.
Render/collider/script ablation deltas flip sign across counts precisely because the real cost is *under the
noise floor* -> these subsystems are cheap for resting trash.

### Rough corroboration
~2000 kinematic trash ~= 49 fps; ~984 dynamic (settling) ~= 20 fps.

## What this means for the goal (10000 lag-free trash)

- The lag is NOT the existence/rendering of trash - it is the **active-physics settling churn** and its
  **collision sounds**. Trash that is asleep/kinematic is nearly free.
- Therefore **10000 STATIC (kinematic, asleep, sound-disabled) trash is plausibly viable**; 10000
  *actively settling* trash is not (and the game won't even hold >2000 live).

## Phase 2 (optimization) direction - now evidence-led, not assumed

1. **Bypass the 2000 cap** - Harmony-patch the eviction / limit check in `TrashManager.CreateTrashItem`
   (`TRASH_ITEM_LIMIT` is a const and cannot be written - writing it crashes; patch the *logic* instead).
2. **Force trash to sleep / go kinematic ASAP and stay asleep** - sleeping is free; the cost is motion.
3. **Throttle or disable per-collision impact SFX at scale** - it is crash-level and gameplay-irrelevant at
   thousands of items.
4. **Consider GPU instancing / batching** for the render path only if 10000 static proves render-bound
   (cheap at 2000, unknown at 10000).

## Reproduce

In a throwaway save: `F5` arm, `F3` physics A/B, `F11` full sweep (writes `Mods/Trashville/runs/*.csv`),
`F4` toggles kinematic/dynamic, `Shift+F10` purges. CSV columns include `stable` and per-counter validity so
no unverified number is ever presented as fact.
