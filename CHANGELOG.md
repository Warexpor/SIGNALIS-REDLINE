# Changelog

## [0.3.1] — 2026-11-26

### Fixed
- Elster clothing blood now actually paints. The stain projection had a flipped vertical axis, so no ink ever landed on the mesh.
- Blood spray, pools and cut surfaces now take scene fog into account instead of glowing through greyed-out rooms at full saturation.
- Enemies no longer slide across the room with frozen legs. A watchdog releases a stuck animator hold when nothing legitimate still claims it.
- STCR crawl deaths settle straight into the procedural crawl pose instead of freezing mid-fall. Crawl height is measured from each enemy instead of guessed from arm span, so tall units stop crumpling.
- Heavy hits now throw weight properly. Waist-split halves fly along the shot instead of dropping at the feet, severed limbs fling on any heavy final kill, and full ragdolls get more lift. Launch caps were raised to let the new speeds through.
- Shotgun pump rack is louder.
- Elster stains her own suit when she takes a hit (6+ damage, stronger at 15+), and healing washes the stains off.
- Stopped trusting the sticky main-menu flag. The game can leave it set during real gameplay, which silently disabled injuries, stains, reinforcements and debris push. Those systems now ignore it.
- Elster arm tear disabled for now (leg limp stays). Acquisition and save loading are both masked; re-enable the two marked spots to bring it back.
- Eligible final kills and stomps can drop ammo for the gun that put the enemy down. Caches use the game’s item catalog and keep their set quantity. Pistol 3, SMG 8, shotgun/revolver/rifle 2, flak/flare 1. Turn them off under **F9 → Combat**.
- Removed Elster shoving debris around. Severed parts stay where they fall; the setting, the panel toggle and the push physics are gone.
- Floor and wall blood blend with the lit scene every frame. New splatters, growing pools and dried stains pick up moving character shadows without rebuilding their meshes for lighting changes.
- Mag caches sample the ground beneath the corpse so they sit on the floor.
- Elster stains are lighter and wrap around. Smaller softer drops, lower opacity, a higher paint threshold, hit-scaled self-bleed, and the far side soaks through instead of staying clean.
- Heavy-shot overkill and stomp finishers no longer touch MNHR, KNCR or ADLR. Mynah and Falke’s ARAR minions keep native combat and revival, and overkill skips enemies with a queued respawn.
- Suit blood projects only triangles near the spray. Elster’s clean textures warm one at a time and stay cached through healing. Frame timing logs show suit blood separately from world blood.

### Changed
- F10 revival is opt-in on new installs and applies to standard Replika corpses. Existing F10 settings carry over.
- Reinforcements default to off (`ReinforcementRate = 0`) while spawns stay unreliable. Spawning no longer requires manager position tables, and the verbose log reports eligible enemies in each checked room, including rooms with none.
- Removed the ammo pickup bonus. The pickup and difficulty hooks are gone, along with the setting, the panel slider and the shipped default — pickups behave exactly as vanilla.

## [0.3.0]

- Corpse revival blends from the resting pose with limb reassembly, including burned and permanently killed enemies on F10.
- Knockdown recovery blends into the crawl when a leg is missing.
- Ariane flash on low-HP hits with automatic portrait caching and an F1 preview.
- Revised weapon handling timing, room reflections and gore foley.
