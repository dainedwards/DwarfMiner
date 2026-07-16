# Creatures, combat, weapons and player controls

<!-- Split out of the old noita-sim note 2026-07-16. CLAUDE.md is the single source of
     truth and links here. Notes are dated and historical — trust the code over any line
     here, and correct the note when you find it stale. -->

## 2026-07-13 Noita-feel pass (branch `noita-sim`)

- **Creature env damage** (same day): Creature.Update probes SampleHazardsNear every 0.2s (random phase stagger) — lava 26 dps + burn, acid 12, fire 5 + burn, filtered by ImmuneTo (Fire now aliases the lava-immune kinds). Deliberately NOT wired to HitFlash (it doubles as digger provocation). Titans untouched (kaiju shrug hazards); no drowning for beached/landlocked creatures.

## 2026-07-14 combat/movement pass

- **Bandit enemies** (Creature.cs, enum tail: Marauder/Raider/Pyro): humanoids with WEAKER player weapons. Marauder = ground gunner (TitanShotKind.**Slug** — new enum member, small tracer, dmg 6, knockback 60); Raider = jetpack hover + 3-round SMG bursts (`_burst/_burstT` fields, drawn pack flame); Pyro = closes to 95px and hoses REAL Fire cells via `cells.LaunchAtWorld` (ImmuneTo Fire but NOT Lava). All share `_gunAim` field driving the drawn weapon. Spawn via cave census bands (surface Marauder 6%, mid Marauder+Raider, deep Pyro+Raider); corpse drops iron/coal/fuel.
- **Melee rung-4 cut raymarches** from the player outward per arc angle and bites the FIRST solid tile ≤0.95 reach (was fixed 0.85-reach samples that skipped near blocks).
- **Jump/jet Noita retune**: JumpSpeed 118, air accel 220, JetLift 150, rise caps 70/82/95/110; **tap-vs-hold** — `_jumpHoldTime > 0.2s` gates the jet, so taps just jump; **W/Up removed from jumpHeld** (Space only; W still climbs/swims, rocket-ascent thrust untouched).

## 2026-07-14 air/water/sea-monsters pass

