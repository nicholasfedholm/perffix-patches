# Watersheds perffix (Algernons Watersheds 7.0.4 and 7.0.6)

- Original mod: Algernons Watersheds (modid `watersheds`), by Algernon.
- ModDB page: https://mods.vintagestory.at/algernonswatersheds
- Unofficial patch. You need the original Watersheds zip; nothing of the original is included here.

## The problem
`PartiallyLitChunkFixPatch` subscribes to `ChunkColumnLoaded` and, on every column load (main thread), calls `topChunk.Unpack()` just to check whether the top chunk's sunlight is all zero. For columns loaded from disk, `Unpack()` decompresses the chunk, discards its compressed copy, and marks it changed so it must be recompressed later.

## What it changes
The check now only runs for columns generated in the current session. A helper class (`helper/FreshColumns.cs`, `Watersheds.Patches.PartiallyLitFreshColumns`) registers a `ChunkColumnGeneration` handler at the `PreDone` pass for the `"standard"` world type and remembers each column's coordinates (set capped at 65,536). `OnChunkColumnLoaded` starts with `if (!PartiallyLitFreshColumns.ConsumeIsFresh(chunkCoordinate)) return;`. `Initialize` registers the handler, `Dispose` clears the set. The patcher IL-patches only those three methods with Mono.Cecil; it was not recompiled from source.

Same patcher and helper were run unchanged on 7.0.4 and on 7.0.6 (abort preconditions did not fire).

Measured results: eliminates the `Unpack()` call for disk-loaded columns; behaviour tests 11/11 (fake server API); JIT of every method matches the original. No FPS percentage was measured.

Known trade-off: a freshly generated column with broken lighting whose relight was still queued when the game quit is loaded from disk next session and is not re-checked, so it could keep a dark top layer. Rolling back to the original and reloading the area fixes it.

## Contents
- `helper/` : FreshColumns.cs + csproj.
- `patcher/` : Cecil patcher. Usage: `WatershedsPatcher <input Watersheds.dll> <helper dll> <output dll> <game dir>`.
- `test/` : 11 behaviour checks; csproj expects the patched DLL at `..\patched\Watersheds.dll`.
- `package.py` : `python package.py <your original watersheds zip>`; swaps in `build\Watersheds.dll` and tags the modinfo description as a local perf build.
