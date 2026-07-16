# Ore, gems and mineral balance

<!-- Split out of the old noita-sim note 2026-07-16. CLAUDE.md is the single source of
     truth and links here. Notes are dated and historical — trust the code over any line
     here, and correct the note when you find it stale. -->

## 2026-07-14 air/water/sea-monsters pass

- **Gems reshaped** (user: smaller, no protruding tips, more diamond): Renderer.DrawGemCrystal AND Game1 loose-Pickup draw both rewritten to ONE clean compact diamond = rimmed rotated-square (rim 2.2-2.6px, body 1.5-1.9px) + facet + glint. Dropped all the `pos ± axis*N` protruding shard/tip rects and the elongated-shard/crown-facet variants. Loose pickups keep the travelling shine.

## 2026-07-14 big 15-item combat/world/ecosystem batch (branch noita-sim)

- **Gems rarer + crystals rarest**: ambient gem thresholds pushed high (Ruby .985/Sapphire .988/Emerald .990/Diamond .992/**Crystal .994 = rarest**), thinning &3→&7 (1/8 sites). Still embedded via SetGem (crystal caverns/geodes left as landmark set-pieces).
- **Gold/silver on EVERY world** (incl. alien homeworld), rarer than iron: base thresholds reachable (silver .945/gold .955, was 1.05 bias-only); coarse `metalSel` noise splits crust into gold-country vs silver-country so they rarely mix. StampRichVeins unchanged. SimTest "gold reaches every world" updated.

## 2026-07-14 ore-balance / gem-embed / tree-polish follow-up

- **Gold less common on rich worlds**: gold/silver OreBias values were tuned for the old UNREACHABLE base threshold; now the base is reachable everywhere so those biases flooded the crust. Slashed them — PlanetDef gold biases 0.13/0.125→0.05, silver 0.135→0.055, debug-world gold/silver 0.16→0.06, belt 0.13→0.06; PlanetGen sigBias gold 0.12→0.05, moon silver 0.14→0.055, rare-vein gold J(.07-.09)→J(.03-.045) silver J(.12-.15)→J(.045-.06). (verdant gold tiles 4648→1727.)
- **Platinum decorrelated**: WorldGen ore scatter samples a SEPARATE noise for platinum (`platN = SampleNoise(oreNoise, wx*.53+1234.5, wy*.53-987.6)`, threshold 0.978) so platinum veins no longer share seams with iron/gold/silver — its own isolated pockets.
- **Platinum recoloured** distinct from silver: base (206,198,172) warm champagne-pearl / glint (255,246,210), vs silver's blue-white.
- **Gems/crystals embedded, never solid blocks**: crystal-cavern inner shell (WorldGen geode + SeedBiomePockets) now lines with Stone STUDDED with SetGem(Crystal) instead of solid TileKind.Crystal tiles; warren vault centrepiece ruby is GoldOre + SetGem(Ruby). Ambient scatter already embedded. ("gems never generate as own tiles" test still green.)

## 2026-07-14 zoom / eruption / lizard / flame / gem-embed follow-up

- **Embedded gems blend into surrounding rock**: Renderer now renders a gem's host tile (embedded overlay OR legacy gem tile) as HostRockFor(neighbours) — the majority neighbouring rock — so a gem no longer reads as its own dark block set apart from the wall; it sits IN the surrounding material.

## 2026-07-14 lizardman-aim / vignette / gold-depth follow-up

- **Gold/silver pushed deeper**: base thresholds 0.945/0.955→0.955/0.962, min depth 30/40→46/56, depth boost 0.6→0.85 — scarce in the upper layers, still findable deep. (verdant gold 1727→1526.)

## 2026-07-14 doors / hose-rework / clouds / deep-caves batch

- **Rare minerals = smooth depth gradient** (replaces hard depth gates): new depthFrac (0 surface→1 deep) + a shallow threshold PENALTY (metals 0.075, gems 0.06). Gold/silver base 0.948/0.955, gems 0.982-0.994, low depth floors (12-20) so they're RICH deep, some in the mid crust, and very rarely in the upper layers. (verdant gold 578, emerald 8 — tests green.) Crystal still rarest.

## 2026-07-15 fire-vs-snow / water-quench / gem-bed pass

- **Gem drops sit in host sand**: SpawnDustInTile gem-tile branch now ALSO fills the tile with dust tagged with a neighbouring host rock (inner/left/right, first solid non-gem non-anchored, default Stone) — gem pickup rests in a bed of the material it was dug out of, not a black hole. (Embedded-overlay gems already dusted their host.) TestGemDrops updated: "leaves a bed of host-rock dust". Pickup draw: dark rim (body/2, 2.6px) → thin Lerp(body,black,0.35) outline at 2.2px — the old rim read as a black square backing.

