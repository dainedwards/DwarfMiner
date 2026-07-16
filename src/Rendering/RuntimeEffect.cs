using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Builds the terrain-carve pixel shader AT RUNTIME, with no shader toolchain.
///
/// Why: MonoGame effects normally require mgfxc, which needs Wine on macOS (it hosts the
/// D3D compiler) — a heavyweight machine dependency this project deliberately avoids (no
/// content pipeline; everything loads at runtime). But on the OpenGL platform an "effect
/// binary" is just the MGFX container around plain GLSL TEXT: Shader.PlatformConstruct does
/// nothing but ASCII-decode the "bytecode" and hand it to glShaderSource. So this class
/// hand-writes the MGFX v10 stream around hand-written GLSL and feeds it to the public
/// <c>Effect(GraphicsDevice, byte[])</c> constructor.
///
/// The byte layout mirrors Effect.ReadEffect / Shader(BinaryReader) in MonoGame 3.8.2
/// EXACTLY (verified against the v3.8.2 tag source):
///   header:  "MGFX", version byte 10, profile byte 0 (OpenGL), int32 effectKey
///   int32 cbufferCount { string name, int16 size, int32 nParams { int32 paramIdx, uint16 offset } }
///   int32 shaderCount  { bool isVertex, int32 len, GLSL bytes,
///                        byte samplers { … }, byte cbuffers { byte idx },
///                        byte attributes { string name, byte usage, byte index, int16 loc } }
///   parameters (recursive: int32 count { class, type, name, semantic, annotations=0,
///                        rows, cols, elements=0, structMembers=0, float data… })
///   int32 techniques   { name, annotations=0, int32 passes { name, annotations=0,
///                        int32 vsIdx, int32 psIdx, bool×3 (no render-state blocks) } }
///
/// Deliberate simplifications, each grounded in the 3.8.2 runtime:
///  - NO sampler table entries: GL guarantees uniforms default to 0, so the lone sampler2D
///    reads texture unit 0 — and SpriteBatcher re-sets Textures[0] = sprite texture AFTER
///    pass.Apply(), so nothing needs to (or should) touch the texture from the effect. This
///    also sidesteps EffectPass.SetShaderSamplers, which would null the slot otherwise.
///  - Attribute table locations are written as 0: ShaderProgramCache queries real locations
///    post-link by NAME (GetVertexAttributeLocations), so only names/usages must be right.
///  - The view×projection matrix is passed as FOUR Vector4 COLUMN params instead of a Matrix
///    param, sidestepping EffectParameter.SetValue(Matrix)'s transpose conventions entirely:
///    clip_j = dot(pos, column_j) is unambiguous.
///
/// Version-locked to MonoGame 3.8.2 (csproj pins 3.8.2.1105): the reader rejects any version
/// mismatch loudly, and BuildTerrainCarve catches everything and returns null — the renderer
/// then keeps its baked-atlas erosion path, so a future MonoGame bump degrades gracefully.
/// </summary>
public static class RuntimeEffect
{
    /// <summary>Build the terrain-carve effect, or null if anything goes wrong (wrong
    /// MonoGame version, GLSL rejected by the driver, …) — callers must treat null as
    /// "no shader" and keep the baked-erosion path.</summary>
    public static Effect? BuildTerrainCarve(GraphicsDevice gd) =>
        Build(gd, PixelGlsl, "TerrainCarve", "terrain carve", psVec4s: 2);

    /// <summary>Build the liquid-composite effect (threshold + rim + body texture over the
    /// liquid RT), or null — callers fall back to a plain NonPremultiplied blit of the RT.</summary>
    public static Effect? BuildLiquidComposite(GraphicsDevice gd) =>
        Build(gd, LiquidGlsl, "LiquidComposite", "liquid composite", psVec4s: 3);