- **Unified AIR meter**: Breath meter DELETED — merged into Player.Oxygen (the one "AIR" bar). Game1.TickAir replaces TickOxygen+TickBreath: drains at `AirDrainPerSecond` 8/s when `HeadInWater && !HasGills` OR `Def.Airless && !HasHelmet`; else refills (OxygenRules.RefillRate). `HasHelmet` = has the vacsuit upgrade (the pressure suit's sealed helmet → airless worlds breathe). `EffectiveMaxOxygen` now folds in lung tiers (×1.5/×2) alongside air-tank/O2 — lungs raise the AIR ceiling. Removed EffectiveMaxBreath/BaseMaxBreath/Breath field + DrownDps. HUD: one AIR bar (Renderer.DrawHudBars — breath bar block gone; AIR tints deep-blue while HeadInWater). RunSave unchanged (only Oxygen was ever saved). SimTest lung test → checks EffectiveMaxOxygen x1/1.5/2.
- **Hostile sea monsters** (Creature.cs, IsWaterKind extended): **AlienShark** (fast torpedo hunter, MoveSpeed 96, TickShark hunts a swimmer in-water else patrols), **Gulper** (deep anglerfish, glowing green lure via AddLight, TickGulper drifts then LUNGES 3.2× speed with `_lungeDir`), **Brinespitter** (aquatic artillery — TickBrinespitter lobs TitanShotKind.Acid globs as water slugs from ≤240px). Shared **SwimToward** helper (sets Velocity only — the Update substep integration moves+collides; must NOT touch Position). Spawn via SpawnDirector.**LakeKindFor** (deep water ~half hostile: whale24/shark26/gulper18/spitter14/crab; shallows shark22/crab). Draw cases + corpse drops added. SimTest "aquatic: shark hunts a swimmer" (40→0px).

## 2026-07-14 big combat/city/tools batch

- **Pickaxe**: cooldown uniform 0.24 at ALL tiers (was tier-1×1.5) — upgrades add STRENGTH (EffectivePickaxePower=tier), never speed.
- **Jetpack/jump SPLIT keys**: Player.Update now takes `jetHeld` separately. **W (or Up) = jump**, **Space (airborne) = jetpack** (W never lights the pack). Reduced jet accel (JetLift 110, JetInitialKick 35, rise caps 62-95). All Player.Update callers updated (+false).
- **Scanner is ACTIVATED** (not passive): "scanner" is a usable belt item (crafting grants it + ScannerTier=1, Use=DoScanPulse). Firing pulses (2.5s cooldown) → snapshots nearest deposit per detectable material within a tier-scaled radius (200→560px), marks each with an expiry = now + 15×tier seconds (up to 60s). `_geoScanHits` now (kind,pos,expiry); prune in update; expanding dotted ring drawn in the WORLD pass (NOT the screen overlay — that crashed: no active batch). GrantGodmodeItems adds the scanner item.
- **Energy blaster** (renamed nuke→"ENERGY BLASTER", item id stays "nuke"): CHARGE-UP weapon (FireEnergyBall). Ball visual (fired projectile Radius + charging muzzle orb) is ×0.35 of the original at every charge stage. Hold LMB (isEnergyBall branch, parallel to throwables) → _energyCharge 0→1 over 1.6s w/ rising hum + growing muzzle orb; release fires. **Non-linear** damage/size/blast/AlloyMinePower via `EnergyPower(c)=Pow(c,1.74)` (half=30%, full=100%). New alien-cannon held sprite + BuildNuke icon. Projectile draw = pulsing violet orb w/ orbiting sparks.
- **Alien metal blast resistance**: Projectile.AlloyMinePower (default 2) — CarveCrater uses it; Nuke sets 12 so a full energy ball breaks alien alloy/glass/brick in ~4 hits.
- **City**: saucers 4→6/district + ONE **BigSaucer** command ship per city (CreatureKind, 900hp, TickSaucer patrol; Game1 militia block fires a 3-laser fan + a constant tractor beam that reels+slows the player; draw = big hull w/ 3 belly turrets + beam cone). Civilians 3× (cluster 2-3 per address, budget 260, AlienHome cap 3→8). Non-civilian aliens 4× HP (Lizardman 120, Peacekeeper 208, Saucer 176). **Any alien kill aggros the city** (resident-kill wrath 35→60, over the 50 threshold; now includes Lizardman/BigSaucer). Tower fix: doors on BOTH sides at street level (dropped the doorSide gate), ladder shaft moved to CENTER column (was edge) full-height — CityProbe doors 180→360.
- **Weapons 30% smaller** (melee draw scale 1.37/0.91→0.96/0.64, guns 0.55→0.39) + clearer swing (smootherstep-eased arc + 3 fading motion-trail afterimages).

## 2026-07-14 blast + ladder pass

- **Center-weighted blast falloff**: new `Combat.BlastFalloff(t)=Clamp(1 - t*t, 0.1, 1)` (t=dist/ExplosionRadius). Replaced the old linear `1 - 0.6t` (center 1 / edge 0.4) at BOTH sites — Combat.ApplyExplosionDamage (creatures) and Game1 player self-blast (damage + knockback). Now center full, edge 10%, squared drop. Titan blast still flat 0.4x (no falloff).

## 2026-07-14 big 15-item combat/world/ecosystem batch (branch noita-sim)

- **Jetpack tier1**: JetChargeMax 0.75→1.0s; JetInitialKick 35→62 (boost pop); reworked to on/off Noita thrust — Space lifts you straight off the GROUND (no W-jump first), W plays no part in flying. SimTest "jetpack burn" now 1/2/3/5.
- **Lava sparks 99% cut** (Game1.TickHazardContact: EmitImpact rate dt*20→dt*0.2) + set Player.HurtFlash instead.
- **Player damage indicator**: new Player.HurtFlash (set in TakeDamage, decays), Renderer.DrawHurtVignette = red screen-edge bands, drawn before HUD.
- **Guns slower**: bullet/pistol/MG + gem cannons — lower projectile speed AND higher ShootCooldown. Bullets redrawn as small oriented rounds with bright tips, each gun slightly different (brass/gold/copper).
- **Arced creature projectiles**: new TitanShotKind.Dart (ballistic — Arcs, but doesn't Splash; Titan.cs Ballistic split into Arcs vs Splashes). Lizardman bone-spear → arcing Dart blowdart (lofted aim). AcidSpitter/Brinespitter already arced.
- **Less erratic jumping**: Creature._hopDir + IdleHopDir() (pick once, 1-in-5 reverse) for TickHopper/TickSlime idle hops; steadier cd variance.
- **3 new enemies** (Creature.cs, Noita-flavoured; enum tail Quillwing/Warpwisp/Thornback): **Quillwing** flyer fires a 3-Spike fan (reuses TickCaveEye); **Warpwisp** floating caster lobs slow wall-piercing Void hex bolts; **Thornback** ground beetle lobs high-arc Dart barb-mortars over walls (no LoS) + repositions. Full wiring: stats/tick/draw/census(mid+deep bands)/corpse drops.
- **Lizard villages**: warren halls cross-linked (i→i+2 tunnels + entrance→vault spine) for a connected network; **treasure chests** — new TileKind.Chest/ChestOpen (anchored, brass-banded art), placed on vault floor, E-to-loot via Game1.TryOpenChest/OpenChest (10-21 gold + a rare gem/silver, toast+sfx). Pack behavior: Lizardman shrieks CallingBackup when ATTACKED (HitFlash>0.12), not just on sight → Game1 war-cry rallies the warren. Spawn in pairs: LizardDoor spawner emits 2/trigger (cap 4), garrison 2-3/hall.

## 2026-07-14 tree-toughness / red-flash / snow follow-up

- **Player red flash**: player sprite draw (both _playerSprite anim + _dwarfTex paths) tints toward red by Player.HurtFlash (Color.Lerp White→(255,45,45)*0.85). (Screen-edge DrawHurtVignette call REMOVED 2026-07-14 per user — damage read is ON the dwarf only now; the Renderer method is left unused.)

## 2026-07-14 zoom / eruption / lizard / flame / gem-embed follow-up

- **Lizard darts reworked**: fire a 3-dart BURST in quick succession (reuses `_burst/_burstT`, 0.13s apart), SMALL darts on a BIG arc (loft 0.42+dist*0.0026, speed 200, dmg 5 each), ~2.8s between volleys. **No more lunging/jumping** — TickLizardman now holds a standoff dart-range (back off <90px, close in >190px, else plant and shoot). The dart "explosion" was Game1's blanket `EmitImpact(Cannon)` on every dead TitanShot — now Dart/Slug/Spike get a small `EmitImpact(Bullet)` puff, only heavy ordnance (flame/acid/lava/void) throws the cannon burst.
- **Warren cave exits**: WorldGen.CarveLizardCities bores a short brick corridor OUT of each hall into the surrounding cave-riddled crust (CarveTunnel, reach 120-210px), gated by a 3-tall DoorClosed at the mouth. Lizardmen (CanUseDoors) open them and range into nearby caves — warrens aren't sealed.

## 2026-07-14 lizardman-aim / vignette / gold-depth follow-up

- **Lizardman fixed**: moves more (tighter 105-150px hold band + moves to get an angle when LoS blocked, so it's not frozen); FACES the prey while hunting (Draw overrides `facing` from player.Position when _aggroT>0, and TickLizardman sets _gunAim at the player); shoots even POINT-BLANK and at LEVEL targets — fire min-dist 60→25, and the dart aim is now `dir + up*clamp(dist*0.0022, 0.05, 0.55)` (nearly flat up close, high arc only at range) with speed `190 + dist*0.4` so it carries before dropping.
- **Screen hurt-vignette removed** (see character-equipment note) — damage flash is the dwarf sprite only.

## 2026-07-15 fine-pixel-fidelity 10-pack (user: "do all those including bigger swings")

- **Creature WetSeconds/OilySeconds** (beside Burn/FreezeSeconds): set in the 0.2s hazard probe via CountWaterNear / new generalized `Cells.CountNear(pos,r,mat)`. Water-natives (ImmuneTo Water) NEVER get wet (would tint permanently). Wet 3s: douses burn, blocks ignition, halves fire dps; water rinses oil. Oily 8s: fire/LAVA contact burns 5s instead of 1.5. Tints in Draw's `Tinted` (oily dark 0.4, wet blue 0.25) below hit/freeze/burn priority.

## 2026-07-15 jump/jet controls REVERTED to Space-only (user request; supersedes the 07-14 "W-jumps/Space-jets split")

- **Space = jump AND jetpack throttle, Noita-style**: tap jumps; a grounded hold lights the pack after `JetHoldDelay` 0.18s; an AIRBORNE press lights it instantly (`_jetPressAirborne`, set on the press edge when `!Grounded && coyote expired` — coyote presses count as jumps). W/Up no longer jump — they only climb ladders, swim-stroke via verticalAxis, steer fly mode, and menu-navigate; rocket-ascent thrust still takes W/Up/Space.
- **Player.Update lost the `jetHeld` param** (5-arg again). Callers: Game1 play update, DM_JETTEST (now just `jumpHeld=true`), 6 SimTest sites (swim/platform). Titan-ride hop-off and grapple-cut moved W→Space (Game1 TickTitanRiding/TickGrapple).
- Pre-existing failures at this point (NOT from this change, verified in a worktree at the pre-edit commit): `city: tower facades draw dead straight (wobble 4.20px)` and `city: one capital plus smaller towns (groups 1/7/7/13)` — parallel session's territory, see [city-facades](city-facades.md).

