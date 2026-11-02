# Footprints perffix (Footprints 1.2.14)

- Original mod: Footprints (modid `footprints`), by Aylor222.
- ModDB page: search the mod name "Footprints" (no URL found in local files).
- Unofficial patch. You need the original `Footprints-v1.2.14.zip`; nothing of the original is included here.

## What it changes and why
`PlayerFootprintLifespan` and `AnimalFootprintLifespan` are int config values in in-game hours, so the minimum (1) is one game hour and 30 seconds cannot be expressed by config alone. The Mono.Cecil patcher edits two getters, `Footprint.get_IsExpired` and `Footprint.get_RemainingLifeFraction`, multiplying the lifespan by 0.25 after its `conv.r8`. The config value therefore means quarter game hours (1 = 0.25 game h, about 30 s at an assumed 120 s per game hour; verify for your calendar speed). Side effect: any other value N now means N x 15 game minutes. The patcher aborts if the IL shape differs. Fewer live footprints is the expected perf gain.

Measured results: none. The perf effect is stated as expected and unmeasured in `PATCH_NOTES.md`.

## Contents
- `patcher/`: Cecil patcher + csproj. Usage: `FootprintsPatcher <input Footprints.dll> <output dll> <game dir>`.
- `pack.py`: repacks your original zip with the patched `out\Footprints.dll` (drops the stale `.pdb`).
- `PATCH_NOTES.md`: build notes.