    private static Effect? Build(GraphicsDevice gd, string pixelGlsl, string technique,
        string label, int psVec4s)
    {
        try
        {
            var fx = new Effect(gd, WriteMgfx(pixelGlsl, technique, psVec4s));
            // Touch the parameters now so a malformed stream fails HERE (inside the
            // try) rather than mid-frame on first use.
            _ = fx.Parameters["MatrixCol0"];
            _ = fx.Parameters["PsParams"];
            _ = fx.Parameters["PsParams2"];
            if (psVec4s >= 3) _ = fx.Parameters["PsParams3"];
            // GLSL compiles LAZILY at the first draw (glShaderSource/link happen inside
            // ApplyState) — so force one degenerate draw through the effect now. A driver
            // that rejects the GLSL throws here, inside the guard, instead of crashing the
            // game mid-frame on the first visible tile.
            using (var px = new Texture2D(gd, 1, 1))
            using (var sb = new SpriteBatch(gd))
            {
                px.SetData(new[] { Microsoft.Xna.Framework.Color.White });
                sb.Begin(effect: fx);
                sb.Draw(px, Microsoft.Xna.Framework.Vector2.Zero, Microsoft.Xna.Framework.Color.White);
                sb.End();
            }
            return fx;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[shader] {label} unavailable, using fallback path: {e.Message}");
            return null;
        }
    }

    /// <summary>Vertex shader: SpriteBatch feeds world-space quads (its transformMatrix is
    /// inert when a custom effect replaces its vertex shader), so this applies view×ortho
    /// from the four column params and passes the raw world XY through to the pixel shader —
    /// that world position is what anchors the carve noise to the terrain.</summary>
    private const string VertexGlsl = @"#version 110
attribute vec4 inPosition;
attribute vec4 inColor;
attribute vec2 inTexCoord;
uniform vec4 vs_uniforms_vec4[4];
uniform vec4 posFixup;
varying vec4 vColor;
varying vec2 vTexCoord;
varying vec2 vWorld;
void main()
{
    vec4 p = vec4(inPosition.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]),
                       dot(p, vs_uniforms_vec4[1]),
                       dot(p, vs_uniforms_vec4[2]),
                       dot(p, vs_uniforms_vec4[3]));
    // MonoGame's standard GL epilogue: the device sets `posFixup` per-draw BY NAME on the
    // active program (GraphicsDevice.OpenGL) — y = -1 when rendering into a render target,
    // which both un-mirrors the image AND restores the triangle winding. Omitting this
    // back-face-culled the whole batch inside the scene RT (invisible world).
    gl_Position.y = gl_Position.y * posFixup.y;
    gl_Position.xy += posFixup.zw * gl_Position.ww;
    vColor = inColor;
    vTexCoord = inTexCoord;
    vWorld = inPosition.xy;
}
";

    /// <summary>Pixel shader. Behaves EXACTLY like SpriteEffect (tex × tint) unless the
    /// vertex alpha byte is in [64..79] — the renderer's flag band, chosen because no other
    /// draw in the tile batch lands there. Flagged quads decode alpha−64 as the 4-bit
    /// air-exposure mask and carve each exposed edge inward by a world-space value-noise
    /// depth, discarding carved fragments. Because the noise is sampled at WORLD position,
    /// the carved coastline runs continuously across tile seams — the actual Noita edge.
    /// On top of the straight edge carve: convex CORNERS (two adjacent edges exposed — a
    /// stair-step of the tile grid) are additionally cut back radially, dissolving the
    /// 90° staircase on diagonal terrain, and the fragments just inside the resulting
    /// coastline darken in two hard 1-px bands — the Noita rim crust, glued to the real
    /// carved boundary instead of the nominal tile square.
    /// PsParams  = (atlasFramesX, atlasFramesY, top-face carve amplitude in frame
    /// fraction, noise frequency per world px).
    /// PsParams2 = (ceiling amplitude, wall amplitude, crust band width in frame
    /// fraction, unused) — the top face keeps the sand-resting physics bound while
    /// walls/ceilings carve deeper. Lattice hashes wrap at 289 px so sin() precision
    /// holds far from the origin (the repeat is invisible at 2–3 px feature size).</summary>
    private const string PixelGlsl = @"#version 110
