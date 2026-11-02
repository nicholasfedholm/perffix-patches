# Farseer perffix - results (2026-10-24)
Bench: headless VintagestoryServer 1.22.7, isolated data path (bench\data\<run>), seed 424242, same terrain stack as live (sampler 1.3.0, watersheds 7.0.4-perffix [bench-time version; Mods now has watersheds_7.0.6-perffix.zip as of 2026-11-01], TerraPrety + default, P5WorldgenEngine, rivers 5.0.3, live ModConfig copies). 24 far regions (5x5 ring around the map centre), grid 128, requested at once through Farseer's own TerrainSamplerAdapter. Pass 1 = cold sampler caches, pass 2 = warm. Sampler path: WatershedsLoaded=True.
Contention: about 15 dotnet.exe and 5 claude.exe processes were running (other sessions/builds); the game was not running. Each figure is one run, so expect roughly ±10% noise. Compare runs against each other only.

## Gates
| Gate | Result |
|---|---|
| Phase 0 thread safety | SAFE-WITH-CONDITIONS (AUDIT.md) |
| Phase 1 byte-identical | PASS: 48/48 heightmaps identical to stock at 1, 4, 8, 12, 16 threads; independent serial reference 24/24 |
| Phase 2 same regions, order only | PASS: wanted set 16/16 produced, 0 dropped regions produced, 0 priority inversions (stock: 24 produced, 260 inversions), all byte-identical to stock |

## Timing (24 regions, pass 1 / pass 2 wall ms, CPU ms pass 1)
| Run | Wall p1 | Wall p2 | p50 p1 | p95 p1 | CPU p1 | "overloaded" lines |
|---|---|---|---|---|---|---|
| stock (4-way) | 843 | 859 | 461 | 811 | 3094 | 0 |
| perffix t4 | 884 | 906 | 476 | 883 | 3422 | 1 |
| perffix t8 | 924 | 832 | 394 | 851 | 5000 | 0 |
| perffix t12 (default) | 637 | 657 | 357 | 636 | 4984 | 1 |
| perffix t16 | 630 | 612 | 352 | 629 | 6266 | 1 |
| perffix t1 (serial) | 2849 | 2830 | 1486 | 2777 | 3203 | 3 |
Earlier stock run: 962/1011 ms. Phase 2 with stale work dropped (prio bench): the 16 wanted regions finished in 419 ms (t12), vs 932 ms for stock, which also built the 8 unwanted ones.

## Honest read
- Phase 1 gain is modest: about 1.3x on wall time (843 -> 637 ms for 24 regions) and +60% CPU. Scaling flattens after about 4 threads.
- Likely cause (unverified, subagent reading of scaling_detail.md): every sample allocates inside the sampler (`Activator.CreateInstance` + `new object[]` for the reflection call into Watersheds, TerrainSamplerMod.cs:90-91). Gen0 GC then limits parallel scaling. That code lives in AlgernonsTerrainSampler, outside this patch's scope (PLAN: TerrainSamplerAdapter only). The Farseer-side fast delegate gave no measurable gain (t1 2849 ms vs plain reflection 2829 ms).
- Phase 2 is the larger practical win when moving: the nearest regions come first, and regions no longer in view stop costing sampler time.
- Absolute cost is already small: about 125 ms of CPU per region at grid 128. If far terrain still fills slowly in game, the bottleneck is probably elsewhere (provider tick cadence of ~7 s for UpdateRegionsInView, network send, client meshing). That was not measured: a headless bench has no client, FPS or MemWatch.
- Not measured: in-game route, FPS, MemWatch peak, client fill time. These need a human play session after install.
- Default chosen: 12 threads (best wall time, no rise in overloaded ticks). 16 gives the same wall time for more CPU.
