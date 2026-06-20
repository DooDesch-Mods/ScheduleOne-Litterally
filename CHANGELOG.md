# Changelog

All notable changes to Litterally are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

## [1.0.0] - 2026-06-20

First public release.

### Added
- Routes the game's own generated trash into a cheap GPU-instanced field (rendered in a
  few draw calls, stored as compact arrays) so the map can hold tens of thousands of trash
  items at a playable frame rate.
- Materializes real, interactable `TrashItem`s only in the small working set around the
  player and cleaners - everything else stays virtual until you (or a cleaner) get near it.
- Keeps all base-game behaviour: pick up / throw nearby trash, cleaners roam and collect
  and dispose litter as usual, and persistence is preserved.
- Sleeping-dynamic materialized physics: nearby trash settles into natural resting poses,
  collides realistically, can be shoved or thrown, then auto-sleeps so it stops costing.
- Compact save blob: the field is saved as one blob and restored on load, written outside
  the game's save folder so it can never bloat or corrupt your save.
- Good citizen: only the game generator's own trash is absorbed; trash another mod creates
  directly is left untouched.
- Render distance cap for the instanced field (`RenderDistance`) - a perf win when looking
  across open ground, with no effect on interaction range.
- Settings: `EnablePerformanceLayer`, `EnableInMultiplayer`, `MaxRealItems`,
  `MaterializeDistance`, `RenderDistance`, `TrashMultiplier`, `ActivePhysics`, plus opt-in
  on-screen overlays (`ShowFpsCounter`, `ShowActiveItems`, `ShowStatsPanel`, `ShowRanges`).

### Notes
- The instanced field is local-only, so the performance layer auto-disables in multiplayer
  to avoid desync (set `EnableInMultiplayer` to try it anyway). Host-authoritative
  multiplayer support is a planned follow-up.
