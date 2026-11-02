# OverhaulLib perffix (Overhaul lib legacy compat 1.2.15)

- Original mod: Overhaul lib legacy compat (modid `overhaulliblegacycompat`), by Maltiez and Alodh.
- ModDB page: the mod's own Spanish lang file links `https://mods.vintagestory.at/overhaullib` for the Overhaul Lib mod; the exact page for the "legacy compat" package was not found locally, so search the mod name.
- Unofficial patch. You need the original `overhaulliblegacycompat_1.2.15.zip`; nothing of the original is included here.

## What it changes and why
The patcher IL-patches the DLL with Mono.Cecil (3 target spots) and refuses to write anything if the IL is not exactly as expected.
1. Removes the empty `AnimationManager.OnClientFrame` postfix registration (`AnimationManagerOnClientFrame`, body was empty but ran for every animated entity every frame).
2. `AnimatorPlayerMap.Get` remembers the last animator it was asked about (per thread, with a version counter bumped by Add/Clear/constructor), so the `ConditionalWeakTable` lookup happens once per animator per pass instead of once per bone.
3. `DirectionCursorRenderer.OnRenderFrame` multiplies its scale by 4, so that the 8 direction cursor textures can be shrunk from 1024x1024 to 256x256 with unchanged on-screen size.

Measured results: none documented for FPS. Documented checks: all 6353 methods JIT-compile like the original; 10/10 behaviour checks for the new `AnimatorPlayerMap`.

## Not included (no separable script)
The cursor texture resize (premultiplied-alpha Lanczos, 1024 to 256 px) was done manually and is not scripted; the textures are third-party art and are not published. Without the resized textures, change 3 would make the cursor 4x too large, so apply change 3 only together with the resize. Patcher source: `patcher/Program.cs`. Usage: `OverhaulLibPatcher <input dll> <output dll> <game dir>`.

## Contents
- `patcher/`: Cecil patcher + csproj.
- `test/`: behaviour tests (`--all` JITs every method of a DLL you pass in).
