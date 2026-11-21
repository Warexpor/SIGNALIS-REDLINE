# REDLINE Combat Overhaul for SIGNALIS

## What's new in 0.3.0

- Replikas missing a leg now recover straight into a crawl after a knockdown when **Crawling** is enabled.
- Corpse revival blends from the body's resting pose and gives reachable severed limbs time to reattach. The F10 revival test also supports burned and permanently killed enemies.
- Elster can see a brief Ariane flash on hits that leave her at 1–15 HP. The effect is on by default, with a switch under **F9 → Combat** and a preview on F1. The six images are extracted from your local copy of SIGNALIS.
- Weapon handling audio has revised reload timing and room reflections. Wounds, impacts, stomps, and limb reattachment use revised gore Foley.

## Combat and effects

REDLINE expands combat with more ammunition, additional Replikas, injuries for Elster, and recorded weapon sounds. The **Combat** tab in the F9 panel controls the new features.

- **Ammo pickups** contain 50% more rounds by default, rounded up after the game's difficulty adjustment. The inventory still uses its normal stack limits and partial pickup behavior.
- **Reinforcements** add up to three Replikas to eligible combat rooms, depending on the number of stock enemies and nearby walkable space. They fight, take injuries, revive, and save through the normal enemy systems. Ammo and encounter changes apply in solo play.
- **Elster injuries** follow damage that actually reduces HP. Leg actuator damage causes a visible limp and reduces movement. A severe or cumulative hit at low health can tear off her left arm, leaving a loose limb and a stump that leaks onto the floor. Shooting one-handed lengthens recovery between trigger pulls and lowers the chance to stagger enemies. Using a repair item restores the leg at 40 HP and the arm at 60 HP. Injuries follow the save and apply in solo play.
- **Ariane flash** shows a shuffled pool of six red-background Ariane images. Each hit that lowers Elster to 1–15 HP has an 80% chance to queue one 0.12-second flash. At 2–15 HP it follows the next eligible red vignette crest or plays after 0.85 seconds if no crest is detected. A successful roll at 1 HP plays immediately as the death sequence starts. Press **F1** in a loaded room for a 0.32-second preview at any HP, including while paused. Both fades reach full opacity.
- **Weapon audio** rotates through recorded firing takes, including individual SMG rounds. Each custom report layers in a short, low-pitched thump. Nearby walls set the timing of stereo reflections and slower echo returns. Enclosed spaces add a reverb tail, with quieter returns for custom reloads and racks. Adjust the reflections and reverb with **Weapon room** in the Audio tab. See [the audio credits](UserData/RedlineAudio/CREDITS.md).

REDLINE adds dismemberment, ragdoll deaths, physics-driven detached limbs, crawling, and blood effects to SIGNALIS. EULR, STAR, ARAR, and STCR support limb cuts and ragdolls. Bodies killed by sufficiently powerful heavy hits render from a separate physics-driven mesh and keep their pose after settling. Each exposed stump bleeds independently from its cut edge. Movement leaves a trail from the wound, and repeated drips collect where the wound comes to rest.

The real KLBR can lose limbs to shots and stomps. A qualifying heavy kill can also launch a ragdoll or split her at the waist. The radio encounter, projections, hit detection, and her movement while alive use their native behavior. MNHR, KNCR, and ADLR use their stock combat and death animations. Flare Gun hits and Thermite Flare damage use the game's fire response, with no REDLINE cuts or ragdoll launch from the heat.

Cuts use the game's shot contact and the damage dealt. Bullet rays test the currently posed body triangles on supported enemies, including cut skins and both halves after a waist split. Crawling and recoverable downed enemies also accept their native hitbox when the lock-on ray misses the posed mesh. Clear limb hits weaken that limb, and a strong shotgun or flak hit can sever two nearby parts. The contact point also affects ragdoll spin.

Torso hits and confirmed shots with no precise contact build a separate damage total on that enemy. Once it reaches the cut threshold relative to full HP, a shot can roll for one cut. Heavy impacts and accumulated damage raise the odds. Weak shots can contribute, but a shot must deal at least 8% of full HP to make the roll. The selection favors the side hit and limbs weakened by earlier direct hits. A successful cut spends part of the accumulated damage. Head and waist cuts still require a terminal hit.

