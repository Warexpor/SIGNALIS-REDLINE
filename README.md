# REDLINE — Combat Overhaul for SIGNALIS

Dismemberment, crawling enemies, recorded weapon audio, persistent world blood, and a low-HP Ariane flash.

Covers EULR, STAR, ARAR and STCR, plus waist-split and decapitation finishes on the real KLBR. MNHR, KNCR and ADLR keep stock combat. Flare Gun and Thermite keep the game's fire response.

## Features

- **Dismemberment and ragdolls.** Shots test the posed body mesh and weakened limbs give way first. Heavy kills can split the waist or take the head. Finishing stomps can sever a part near contact.
- **Crawlers.** Losing a leg drops a Replika into a crawl instead of killing it. Losing both legs is fatal, and so is a lethal hit on a crawler. Crawlers swipe up close and can be finished with the stomp prompt.
- **Corpse revival.** Revival blends from the resting pose and reachable severed parts drag back into place, unless **Reassemble on revival** is off.
- **Weapon audio.** Recorded firing takes with rotation, a low thump under each report, wall-timed reflections and room reverb. Reload and handling sounds play from WAVs you add yourself (see below), otherwise stock cues play.
- **World blood.** Directional spray, spreading pools and wall splatter. Floor and wall stains blend with the scene as it is lit each frame, including moving character shadows. Stains survive revisits within a session and clear at the main menu.
- **Elster injuries and stains.** Heavy damage can damage her leg actuator, causing a limp and slower movement. Repair items restore the leg at 40 HP. Nearby gore or taking a hit can stain her suit, and healing clears the stains.
- **Mag caches.** A final kill or finishing stomp can drop a few rounds at the corpse for the gun that put it down. Caches use the game’s item catalog and are on by default. Toggle them under **F9 → Combat**.
- **Ariane flash.** Hits that drop Elster to 1–15 HP can queue a brief portrait flash. The images cache automatically from your game files on first launch, so there is nothing to set up. Toggle under **F9 → Combat**, preview with F1.
- **Panel and test keys.** F9 opens settings at the title, in gameplay or in the pause menu. Under **System**, opt in to **F8 cut preview** or **F10 revival test**. F10 can revive a standard Replika in the room, including a permanent kill.

## Requirements

