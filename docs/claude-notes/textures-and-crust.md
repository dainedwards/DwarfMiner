# Texture pipeline and the additive crust

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

DwarfMiner's art is a hybrid (user's chosen direction, implemented 2026-07-06): downloaded
CC0 texture packs provide pixel detail, the game's own palette provides color.

- Tiles: `TileAtlas.ComposeHybrid` loads `assets/tiles/*.png` (16×16) and remaps each
  pixel's luminance onto `Tiles.BaseColor(kind)` (ore nuggets onto `Tiles.OreSpeckle` via
  a saturation>45 split; grass/moss via a greenness split). Kinds without a source file
  — or a missing assets folder — fall back to the original procedural generator.
- Terraria-style continuity (added later on 2026-07-06): the 4 atlas variants of pack-art
  kinds are half-size rolls of the seamless source, picked by tile parity
  (`Renderer.VariantFor`: `(t&1)|((r&1)<<1)`) so the pattern flows across tile boundaries;
  procedural kinds keep the hash pick. Hybrid tiles bake NO top-lit gradient or grain —
  a per-tile gradient redraws the tile grid. Grass never rolls vertically (sky-edge strip
  must stay row 0).
- Edges (final state after several user rejections): NO painted decoration at
  block-vs-air edges — no outline, no lip/bevel, no underside shade. The user rejected
  every per-tile edge accent (light AND dark) as "painted rectangles per block". Do not
  reintroduce per-tile edge strips. Instead: baked SILHOUETTE EROSION — the atlas holds
  4 rolls × 16 exposure masks per kind (`TileAtlas.MaskCount`, bit0 outer/1 inner/2
  left/3 right); exposed edges get ragged alpha cut-outs (0–2 art px), corners chamfered
  ~3 px, so silhouettes round organically, showing background through. Renderer computes the
  mask from the sky flags (moved above the tile draw).
- DE-BLOCKING, what NOT to repeat (2026-07-14): a Terraria full-45°-corner-slope pass
  (`TileAtlas.Corner` → Res-1 cut on clean convex corners) was tried and REJECTED and fully
  backed out. Being VISUAL-ONLY (alpha cut, square physics) it opened big empty triangles —
  underground they revealed the skybox (no back-walls), and the live falling-sand layer,
  which collides on the square tile, rested/poured through the cut region. Lesson: any
  visual-only silhouette change that CARVES INWARD fights the sand sim and the missing
  back-walls. Do not reintroduce inward cuts/slopes without real slope-aware collision.
- Noita crust (current, 2026-07-14, replaces the slope attempt): `Renderer.DrawCrust` roughs
  the air-facing silhouette into a continuous per-pixel coastline by scattering 1-px
  terrain-coloured cells 1–2 layers OUT into the void along each sky edge, kept/skipped by a
  world-space value-noise field (`Renderer.Noise2`, freq 0.32, thresholds {0.40,0.66}) so
  neighbouring tiles form one wandering edge and the grid dissolves. PURELY ADDITIVE — only
  paints over already-drawn background, never carves — so it can't open a triangle or a
  sand-gap (tile stays a full solid square to physics). Drawn with direct world math (NOT
  DrawDeco, whose 0..7 caller coords vs TileSize=4 divisor are inconsistent). Excludes
  engineered city tiles (AlienAlloy/CityGlass/LizardBrick) and the authored-art kinds. On by
  default; `DM_NOITA=0` reverts to the plain atlas-eroded edge. Subtle on flat grassland
  (verdant spawns at -π/2 = flat); reads best on relief/caves. Runs can't be A/B'd pixel-
  identically — the sand sim uses Random.Shared so worlds diverge each launch.
- Engineered city tiles straight (2026-07-14, user: "buildings all jagged"): AlienAlloy /
  CityGlass / LizardBrick are machined, not geological — `Renderer.IsEngineered(k)` gates
  them out of the silhouette erosion (TileAtlas.BlitEroded forces `mask = 0` for them → full
  crisp squares, no ragged alpha/chamfer), the surface-crumb scatter, and the Noita crust.
  Residual tower-outline stepping is worldgen (polar band-boundary tile-count halving +
  intentional slab-rib ledges in `WorldGen.BuildTower`), NOT tile rendering — don't chase it
  in the renderer. Doors (DoorClosed/DoorOpen, authored-art path) were reskinned from teal
  sci-fi panels to a warm timber PANELLED door (dark stiles, recessed panel, full-height
  brass pull; open = dark doorway + folded-back leaf) — every element runs full tile height so
  a door built 2–3 tiles tall reads as one continuous leaf. Doors gen at tower BASES (street
  level, both edges) in BuildTower.
