# Runtime MGFX shader (no toolchain) and the Noita carve

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

DwarfMiner has a custom pixel shader WITHOUT any shader toolchain (2026-07-14). Context:
MonoGame's mgfxc requires Wine on macOS (not installed, deliberately avoided). On the
OpenGL platform an "effect binary" is just the MGFX v10 container around plain GLSL TEXT
(Shader.PlatformConstruct ASCII-decodes it straight into glShaderSource), so
`src/Rendering/RuntimeEffect.cs` hand-writes the MGFX byte stream with a BinaryWriter and
feeds it to the public `Effect(GraphicsDevice, byte[])` ctor.

**Version-locked to MonoGame 3.8.2** (csproj pins 3.8.2.1105). A MonoGame bump will make
the reader reject the stream — BuildTerrainCarve catches everything and returns null, and
the renderer falls back to the baked-erosion atlas frames, so nothing breaks visibly.
If bumping MonoGame: re-verify the byte layout against the new tag's Effect.ReadEffect /
Shader(BinaryReader) and bump the version byte.

Traps found the hard way / verified against v3.8.2 source:
- **posFixup — the big one**: every MonoGame GL vertex shader MUST declare
  `uniform vec4 posFixup;` and end with the two-line epilogue
  (`gl_Position.y *= posFixup.y; gl_Position.xy += posFixup.zw * gl_Position.ww;`).
  GraphicsDevice.OpenGL sets it per-draw BY NAME on the active program (y = -1 when a
  render target is bound). Omitting it: geometry drawn into a render target is Y-flipped →
  winding reverses → CullCounterClockwise back-face-culls EVERY triangle → the whole batch
  is invisible with no error. Since the game renders the world into _sceneRt, that
  presented as "world barely renders, like a shadow/cloud" (only backdrop + lightmap +
  entity batches visible). The startup self-test draw does NOT catch it (backbuffer needs
  no flip) — a magenta-debug pixel shader that never shows = suspect culling, not the PS.
- **File TAIL**: after ReadEffect the ctor reads an extra int32 that must equal the MGFX
  signature (0x5846474D) — omitting it = "Unable to read beyond the end of the stream".
- GLSL compiles **lazily at first draw**, not at Effect construction — BuildTerrainCarve
  does a throwaway 1-px SpriteBatch draw inside its try/catch to force compile/link so a
  driver rejection falls back instead of crashing mid-frame.
- NO sampler table entries: GL uniforms default to 0 = texture unit 0, and SpriteBatcher
  re-sets Textures[0] AFTER pass.Apply(). Adding a sampler entry would make
  EffectPass.SetShaderSamplers null the slot (its parameter's texture is never set).
- Matrix passed as FOUR Vector4 COLUMN params (MatrixCol0..3), not a Matrix param —
  sidesteps SetValue(Matrix) transpose conventions; GLSL does clip_j = dot(pos, col_j).
  CPU sets columns of (view * CreateOrthographicOffCenter(0,vp.W,vp.H,0,0,-1)).
- SpriteBatch's Begin transformMatrix is INERT under a custom effect (its vertex shader is
  replaced) — vertices arrive in world space, which is exactly what gives the pixel shader
  its world position (vWorld varying) for world-anchored noise.
- Attribute table locations are placeholders; the GL runtime re-queries by NAME post-link.
  Names must match the GLSL attribute declarations; usages match VertexPositionColorTexture.
- Constant buffers upload as `uniform vec4 <cbufferName>[size/16]` located by the cbuffer
  NAME (vs_uniforms_vec4 / ps_uniforms_vec4).

**What the shader does** (the real Noita edge, replacing baked per-tile erosion): the tile
pass runs in its own batch under the effect (Renderer.DrawWorld splits the batch around the
ring loop). Base tile quads select atlas frame 0 (UNERODED) and flag their 4-bit
air-exposure mask in the vertex ALPHA: **a = 64+mask, flag bands [64..95]** — audited that
no other draw in that batch lands in this range (crack overlay is 102–191, glints 153;
keep it that way). Band [80..95] (2026-07-16, `carve-everywhere` worktree) = same mask but
the tile has LIQUID resting on a carved face: carved-away fragments render as darkened rock
(tex×0.55, rim skipped) instead of discard, so pools meet wet shadowed stone with no moat.
The pixel shader decodes the mask and carves each exposed edge inward by
world-space value-noise depth (discard), so the coastline is per-pixel and continuous
across tile seams. Non-flagged draws pass through exactly like SpriteEffect. Engineered
city tiles (Renderer.IsEngineered) stay unflagged → dead straight. The additive DrawCrust
fringe (outward) stays on top of the carve (inward).

