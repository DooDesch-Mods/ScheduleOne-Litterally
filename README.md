# Litterally

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

A performance mod for **Schedule I** (MelonLoader / IL2CPP) that lets the world hold
*litterally* far more trash with little cost, **without losing any base-game behaviour**.

## What it does

The game's own trash generators are a known performance sink: every piece of litter is a
real `GameObject` with a rigidbody, colliders, a per-item tick and its own save file.
Litterally routes the game's generated trash into a cheap **GPU-instanced field** (rendered
in a few draw calls, stored as compact arrays) and **materializes real, interactable items
only in the small working set around the player and cleaners**. Everything else stays
virtual until you (or a cleaner) actually get near it.

The result: the map can hold tens of thousands of trash items at a playable frame rate,
while everything still works exactly like vanilla:

- **Pick up / throw** trash near you - the items in front of you are real `TrashItem`s.
- **Cleaners** roam, collect the litter, empty their bins and dispose the bags as usual.
- **Sleeping-dynamic physics** - nearby trash settles into natural resting poses, collides
  realistically and can be shoved or thrown, then auto-sleeps so it stops costing.
- **Persistence** - the field is saved as one compact blob and restored on load. It is
  written *outside* the game's save folder, so it can never bloat or corrupt your save.
- **Good citizen** - only the game generator's own trash is absorbed; trash another mod
  creates directly is left untouched.

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) for Schedule I.
2. Install **S1API** (required) and, optionally, **Mod Manager & Phone App** (for an
   in-game settings screen).
3. Drop `Litterally.dll` into the game's `Mods/` folder.

The performance layer turns on automatically - there is nothing to configure to get the
benefit.

## Settings

Configurable via the MelonPreferences file (`UserData/MelonPreferences.cfg`, category
`Litterally_01_Main`) or the Mod Manager phone app:

| Setting | Default | Meaning |
|---|---|---|
| `EnablePerformanceLayer` | on | Master switch. Off = fully vanilla trash. |
| `EnableInMultiplayer` | off | Force the layer on in multiplayer (experimental - see below). |
| `MaxRealItems` | 200 | How many trash items are real/interactable around you at once (the perf vs interaction-range dial). |
| `MaterializeDistance` | 32 | How far ahead (metres) trash becomes interactable. |
| `RenderDistance` | 150 | How far (metres) the instanced field draws - lower it for a perf win on open ground. |
| `TrashMultiplier` | 10 | Multiply the game's own trash output (1 = vanilla amount, up to 1000), kept cheap by the layer. |
| `ActivePhysics` | off | Materialized trash uses active (sleeping-dynamic) physics. |

Optional on-screen overlays, all off by default: `ShowFpsCounter`, `ShowActiveItems`
(highlights the active/real trash), `ShowStatsPanel` (live perf-layer stats), `ShowRanges`
(materialize-radius rings).

## Multiplayer

The instanced field is **local-only** - it is not replicated over the network - so by
default the performance layer **auto-disables in multiplayer** to avoid desync, and the
game runs vanilla. Testers can set `EnableInMultiplayer = on` to try it anyway. Proper
host-authoritative multiplayer support is a planned follow-up.

## Building from source

`net6.0` (IL2CPP backend). References are resolved from `../Workspace/lib`.

- `dotnet build -c Release` - the clean shipping build (no dev surface).
- `dotnet build -c Debug` - adds the benchmark/ablation harness: an on-screen HUD, dev
  hotkeys, and a `tv ...` dev console. All of it is compiled out of Release via `#if DEBUG`.

Author: DooDesch. Licensed MIT (see LICENSE.md).