- DrawDeco 2× bug FIXED 2026-07-15 (user's long-standing "random floating pixels to the
  right of blocks, disappear when mined" — originally misdiagnosed as flying cells): all
  deco art is authored in an 8×8 reference grid, but DrawDeco divided by Planet.TileSize
  (4 since the tile halving) → every decoration drew 2× scale anchored top-left, spilling
  one tile right + one inward. Spill was overdrawn by later-drawn solid neighbours but
  FLOATED where it hit sky on the right (crumbs/tufts/glints on surface tiles). Fix:
  DrawDeco maps by `DecoGrid = 8` on both axes; the damage-crack Walk (the one 4-grid
  caller) had its constants doubled to match. ALL deco art now renders at authored size —
  half its previous on-screen size; if any specific art now reads too small, re-author that
  art's coords, do NOT touch DrawDeco's divisor again.
- Plus crumb accretion: 0–3 material-
  coloured grains scattered on exposed top surfaces (not grass — it has tufts). Grass
  still wraps exposed sides/undersides. Corner AO dots kept. Lighting was checked — it's
  already a smooth world-pixel field (Lighting.cs), no per-tile snapping to fix.
- Back-walls are NOT RENDERED at all (user: "too blocky" — tried dim atlas walls, then
  soft baked rim shadows, both rejected). Instead `Renderer.MakeStarfield` bakes a
  tileable 256px pixel night sky (nebula washes + 3 star tiers, brightest with cross
  glints), drawn screen-space with PointWrap at integer camera zoom and 0.15× parallax
  before the world. Dug openings, caves and the horizon all show this skybox (lighting
  darkens it underground). Wall DATA (Planet.GetWall/SetWall) still exists and is used
  by Game1/SimTest inside-planet checks — only the rendering is gone.
- Backdrop is layered Noita-style (back→front): elevation-banded clear colour + starfield
  → `MakeAtmosphere` radial shell (premultiplied disc, alpha 0 through the crust, peak
  ~0.68 planet radii = mean surface, gone by 0.93 so mountains poke out; teal horizon →
  violet aloft) → `DrawHazeWisps` (44 soft stretched blobs orbiting at staggered
  radii/speeds in two tints) → terrain. All at the top of DrawWorld.
- Sky changes with elevation (camDist / planet radius): backdrop cave-black < 0.60,
  dusk blue 0.70–0.80 (surface sits at ~0.68), fading to space-dark by 0.95; the
  starfield alpha ramps 0→1 over 0.82–0.96, so NO stars are visible from the surface or
  in dug caves — they only fade in high in the atmosphere (user requirement). Mountain
  peaks (up to ~0.99) reach full starfield.
- Material separation = `BiteEdge` interpenetration, not lines: where `MergeGroup`
  families differ, the SOFTER material (`Bites`: lower hardness wins, enum tie-break)
  paints ragged 1–2 px teeth of its colour into the harder tile's edge — inside the
  harder tile's own bounds (teeth pushed into a neighbour would be overdrawn by later
  draw order: rings inner→outer, angles ascending). A dither stripe and a blended-dot
  version were both tried and rejected. Interior pack detail at 0.5.
- Anti-pebble/repetition pass (same day): sources swapped to smoother pack art —
  stone=`slate`, granite=`schist`, dirt=`mud`, basalt=`basalt_flow` (user found cobble
  sources too pebbly; gabbro/diorite/limestone rejected as noisy). Ore tiles composite:
  nugget pixels from the ore source, background pixels from the *stone* source, so veins
  are texture-continuous with surrounding rock. `Renderer.BlobShade` (2-octave value
  noise, ~7- and 3-tile wavelengths, darken-only ≤12%) multiplies both foreground and
  back-wall atlas draws to break the 2-tile pattern repeat on large faces.
- Player: `PlayerSprite.TryLoad` loads `assets/player/{idle1-3,run1-6,jump,fall}.png`
  (16×16, faces right), auto-measures opaque bounds for feet alignment and scale
  (~7.5 world px tall). Kept in its authored palette deliberately — recoloring flat-color
  character art hurts readability. Fallback: the old string-art dwarf in Game1.
- Sources (both CC0, recorded in assets/CREDITS.md): "16x16 Block Texture Set" by
  ARoachIFoundOnMyPillow and "Treasure Hunter pack 16x16" by HDST, both from OpenGameArt.
- csproj copies `assets/**` to output; no MonoGame content pipeline (.mgcb) — PNGs load
  at runtime via Texture2D.FromStream.
- Visual verification trick: run with `DM_AUTOSHOT=<seconds>` env var; screenshots land in
  `bin/Debug/net8.0/screenshots/`. macOS has no `timeout` — background the run and kill it.