uniform sampler2D s0;
uniform vec4 ps_uniforms_vec4[2];
varying vec4 vColor;
varying vec2 vTexCoord;
varying vec2 vWorld;
float vhash(vec2 p)
{
    p = mod(p, 289.0);
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}
float vnoise(vec2 x)
{
    vec2 i = floor(x);
    vec2 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(vhash(i),                  vhash(i + vec2(1.0, 0.0)), f.x),
               mix(vhash(i + vec2(0.0, 1.0)), vhash(i + vec2(1.0, 1.0)), f.x), f.y);
}
float carve(vec2 w, vec2 off)
{
    // Two octaves: a dominant long swell (~8 px at the base frequency) that bends the
    // coastline coherently across whole runs of tiles, plus fine grain at 3.5x so the
    // swell's surface stays rough. Mild pow shaping leaves stretches near depth zero, so
    // the edge still touches the nominal tile line here and there instead of shrinking
    // the whole silhouette uniformly.
    float n = 0.7 * vnoise(w + off)
            + 0.3 * vnoise(w * 3.5 + off * 1.7 + vec2(53.0, 29.0));
    return pow(n, 1.3);
}
void main()
{
    vec4 tex = texture2D(s0, vTexCoord);
    float aByte = vColor.a * 255.0;
    if (aByte < 63.5 || aByte > 79.5)
    {
        gl_FragColor = tex * vColor;
        return;
    }
    float m = floor(aByte + 0.5) - 64.0;
    float eR = step(8.0, m); m -= eR * 8.0;
    float eL = step(4.0, m); m -= eL * 4.0;
    float eI = step(2.0, m); m -= eI * 2.0;
    float eO = m;
    vec2 intra = fract(vTexCoord * ps_uniforms_vec4[0].xy);
    float ampO   = ps_uniforms_vec4[0].z;
    float freq   = ps_uniforms_vec4[0].w;
    float ampI   = ps_uniforms_vec4[1].x;
    float ampS   = ps_uniforms_vec4[1].y;
    float crustW = ps_uniforms_vec4[1].z;
    vec2 w = vWorld * freq;
    // Per-edge carve depth: the outer (top) face keeps the sand-resting physics bound;
    // walls (ampS) and cave ceilings (ampI) carve deeper — nothing rests against them.
    float dO = eO * carve(w, vec2(0.0, 0.0))   * ampO;
    float dI = eI * carve(w, vec2(37.0, 17.0)) * ampI;
    float dL = eL * carve(w, vec2(91.0, 53.0)) * ampS;
    float dR = eR * carve(w, vec2(13.0, 71.0)) * ampS;
    // Signed distance INSIDE the carved boundary (frame fraction), min over the exposed
    // edges — 0 sits exactly on the coastline; unexposed edges contribute a sentinel.
    float sd = 9.0;
    sd = min(sd, mix(9.0, intra.y - dO,       eO));
    sd = min(sd, mix(9.0, 1.0 - intra.y - dI, eI));
    sd = min(sd, mix(9.0, intra.x - dL,       eL));
    sd = min(sd, mix(9.0, 1.0 - intra.x - dR, eR));
    // Convex corner rounding: where two ADJACENT edges are both exposed (a stair-step
    // corner of the tile grid) the corner is cut back radially with a noise-modulated
    // radius, so diagonal terrain reads as a wandering coastline instead of a 90-degree
    // staircase. The radius scales off the same edge amps — DM_CARVE=0 kills this too.
    float rO = max(ampO, ampS);
    float rI = max(ampI, ampS);
    sd = min(sd, mix(9.0, length(intra)                        - rO * (0.6 + 0.6 * vnoise(w * 1.3 + vec2( 7.0, 43.0))), eO * eL));
    sd = min(sd, mix(9.0, length(vec2(1.0 - intra.x, intra.y)) - rO * (0.6 + 0.6 * vnoise(w * 1.3 + vec2(61.0, 23.0))), eO * eR));
    sd = min(sd, mix(9.0, length(vec2(intra.x, 1.0 - intra.y)) - rI * (0.6 + 0.6 * vnoise(w * 1.3 + vec2(31.0, 79.0))), eI * eL));
    sd = min(sd, mix(9.0, length(vec2(1.0, 1.0) - intra)       - rI * (0.6 + 0.6 * vnoise(w * 1.3 + vec2(97.0, 11.0))), eI * eR));
    if (sd < 0.0)
        discard;
    // Rim crust: two HARD darkening bands hugging the actual carved coastline (quantised
    // steps, not a gradient, so the pixel-art read survives). Noise-gated so the rim is
    // patchy grime along ~3/4 of the coastline, not a uniform sticker outline.
    if (sd < crustW * 2.0 && vnoise(w * 2.2 + vec2(19.0, 87.0)) > 0.25)
        tex.rgb *= sd < crustW ? 0.78 : 0.90;
    gl_FragColor = vec4(tex.rgb * vColor.rgb, tex.a);
}
";

    /// <summary>Pixel shader for the LIQUID COMPOSITE pass. The sprite texture is the
    /// liquid render target: cells were rasterized into it as soft-alpha blobs whose alpha
    /// ACCUMULATES (colour replaces), so the alpha channel is a coverage field — a lone
    /// droplet peaks at its own texel, two droplets a texel apart sum past the threshold in
    /// the gap between them. Thresholding that field is the classic metaball cut: pool
    /// edges round off, near droplets fuse, and everything below the cut vanishes instead
    /// of ghosting at partial alpha. Texels whose 4-neighbourhood dips below the threshold
    /// are the liquid's outline — they brighten into the Noita rim highlight. Output is
    /// premultiplied at ONE uniform opacity, so the whole body composites over the world
    /// in a single blend: no per-quad double-blend mottling, tiles ghost through evenly.
    /// PsParams  = (texelW, texelH, coverage threshold, body opacity).
    /// PsParams2 = (rim multiply, rim add, time, WATER depth-gradient strength).
    /// PsParams3 = (planet centre in RT px X, Y, world px per RT px, unused).
    ///
    /// Water depth gradient (strength &gt; 0): WATER body pixels shift from their flat
    /// base blue toward a deep navy by how far below open air they sit — the coverage
    /// field is marched outward (toward the surface) to measure depth per pixel. Done
    /// HERE rather than in the fill colours because the field is assembled from mixed
    /// primitives (merged run chunks, per-cell quads, blob stamps) whose colour sampling
    /// points differ — anything positional baked into them shows every primitive
    /// boundary as a seam, while a per-pixel gradient cannot. Water is identified by its
    /// flat body colour (Cells.LiquidBody's water base — the bodies arrive strictly
    /// flat, so the match is exact within the surface row's small tint); acid, oil,
    /// glint quads and the hot field pass through untouched. The flame/hot composite
    /// passes strength 0, skipping the march entirely.</summary>
    private const string LiquidGlsl = @"#version 110