An enemy can lose an arm to a nonlethal hit and stay on its feet. A leg cut makes it crawl at 34% of its usual movement speed with both arms, or slower with one arm left. Losing both legs is immediately fatal. A lethal hit against a crawler is permanent and starts its collapse from the crawl pose. A living crawler can also be finished with the native stomp prompt. With `Ragdoll` enabled, a supported enemy goes limp from its current pose as a finishing stomp lands. An enemy that loses its weapon arm switches to a short, weaker swipe with its remaining arm. A crawler also swipes at close range. Each swipe has a windup, a brief contact window, and a recovery. An upright enemy with its weapon hand intact keeps its native weapon attack.

With `Ragdoll` enabled, a heavy shot that empties the target's health and deals at least its full HP (minimum 35 damage) can bypass the recoverable fall and kill it permanently. On a full-health target the chance ranges from 28% to 72% for the rifle and from 12% to 45% for other heavy weapons, based on excess damage. Prior damage raises the chance, up to a guaranteed bypass at or below 25% HP for a qualifying shot. When the roll misses, the game applies the full shot damage and resolves its normal downed or death state. Heavy shots can launch a ragdoll if they deal at least 20 damage and at least 54% of full HP and the game confirms death on that shot. The rifle pushes farther along the floor.

Heavy fatal hits can split an enemy at the waist or decapitate it, even when lock-on provides no precise contact. Hits near the waist or head favor their respective cuts. These cuts also detach another available limb. A confirmed heavy kill that deals at least 62% of full health at the default cut sensitivity can fall back to another available limb if the first cut fails. A split preserves existing limb wounds and gives each capped half independent physics and short-lived bleeding. Detached parts settle into static debris and remain through room and level revisits during the current session. Returning to the main menu clears them. A confirmed stomp can sever an intact part near contact. REDLINE plays distinct Foley for bullet impacts, cuts, limb reattachment, and stomps. Each fresh wound can play one wet cue while the visual bleeding continues.

Decapitation and waist splits permanently kill the affected Replika and block automatic revival. Replikas keep their injuries after a recoverable knockdown, and their severed limbs stay where they fell. With crawling enabled, a Replika missing a leg blends from the grounded pose directly into a crawl when the recovery ends. When a corpse revives, reachable severed parts twitch, drag across the floor, and pull back into place. Each piece closes the wound when it joins. Parts on another floor or behind a wall stay detached. Physics corpses blend from their resting pose into the game's get-up animation or into a crawl if a leg is still missing and crawling is enabled. The effect can use matching debris on a room revisit during the same session. Turn off **Reassemble on revival** in **Injuries** to keep limbs detached after corpse revival.

Elster can nudge corpses, split halves, and severed limbs by walking into them. Grounded debris slides briefly, and solid room geometry constrains travel. The crawl and swipe are procedural poses layered over the stock animation. Gunshot spray follows the bullet's direction in a cone and leaves streaks and drops where it strikes the floor or walls. Other impacts spread more broadly and can fan toward a nearby wall. Stains, pools, and cut surfaces use the same blood palette and room-light sampling. Pools start beneath the grounded body or wound and spread over roughly nine seconds, with a dark rim and a subtle wet sheen. Floor and wall stains and dried pools stay in place through room and level revisits for the current play session. Returning to the main menu clears them.

Fatal shots within short to medium range and finishing stomps can stain Elster with blood. The splatter gets lighter with distance and lands on the side facing the enemy. Repeated kills build up separate stains that dry and fade independently over roughly 25–45 seconds.

## Build

