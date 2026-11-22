# REDLINE — Combat Overhaul for SIGNALIS

Dismemberment, crawling enemies, recorded weapon audio, persistent world blood, and a low-HP Ariane flash.

Covers EULR, STAR, ARAR and STCR, plus waist-split and decapitation finishes on the real KLBR. MNHR, KNCR and ADLR keep stock combat. Flare Gun and Thermite keep the game's fire response.

## Features

- **Dismemberment and ragdolls.** Shots test the posed body mesh and weakened limbs give way first. Heavy kills can split the waist or take the head. Finishing stomps can sever a part near contact.
- **Crawlers.** Losing a leg drops a Replika into a crawl instead of killing it. Losing both legs is fatal, and so is a lethal hit on a crawler. Crawlers swipe up close and can be finished with the stomp prompt.
- **Corpse revival.** Revival blends from the resting pose and reachable severed parts drag back into place, unless **Reassemble on revival** is off.
- **Weapon audio.** Recorded firing takes with rotation, a low thump under each report, wall-timed reflections and room reverb. Reload and handling sounds play from WAVs you add yourself (see below), otherwise stock cues play.
- **World blood.** Directional spray, spreading pools and wall splatter. Everything clears when you return to the main menu.
- **Ammo.** Pickups carry 50% extra rounds by default, applied after difficulty scaling.
- **Elster injuries.** Heavy damage can damage her leg actuator or tear off her left arm. Repair items restore the leg at 40 HP and the arm at 60 HP.
- **Ariane flash.** Hits that drop Elster to 1–15 HP can queue a brief portrait flash. The images cache automatically from your game files on first launch, so there is nothing to set up. Toggle under **F9 → Combat**, preview with F1.
- **Panel and test keys.** F9 opens settings at the title, in gameplay or in the pause menu. F8 previews a leg cut, F10 revives a corpse in the room.

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
| `PistolHandling` | `mag-insert.wav` and `slide-forward.wav` |
| `SmgHandling` | `mag-release.wav`, `mag-insert.wav`, and `action-close.wav` for the timed reload |
| `RevolverHandling` | `cylinder-eject.wav`, `cartridge-insert.wav`, `cylinder-close.wav`, and `inventory-reload.wav`. Eject, insert, and inventory reload also support an `-alt` take |
| `RifleHandling` | `breech-open.wav`, `cartridge-insert.wav`, and `breech-close.wav` |

## Known issues

- **Reinforcements are unreliable and default off.** Encounter spawns often fail silently, so new installs ship with `ReinforcementRate = 0`. Leave it at 0% unless you are testing it.
- **Blood stains on Elster do not appear.** World blood (spray, pools, walls) works; clothing stains currently do not show up. Under investigation.
- **Scripted revives vs heavy kills.** MNHR, KNCR and ADLR always keep stock combat and death, so the heavy-shot overkill bypass and stomp ragdolls never touch them. Standard enemies with a queued respawn are also excluded from overkill. Overkill still consumes revives by design, so it can permanently kill adds that a boss would otherwise pick back up — turn off **Heavy-shot overkill** under System for such fights if you want the scripted revives intact. If downed enemies stay dead when the boss should revive them, report the boss/room plus the `overkill decision` lines from the verbose log.

## Build

You need a .NET SDK that can build .NET Framework 4.7.2, plus Python. The project builds against the managed assemblies MelonLoader generates, and the skin map is generated from your installed game.

```powershell
python -m pip install -r requirements.txt
python scripts/package.py
```

For a custom Steam library, pass the directory containing `SIGNALIS.exe`:

```powershell
python scripts/package.py --game-dir "D:\SteamLibrary\steamapps\common\SIGNALIS"
```

The script writes `dist/REDLINE-0.3.0.zip` with the DLL, skin map, audio, default config, README and license. For a DLL-only compile against the default Steam install, run `dotnet build Source/Redline.csproj -c Release`.

## Credits

- Ben Jaszczak, Brian Nelson, Kevin Heras, Matthew Nanney — Free Firearm Sound Library recordings (CC0 1.0) behind the pistol, SMG, shotgun, revolver, rifle, flak and flare reports. See `UserData/RedlineAudio/CREDITS.md`.
- Will Leamon — fleshy fight excerpts behind the gore bed (OGA-BY 3.0).
- Iwan 'qubodup' Gabovitch — impact excerpts (CC0).
- opengameart.org donors — prepared firearm archive and track metadata linked in `UserData/RedlineAudio/CREDITS.md`.

## License

REDLINE's source and build scripts are [GPL-3.0-only](LICENSE). SIGNALIS and its dependencies ship under their own licenses. Source: [GitHub — SIGNALIS-REDLINE](https://github.com/Warexpor/SIGNALIS-REDLINE).