uniform sampler2D s0;
uniform vec4 ps_uniforms_vec4[3];
varying vec4 vColor;
varying vec2 vTexCoord;
varying vec2 vWorld;
void main()
{
    vec2 texel   = ps_uniforms_vec4[0].xy;
    float thresh = ps_uniforms_vec4[0].z;
    vec4 c = texture2D(s0, vTexCoord);
    if (c.a < thresh)
        discard;
    float n =        texture2D(s0, vTexCoord + vec2(texel.x, 0.0)).a;
    n = min(n, texture2D(s0, vTexCoord - vec2(texel.x, 0.0)).a);
    n = min(n, texture2D(s0, vTexCoord + vec2(0.0, texel.y)).a);
    n = min(n, texture2D(s0, vTexCoord - vec2(0.0, texel.y)).a);
    vec3 rgb = c.rgb;
    float amp = ps_uniforms_vec4[1].w;
    if (amp > 0.0)
    {
        vec3 dw = rgb - vec3(0.180, 0.353, 0.698);   // 46,90,178 = water body base
        if (dot(dw, dw) < 0.006)
        {
            float wpp = max(ps_uniforms_vec4[2].z, 0.001);
            vec2 rel  = vTexCoord / texel - ps_uniforms_vec4[2].xy;
            vec2 up   = (rel / max(length(rel), 0.001)) * texel * (4.0 / wpp);
            float depth = 16.0;
            for (int i = 1; i <= 16; i++)
            {
                if (texture2D(s0, vTexCoord + up * float(i)).a < thresh)
                {
                    depth = float(i);
                    break;
                }
            }
            rgb = mix(rgb, vec3(0.024, 0.055, 0.157), (depth / 16.0) * amp);
        }
    }
    if (n < thresh)
        rgb = min(rgb * ps_uniforms_vec4[1].x + vec3(ps_uniforms_vec4[1].y), vec3(1.0));
    float a = ps_uniforms_vec4[0].w;
    gl_FragColor = vec4(rgb * a, a) * vColor;
}
";

    private static byte[] WriteMgfx(string pixelGlsl, string technique, int psVec4s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);

        // --- header (Effect.ReadHeader: 10 bytes) ---
        w.Write(0x5846474D);            // "MGFX" read as little-endian int
        w.Write((byte)10);              // MGFXVersion — 3.8.2's reader accepts exactly 10
        w.Write((byte)0);               // profile: 0 = OpenGL (Shader.PlatformProfile)
        // effectKey: id for the device effect cache — MUST differ between the effects this
        // class builds or the second one silently reuses the first one's compiled shaders.
        w.Write(unchecked((int)0xD0A17A01) ^ technique.GetHashCode());

        // --- constant buffers ---
        w.Write(2);
        // cbuffer 0 → VS: the 4 matrix columns, 16 bytes each. GL uploads the whole buffer
        // as `uniform vec4 <name>[size/16]` via glUniform4fv, located by this exact name.
        w.Write("vs_uniforms_vec4");
        w.Write((short)64);
        w.Write(4);
        for (var i = 0; i < 4; i++) { w.Write(i); w.Write((ushort)(i * 16)); } // params 0..3 at 0,16,32,48
        // cbuffer 1 → PS: psVec4s packed vec4s of shader parameters (the GLSL's
        // `ps_uniforms_vec4[n]` array size must match).
        w.Write("ps_uniforms_vec4");
        w.Write((short)(psVec4s * 16));
        w.Write(psVec4s);
        for (var i = 0; i < psVec4s; i++) { w.Write(4 + i); w.Write((ushort)(i * 16)); }

        // --- shaders ---
        w.Write(2);
        WriteShader(w, isVertex: true, VertexGlsl, cbuffer: 0, withAttributes: true);
        WriteShader(w, isVertex: false, pixelGlsl, cbuffer: 1, withAttributes: false);

        // --- parameters (indices must match the cbuffer tables above) ---
        w.Write(4 + psVec4s);
        WriteVec4Param(w, "MatrixCol0");
        WriteVec4Param(w, "MatrixCol1");
        WriteVec4Param(w, "MatrixCol2");
        WriteVec4Param(w, "MatrixCol3");
        for (var i = 0; i < psVec4s; i++)
            WriteVec4Param(w, i == 0 ? "PsParams" : $"PsParams{i + 1}");

        // --- techniques ---
        w.Write(1);
        w.Write(technique);
        w.Write(0);                     // annotations
        w.Write(1);                     // passes
        w.Write("P0");
        w.Write(0);                     // annotations
        w.Write(0);                     // vertex shader index
        w.Write(1);                     // pixel shader index
        w.Write(false);                 // no blend-state block (SpriteBatch's state stands)
        w.Write(false);                 // no depth-stencil block
        w.Write(false);                 // no rasterizer block

        // File tail: the Effect ctor re-reads the signature here and throws if it isn't
        // exactly in place — a free end-to-end check that every field above was sized right.
        w.Write(0x5846474D);

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteShader(BinaryWriter w, bool isVertex, string glsl, byte cbuffer, bool withAttributes)
    {
        var code = Encoding.ASCII.GetBytes(glsl);
        w.Write(isVertex);
        w.Write(code.Length);
        w.Write(code);
        w.Write((byte)0);               // samplers: none — see class doc (defaults to unit 0)
        w.Write((byte)1);               // cbuffers used by this stage
        w.Write(cbuffer);
        if (withAttributes)
        {
            // Names must match the GLSL attribute declarations; usages must match
            // SpriteBatch's VertexPositionColorTexture. Locations are placeholders —
            // the GL runtime re-queries them by name after linking.
            w.Write((byte)3);
            WriteAttribute(w, "inPosition", VertexElementUsage.Position);
            WriteAttribute(w, "inColor", VertexElementUsage.Color);
            WriteAttribute(w, "inTexCoord", VertexElementUsage.TextureCoordinate);
        }
        else
        {
            w.Write((byte)0);
        }
    }

    private static void WriteAttribute(BinaryWriter w, string name, VertexElementUsage usage)
    {
        w.Write(name);
        w.Write((byte)usage);
        w.Write((byte)0);               // usage index
        w.Write((short)0);              // location placeholder (re-queried post-link)
    }

    /// <summary>One float4 parameter: class Vector, type Single, 1 row × 4 cols, zeroed
    /// initial data (the reader allocates and fills exactly rows×cols floats).</summary>
    private static void WriteVec4Param(BinaryWriter w, string name)
    {
        w.Write((byte)EffectParameterClass.Vector);
        w.Write((byte)EffectParameterType.Single);
        w.Write(name);
        w.Write("");                    // semantic
        w.Write(0);                     // annotations
        w.Write((byte)1);               // rows
        w.Write((byte)4);               // columns
        w.Write(0);                     // elements (recursive parameter list: count 0)
        w.Write(0);                     // struct members (count 0)
        for (var i = 0; i < 4; i++) w.Write(0f);
    }
}
