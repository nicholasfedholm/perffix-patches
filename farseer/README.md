# Farseer perffix (Farseer 1.4.0)

- Original mod: Farseer (modid `farseer`), by Badgerson.
- ModDB page: https://mods.vintagestory.at/show/mod/22371 (from a comment in the mod's own lang file).
- Unofficial patch. You need the original `farseer_1.4.0.zip`; nothing of the original is included here.

## What it changes and why
Far-terrain region heightmaps are sampled by Farseer's `TerrainSamplerAdapter`. The patch IL-patches 3 method bodies of the stock `Farseer.dll` (Mono.Cecil) to hand work to a new helper assembly, `FarseerPerffix.dll` (`src/FarseerPerffix`, our source):
- Phase 1: one global pool of sampler threads pulls grid rows from the highest-priority region, so a region is sampled by many threads. Output is independent of thread count and order.
- Phase 2: region priority and stale-work cancellation are forwarded from Farseer's own sort/cancel calls, so nearest regions are done first and regions no longer in view stop costing sampler time.
- Optional `ModConfig/farseer-perffix.json` (read only): SamplerThreads (default logical cores - 4, capped at 12), ThreadPriority, FastDelegate, PriorityQueue, CancelStale.

## Measured results (from RESULTS.md)
Quoted line: "Phase 1 gain is modest: about 1.3x on wall time (843 -> 637 ms for 24 regions) and +60% CPU."
That is 24.4% less wall time (843 to 637 ms) on a 24-region headless bench, about 1.32x, stock 4-way vs perffix 12 threads. Caveats from the same file: one run each, roughly +-10% noise, other processes were running, no in-game FPS or client fill time was measured. Correctness gates: 48/48 heightmaps byte-identical to stock at 1, 4, 8, 12, 16 threads. See `RESULTS.md`.

## Contents
- `src/FarseerPerffix/`: helper source. Needs the stock `Farseer.dll` at `work/stock/Farseer.dll` (extract it from your own copy; not included).
- `tools/Patcher/`: Cecil patcher. Usage: `Patcher <stock Farseer.dll> <FarseerPerffix.dll> <out Farseer.dll> [--phase2]`. `tools/Inspect/`: small IL inspection helper.
- `build.py phase1|phase2`: builds helper, patcher, and a zip from your stock zip. It reads `%APPDATA%\VintagestoryData\Mods\farseer_1.4.0.zip`; edit the constant if yours is elsewhere.
- `bench/`: headless bench harness and runner (`run_bench.py`) that uses an isolated data path and copies a terrain mod stack from your Mods folder.
- `RESULTS.md`: benchmark notes.