Retuned 2026-07-15 after user "I don't see a difference" (first tuning matched the baked
erosion's scale/frequency, so it was invisible at parity by construction): carve is now a
TWO-OCTAVE noise — dominant ~8 px swell (CarveFreq 0.13) that bends the coastline coherently
across tile runs + 3.5× fine grain, `pow(n,1.3)` shaping. The long wavelength is the
shader's entire visible payoff — per-tile baked frames cannot wander across seams. Verify on
BARE rock (hollow asteroid is ideal): on grass surfaces the wrap/tuft decorations draw OVER
the carved edge and mostly hide it — expected, not a bug.

**Carve depth WAS a physics bound (sand hovers over carved dips); contact suppression
retired it** (2026-07-16, on worktree branch `worktree-carve-everywhere`, UNMERGED): the
tile pass probes the face-adjacent cell rows (3 spread samples per exposed face, majority
vote — Renderer.FaceContact/SideContact, same pattern as the wetness probe). GRANULAR
matter (Materials.IsCompactable) on a face clears that face's carve bit → pile seats flush
on the square boundary. LIQUID contact must NOT suppress (squared a submerged shoreline
into a blocky staircase — screenshot-proven); it keeps the carve and flags band [80..95]
gap-fill instead. Default top-face depth is now 2.0 px, side/ceil multipliers 1.2/1.4
(≈2.4/2.8 px). Creatures/pickups aren't cells so their feet still hover by local carve —
that's the remaining bound on going deeper. Get more organic via WAVELENGTH first.
Also: DrawCrust is FALLBACK-ONLY (`_noita && _tileFx == null`) — crust hugs the nominal
square boundary while the carve retreats the body inward, so crust+carve together read as a
detached grain outline tracing the tile grid around an empty moat. Never re-enable both.
True sand-fills-the-hollows would need the cell sim to sample the carve contour in
IsBlocked (hot path) + matching player collision — significant, offered but not built.

Round 2 (liquid-composite carve-mating dilation + deep BiteEdge v2 on ALL merge pairs +
wet wall-amp halving) was blanket-REVERTED at user request ("you screwed up snow" — the
deep fringe made drifts finger into dirt), but the user then hit the same blocky lake
bottoms again, so the LIQUID MATING HALF was REINSTATED verbatim (round 4, 2026-07-16):
LiquidGlsl carries 4 ps vec4s (WriteMgfx psVec4s param), reconstructs world pos as
screen×inverse-view (PsParams3/4, centre in PsParams2.zw), dilates coverage radially
outward by the SAME two-octave carve noise × ampO, rim on the dilated field; ampPx=0 when
the terrain carve is off. Deep-bite-on-all-pairs and wet-amp halving stay reverted.
Round 3 (what stands): (1) EARTH-ONLY textured fringe — BiteFringe draws the softer
neighbour's own ATLAS sub-rect (the strip adjacent to the shared edge, DrawDecoTex,
BlobShade tint at FULL alpha — flag-band trap) with world-space ValueNoise column depths
0..2px, dispatched only when BOTH families are earth (dirt 1 / gravel+Conglomerate 3 /
rock+LavaRock 4 — EarthPair); snow (2) and every other pair keep the ORIGINAL v1 hash
teeth. (2) Lake/acid basins are QUARTIC bowls now, see [worldgen-caves-and-lava](worldgen-caves-and-lava.md).
KNOWN REMAINING: tree trunk/canopy/root tiles are authored-art flat quads (UsesAuthoredArt)
— can't take the carve flag (deco texcoords don't map tile space), still read as square
block chains; slope stepping at strong zoom is tile GEOMETRY (4px), not edge rendering.

Hooks: `DM_SHADER=0` disables the shader (baked erosion returns), `DM_CARVE=<px>` sets max
carve depth in world px (default 2.0), `DM_NOITA=0` disables the additive crust,
`DM_CONTACT=0` disables the contact probe (raw carve everywhere, no liquid band).
Related: [textures-and-crust](textures-and-crust.md) (erosion/crust history), [cells-and-materials](cells-and-materials.md) (cell sim).

