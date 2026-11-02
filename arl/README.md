# ARL perffix (Attribute Rendering Library 3.2.0)

- Original mod: Attribute Rendering Library (modid `attributerenderinglibrary`), by Craluminum2413 (Dana), Nanotect, BowlSoldier.
- ModDB page: search the mod name "Attribute Rendering Library" (no URL found in local files).
- Unofficial patch. You need the original `AttributeRenderingLibrary-v3.2.0.zip`; nothing of the original is included here.

## What it changes and why
The patcher IL-patches the original DLL with Mono.Cecil; every other method stays the author's original IL. (The decompiled project does not recompile cleanly, so a source rebuild was not used.)
1. Precomputed `bool[]` "may have supplier" tables by block/item id, built in a new `Core.AssetsFinalize` and rebuilt at `SaveGameLoaded` / `LevelFinalize`. Blocks with a block entity, or with behaviours or `GetInterface` overrides that can supply properties, always take the original path.
2. The first line of 14 hot property postfixes (GetColor, GetRequiredMiningTier, GetBlockMaterial, GetLightHsv, GetCombustibleProperties, GetCrushingProperties, GetDamagedBy, GetGrindingProperties, GetNutritionProperties, GetTags, GetToolTier, GetTool, GetTransitionableProperties, Item.GetRandomColor) becomes an early return when the table says no supplier exists. Anything uncertain runs the original code.
3. `lock(pos)` removed from the GetBlockMaterial and GetLightHsv postfixes (suppliers only read `pos`).

Measured results: none documented for FPS or frame time. Documented checks: all 1194 methods JIT-compile; 25/25 behaviour checks pass against a fake world.

## Contents
- `helper/ArlFastPath.cs` + csproj: the fast-path class that is cloned into the DLL.
- `patcher/Program.cs` + csproj: Cecil patcher. Usage: `ArlPatcher <input AttributeRenderingLibrary.dll> <helper dll> <output dll> <game dir>`.
- `test/`: behaviour tests. The csproj expects the patched DLL at `..\patched\AttributeRenderingLibrary.dll`.

## Build
1. `dotnet build helper -c Release` (needs `VintagestoryAPI.dll` from your game dir).
2. `dotnet build patcher -c Release`, run it on your copy of the original DLL.
3. Put the patched DLL into a copy of the original zip (same modid and version) in place of the original DLL. Keep the original for rollback.
