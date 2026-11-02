# Footprints 30 s lifespan (perffix) - BUILD ONLY, not installed
Zip: 14_footprints\Footprints-v1.2.14-perffix.zip (353 files; python -m zipfile -t OK; patcher dotnet build 0 errors)
SHA-256: 22d419777184feec78cc4c5d4329c4a98d0e59b119bcd7c9fe2bf4345e2c8aa1 (also .sha256 file)
Source: stock Mods\Footprints-v1.2.14.zip (modid footprints, version unchanged). Patcher: 14_footprints\patcher (Mono.Cecil).

## Units / why config alone fails
- PlayerFootprintLifespan / AnimalFootprintLifespan are C# int properties, units = IN-GAME HOURS (Footprint.IsExpired: Calendar.TotalHours - CreatedGameHours >= lifespan).
- Live value 1 = 1 game hour. 30 s needs 0.25 h: not representable as int (Newtonsoft would round 0.25 to 0 = instant expiry; server sync also copies ints). Config alone CANNOT do it.
- Real seconds per game hour depends on world calendar speed; the prompt assumed ~120 s (so 1 = ~2 min). Verify in game.

## What changes
- IL patch, 2 methods only: Footprint.get_IsExpired and Footprint.get_RemainingLifeFraction. After the lifespan conv.r8, multiply by 0.25.
- New semantics: config value is in QUARTER game hours. An existing 1 / 1 setting now means 0.25 game h (~30 s at 120 s/game-h). No config edit needed; MaxFootprints 2000 / MaxVisible 400 and all other settings untouched.
- Safety checks in patcher abort if IL shape differs. IL diff vs stock: only those 2 methods plus RVA offset shifts. Footprints.pdb dropped (stale). Config/sync/ConfigLib types untouched (int stays int).
- Side effect: any other value N now means N*15 game minutes (default 6 = 1.5 h instead of 6 h).

## Perf effect (expected, unmeasured)
- ~4x fewer live footprints at a given walking pace (30 s vs ~2 min), fewer entries in LODManager scans and render lists. Gain is real only when the 400/2000 caps are not already binding; no before/after FPS measured.

## Install / uninstall (game closed)
- Install: move Mods\Footprints-v1.2.14.zip out (keep backup), copy Footprints-v1.2.14-perffix.zip into Mods. Do not keep both (same modid).
- Uninstall: delete the perffix zip, restore the stock zip. Config needs no change either way (set lifespan back to 1 = 1 h semantics returns automatically).