Use the Windows Steam build `20243420` of SIGNALIS, [MelonLoader 0.5.7 x64](https://github.com/LavaGang/MelonLoader/releases/tag/v0.5.7), a .NET SDK that can build .NET Framework 4.7.2, and Python. The project uses the managed assemblies generated by MelonLoader on its first successful launch. The skin map is generated from your installed game.

From the repository root, install the pinned Python dependency and build a local package.

```powershell
python -m pip install -r requirements.txt
python scripts/package.py
```

For a custom Steam library, pass the directory containing `SIGNALIS.exe`.

```powershell
python scripts/package.py --game-dir "D:\SteamLibrary\steamapps\common\SIGNALIS"
```

The script writes `dist/REDLINE-0.3.0.zip` with the DLL, skin map, weapon and gore audio, default config, source, and license. To compile just the DLL against the default Steam installation, run `dotnet build Source/Redline.csproj -c Release`. For a custom location, add `-p:GameDir="D:\Path\To\SIGNALIS"`.

## Install and configure

With SIGNALIS closed, copy `Mods/Redline.dll`, `UserData/RedlineSkinMap.gz`, and the `UserData/RedlineAudio` directory from the package into the corresponding folders next to `SIGNALIS.exe`. Copy `UserData/Redline.cfg` on first install, or keep your existing config when updating. Move `Mods/Redline.dll` out of `Mods` to disable REDLINE.

**Ariane flash** is on by default. The switch under **F9 → Combat** controls both the automatic flash and the F1 preview. Both need images from your local game, extracted with the `Source/extract_ariane.py` script from the repository. Without those images the flash stays off and everything else works. To extract, install the Python dependency from the Build section and run this from the repository root. Substitute your game folder if you use a custom Steam library. The script extracts the images into the game's `UserData/RedlineAriane/` directory. Older installs can still use `UserData/RedlineAriane.png` as a single-image fallback.

```powershell
python Source/extract_ariane.py "C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS"
```

Press **F9** at the title screen, during gameplay, or from the pause menu to open REDLINE's settings. Click a tab or press **Tab** to switch sections. Use **Up** and **Down** to select a row and **Left** and **Right** to adjust it, or use the mouse. Close with **F9**, **Esc**, or **Save & Close** to save the settings to `UserData/Redline.cfg`. Opening the panel during gameplay pauses the game until you close it.

Optional solo handling recordings go under `UserData/RedlineAudio`. Use the **Weapon handling** switch and **Handling volume** slider in the Audio tab. Missing recordings fall back to the game cues.

| Folder | WAVs |
| --- | --- |
| `ShotgunHandling` | `rack-open.wav` and `rack-close.wav` for the timed pump, plus `shell-insert.wav` and `reload-accent.wav` |
| `PistolHandling` | `mag-insert.wav` and `slide-forward.wav` |
| `SmgHandling` | `mag-release.wav`, `mag-insert.wav`, and `action-close.wav` for the timed reload |
| `RevolverHandling` | `cylinder-eject.wav`, `cartridge-insert.wav`, `cylinder-close.wav`, and `inventory-reload.wav`. Eject, insert, and inventory reload also support an optional `-alt` take |
| `RifleHandling` | `breech-open.wav`, `cartridge-insert.wav`, and `breech-close.wav` |

Select **Reset defaults** (or press **R**) and confirm **Reset all** to restore every REDLINE option. Close the panel to save the restored values.

- **Blood** covers overall density, impact spray, pool size, wound bleeding, Elster's clothing stains, and wall splatter.
- **Combat** covers extra rounds, reinforcement density, Elster's injuries, and the Ariane flash.
- **Injuries** covers dismemberment, crawling, sensitivity to limb damage from shots, decapitation, waist-split chance, and reassembly on revival.
- **Physics** covers ragdolls, extreme launches, throw speed, vertical lift, spin, and whether Elster can push severed limbs. Walking into a corpse leaves it in place.
- **System** covers heavy shots that bypass recoverable falls, gore sound volume, diagnostic logging, and the F8 and F10 test hotkeys.

Press **F10** during gameplay to revive a corpse in the current room, including burned enemies, severed crawlers, and other permanent deaths. The test prioritizes corpses with a reachable severed part when **Reassemble on revival** is enabled. To disable the hotkey, turn off **F10 revival test** under **System**.

`ExtremeRagdoll` defaults to off and requires `Ragdoll`. It gives heavy hits more knockback, lift, and spin within capped launch speeds. Finishing stomps use a short, ground-directed shove. The three launch sliders scale newly launched bodies up to their limits. Multipliers range from 0× to 2×.

You can also edit `UserData/Redline.cfg` while the game is closed. `Blood` ranges from `0` to `2.5`. `DiagnosticLog` writes a fresh `UserData/Redline.verbose.log` each launch with shot and enemy IDs, state transitions, mesh cuts, floor samples, physics contacts, and blood counts. `ScenePreviewF8` enables an F8 test that previews a leg cut on a nearby active enemy without reducing its HP.

`WaistCutChance` defaults to `0.42` and requires both `Dismemberment` and `Ragdoll`. The Shotgun, Flak Gun, Rifle, and Revolver qualify as heavy weapons. A waist-level hit has the highest chance, followed by a torso or unlocated hit. Set it to `0` to disable waist cuts.

## License

REDLINE's source and build scripts are licensed under the [GNU General Public License v3.0 only](LICENSE) (`GPL-3.0-only`). SIGNALIS and its dependencies are obtained separately under their respective licenses.
