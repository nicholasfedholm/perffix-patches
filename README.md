# perffix-patches

Unofficial performance patches for five Vintage Story (1.22.x) mods. Patch scripts by Nick.

**You need the original mods.** This repo contains no original code, no original or patched DLLs or zips, and no third-party assets. Each folder holds only our own tooling (Mono.Cecil IL patchers, small helper sources, test harnesses, build/pack scripts) and notes. You run the patcher against your own copy of the original mod, and keep the original zip for rollback. Patched builds keep the original modid and version, so never keep both in your Mods folder.

These are not endorsed by the original authors. Please support the original mods on the Vintage Story ModDB.

## Mods

| Folder | Original mod | Author(s) | Version patched | Fix | Measured result |
|---|---|---|---|---|---|
| `arl` | Attribute Rendering Library | Craluminum2413 (Dana), Nanotect, BowlSoldier | 3.2.0 | per-id "may have supplier" tables so plain blocks/items skip 14 hot property postfixes; `lock(pos)` removed from two postfixes | none measured; 25/25 behaviour checks |
| `overhaullib` | Overhaul lib legacy compat | Maltiez, Alodh | 1.2.15 | empty per-frame postfix removed; per-bone player-animator lookup cached per animator; direction cursor textures 1024 to 256 px | none measured; 10/10 behaviour checks |
| `watersheds` | Algernons Watersheds | Algernon | 7.0.4 and 7.0.6 | no `Unpack()` call for chunk columns loaded from disk | no FPS measured; behaviour tests 11/11 |
| `farseer` | Farseer | Badgerson | 1.4.0 | parallel, prioritised, cancellable far-terrain region sampling | 24.4% less wall time on the 24-region headless bench (843 to 637 ms), about 1.32x; single run, roughly +-10% noise |
| `footprints` | Footprints | Aylor222 | 1.2.14 | lifespan unit changed so footprints can expire in 30 s | none measured |

Each folder has its own README with the ModDB link (or how to find it), what changes and why, and build steps.

## Common build notes

- .NET 10 SDK. Projects find the game via `$(APPDATA)\Vintagestory`; override with `-p:VSDir=<game dir>` (C# test programs use the `VINTAGE_STORY` environment variable, else `%APPDATA%\Vintagestory`).
- Patchers take explicit arguments (input DLL, output DLL, game dir). They abort without writing if the original IL is not the expected shape (for example a different mod version).
- Worldgen-adjacent changes (Watersheds, Farseer) only affect newly generated chunks or regions.

## Private-only inventory (not published)

| Item | Why kept private |
|---|---|
| Original and patched DLLs, zips, `.pdb`, IL dumps (`il_orig.il`) | Third-party binaries |
| Decompiled source trees (`dec-orig`, `dec-patched`, `src`, game/mod decompiles) | Third-party code |
| Original mod assets and the resized OverhaulLib cursor textures | Third-party assets |
| OverhaulLib cursor texture resize | No separable script exists; done manually. Textures derive from third-party art |
| `watersheds-706/install_live.py` | Local helper that writes into a live Mods folder; not a patch tool |
| Other-session notes, audits, prompts, logs (farseer-perffix AUDIT/PLAN/prompts, farseer-run logs, benchmark data dirs) | Local working notes, not patch material |
| Footprints work dir `footprints-perffix` | Contains only the task prompt; the actual patcher lives in the audit folder (staged here) |

No patch script was excluded for embedding third-party code: the patchers use small anchor names (type/method names) to locate patch points and emit our own IL.
