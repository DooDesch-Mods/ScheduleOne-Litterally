# Litterally - Trash Without the Lag

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/litterally](https://support.doodesch.de/litterally).

> Schedule I's litter is a known performance sink - every scrap is a real object with a
> rigidbody, colliders and its own save entry. Litterally routes all of it into a cheap
> instanced field and only makes the trash near you real. You get the vanilla amount of
> litter and better frames; turn one setting up and the map holds *litterally* tens of
> thousands of pieces, still at a playable frame rate.

![Version](https://img.shields.io/badge/version-1.1.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)

## What it does

The game's trash generators are expensive: every piece of litter is a real `GameObject`
with a rigidbody, colliders, a per-item tick and its own save file. Litterally routes that
generated trash into a cheap **GPU-instanced field** (a few draw calls, compact arrays) and
**materializes real, interactable items only in the small working set around the player and
cleaners**. Everything else stays virtual until you (or a cleaner) actually get near it.

Turn the multiplier up and the map holds tens of thousands of trash items at a playable
frame rate, with everything still working exactly like vanilla.

## Features

- **Pick up / throw** trash near you - the items in front of you are real `TrashItem`s.
- **Cleaners** roam, collect the litter, empty their bins and dispose the bags as usual.
- **Sleeping-dynamic physics** - nearby trash settles into natural poses, collides
  realistically and can be shoved or thrown, then auto-sleeps so it stops costing.
- **Persistence** - the field is saved as one compact blob and restored on load, written
  *outside* the game's save folder so it can never bloat or corrupt your save.
- **Good citizen** - only the game generator's own trash is absorbed; trash another mod
  creates directly is left untouched.
- **More trash if you want it** - `TrashMultiplier` defaults to 1, the vanilla amount.
  Raise it to pile up far more litter; the same layer keeps that cheap.

## Requirements

- **Schedule I** `0.4.6f11` (IL2CPP) with **MelonLoader 0.7.3+**.
- **S1API** (pulled in as a dependency).

The performance layer turns on automatically - there is nothing to configure to get the
benefit.

## Settings

Editable in `UserData/MelonPreferences.cfg`
(category `Litterally_01_Main`):

| Setting | Default | Meaning |
|---|---|---|
| `EnablePerformanceLayer` | on | Master switch. Off = fully vanilla trash. |
| `EnableInMultiplayer` | off | Force the layer on in multiplayer (experimental). |
| `MaxRealItems` | 200 | How many trash items are real/interactable around you at once. |
| `MaterializeDistance` | 32 | How far ahead (m) trash becomes interactable. |
| `RenderDistance` | 150 | How far (m) the instanced field draws. |
| `TrashMultiplier` | 1 | Multiply the game's own trash output (1 = vanilla, up to 1000). |
| `ActivePhysics` | off | Materialized trash uses active (sleeping-dynamic) physics. |

Optional on-screen overlays (all off by default): `ShowFpsCounter`, `ShowActiveItems`,
`ShowStatsPanel`, `ShowRanges`.

## Multiplayer

The instanced field is **local-only** - it is not replicated over the network - so by
default the performance layer **auto-disables in multiplayer** and the game runs vanilla.
Set `EnableInMultiplayer = on` to try it anyway. Proper host-authoritative multiplayer
support is a planned follow-up.

## License

MIT. See the included LICENSE.md.