Windows Steam build `20243420` of SIGNALIS with [MelonLoader 0.5.7 x64](https://github.com/LavaGang/MelonLoader/releases/tag/v0.5.7). Launch the game once after installing MelonLoader so it generates its managed assemblies, then close the game.

## Install

Extract the release ZIP straight into the folder containing `SIGNALIS.exe` so `Mods` and `UserData` merge. That gives you `Mods/Redline.dll`, `UserData/RedlineSkinMap.gz`, `UserData/Redline.cfg` and `UserData/RedlineAudio`.

To update, extract the newer ZIP the same way but keep your existing `UserData/Redline.cfg`. To uninstall, delete `Mods/Redline.dll`. Saves are untouched. Deleting `UserData/Redline.cfg`, `UserData/RedlineSkinMap.gz`, `UserData/RedlineAudio` and `UserData/RedlineAriane` removes all leftovers.

## Configuration

Press **F9** to open the panel. Click a tab or press **Tab** to switch sections, **Up/Down** to pick a row, **Left/Right** or the mouse to adjust. Closing the panel saves to `UserData/Redline.cfg`. Gameplay pauses while the panel is open. Press **R** then **Y** to reset everything to defaults.

**DiagnosticLog** is on by default and writes `UserData/Redline.verbose.log`. If something looks wrong, reproduce it and include the relevant lines in your report.

Optional handling recordings go under `UserData/RedlineAudio`. Enable **Weapon handling** and set **Handling volume** in the Audio tab. Missing files fall back to the game cues.

| Folder | WAVs |
| --- | --- |
| `ShotgunHandling` | `rack-open.wav` and `rack-close.wav` for the timed pump, plus `shell-insert.wav` and `reload-accent.wav` |
| `PistolHandling` | `mag-release.wav`, `mag-insert.wav`, and `slide-forward.wav` |
| `SmgHandling` | `mag-release.wav` and `mag-insert.wav` for the timed reload |
| `RevolverHandling` | `cylinder-eject.wav`, `cartridge-insert.wav`, `cylinder-close.wav`, and `inventory-reload.wav`. Eject, insert, and inventory reload also support an `-alt` take |
| `RifleHandling` | `breech-open.wav`, `cartridge-insert.wav`, and `breech-close.wav` |

## Known issues

- **Reinforcements are experimental and default off.** Spawning depends on the room, eligible enemies and navigation. The verbose log records the room and spawn decision.
- **Elster’s arm tear is disabled.** The leg injury remains available.
- **Scripted revives.** MNHR, KNCR and ADLR keep stock combat and death. The ARARs linked to the Mynah and Falke fights also keep native combat so the bosses can revive them. The overkill bypass skips enemies with a queued respawn.

## In-game checks

1. Take a hit that stains Elster, heal, then take another hit. Compare `suitBlood` and `worldBlood` in the frame timings and check that healing clears the stains.
2. Finish an enemy in a room with no matching ammo pickup. Check that the cache rests at the corpse, opens the native take prompt and grants the amount printed in the `mag cache` log. Turn off **Mag caches** under **Combat** and check that the next eligible kill leaves no ammo.
3. Use a heavy weapon on an ARAR during the Mynah or Falke fight. Check that the boss can still bring it back and look for `overkill preserved native boss revival` in the log.
4. Walk Elster or draw an enemy across fresh splatter, a pool and a dried stain. Check that the moving shadow stays continuous at each edge.
5. In an eligible room, test reinforcements with the slider enabled. The log includes a room summary even when no enemies qualify.
6. Check F9 on the title screen and in play. With the release defaults, F10 is idle. Enable it under **System** and check that it revives an ordinary corpse in the room.

## Build

You need a .NET SDK that can build .NET Framework 4.7.2, plus Python. The project builds against the managed assemblies MelonLoader generates, and the skin map is generated from your installed game.

```powershell
python -m pip install -r requirements.txt
python scripts/package.py
```

For a custom Steam library, pass the directory containing `SIGNALIS.exe`.

```powershell
python scripts/package.py --game-dir "D:\SteamLibrary\steamapps\common\SIGNALIS"
```

The script writes `dist/REDLINE-<version>.zip` with the DLL, skin map, recorded shots and gore sounds, default config, README, changelog and license. For a DLL-only compile against the default Steam install, run `dotnet build Source/Redline.csproj -c Release`.

## Source layout

`Source/RedlineMod*.cs` wires the game hooks and updates. `EnemyEffects*.cs`, `Rig.cs` and `SkinCuts.cs` implement enemy injuries and recovery. `CombatWorld.cs` provides the shared gameplay and multiplayer state checks, and `MagCaches.cs` builds ammo pickups from catalog items. `BloodEffects.cs` batches spray and world stains, `BloodMaterial.cs` controls their rendering, and `ClothingBlood.cs` projects stains onto Elster’s textures. `WeaponAudio.cs` handles shots and reloads, with room acoustics in `GunshotAcoustics.cs`.

`UserData/` holds release defaults and bundled audio. `Source/make_skin_map.py` generates bindings from the local game into `build/`. `scripts/package.py` assembles the player ZIP in `dist/`. See [CHANGELOG.md](CHANGELOG.md) for release changes.

## Credits

- Ben Jaszczak, Brian Nelson, Kevin Heras, Matthew Nanney — Free Firearm Sound Library recordings (CC0 1.0) behind the pistol, SMG, shotgun, revolver, rifle, flak and flare reports. See `UserData/RedlineAudio/CREDITS.md`.
- Will Leamon — fleshy fight excerpts behind the gore bed (OGA-BY 3.0).
- Iwan 'qubodup' Gabovitch — impact excerpts (CC0).
- opengameart.org donors — prepared firearm archive and track metadata linked in `UserData/RedlineAudio/CREDITS.md`.

## License

REDLINE's source and build scripts are [GPL-3.0-only](LICENSE). SIGNALIS and its dependencies ship under their own licenses. Source: [GitHub — SIGNALIS-REDLINE](https://github.com/Warexpor/SIGNALIS-REDLINE).
