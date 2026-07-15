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
    public static Effect? BuildTerrainCarve(GraphicsDevice gd)
    {
        try
        {
            var fx = new Effect(gd, WriteMgfx());
            // Touch the parameters now so a malformed stream fails HERE (inside the
            // try) rather than mid-frame on first use.
            _ = fx.Parameters["MatrixCol0"];
            _ = fx.Parameters["PsParams"];
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
            Console.WriteLine($"[shader] terrain carve unavailable, using baked erosion: {e.Message}");
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
    /// PsParams = (atlasFramesX, atlasFramesY, carve amplitude in frame fraction, noise
    /// frequency per world px). Lattice hashes wrap at 289 px so sin() precision holds
    /// far from the origin (the pattern repeat is invisible at 2–3 px feature size).</summary>
    private const string PixelGlsl = @"#version 110
uniform sampler2D s0;
uniform vec4 ps_uniforms_vec4[1];
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
    float amp  = ps_uniforms_vec4[0].z;
    float freq = ps_uniforms_vec4[0].w;
    vec2 w = vWorld * freq;
    float dO = eO * carve(w, vec2(0.0, 0.0))   * amp;
    float dI = eI * carve(w, vec2(37.0, 17.0)) * amp;
    float dL = eL * carve(w, vec2(91.0, 53.0)) * amp;
    float dR = eR * carve(w, vec2(13.0, 71.0)) * amp;
    if (intra.y < dO || 1.0 - intra.y < dI || intra.x < dL || 1.0 - intra.x < dR)
        discard;
    gl_FragColor = vec4(tex.rgb * vColor.rgb, tex.a);
}
";

    private static byte[] WriteMgfx()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);

        // --- header (Effect.ReadHeader: 10 bytes) ---
        w.Write(0x5846474D);            // "MGFX" read as little-endian int
        w.Write((byte)10);              // MGFXVersion — 3.8.2's reader accepts exactly 10
        w.Write((byte)0);               // profile: 0 = OpenGL (Shader.PlatformProfile)
        w.Write(unchecked((int)0xD0A17A01)); // effectKey: unique id for the device effect cache

        // --- constant buffers ---
        w.Write(2);
        // cbuffer 0 → VS: the 4 matrix columns, 16 bytes each. GL uploads the whole buffer
        // as `uniform vec4 <name>[size/16]` via glUniform4fv, located by this exact name.
        w.Write("vs_uniforms_vec4");
        w.Write((short)64);
        w.Write(4);
        for (var i = 0; i < 4; i++) { w.Write(i); w.Write((ushort)(i * 16)); } // params 0..3 at 0,16,32,48
        // cbuffer 1 → PS: one packed vec4 of carve parameters.
        w.Write("ps_uniforms_vec4");
        w.Write((short)16);
        w.Write(1);
        w.Write(4); w.Write((ushort)0);  // param 4 at offset 0

        // --- shaders ---
        w.Write(2);
        WriteShader(w, isVertex: true, VertexGlsl, cbuffer: 0, withAttributes: true);
        WriteShader(w, isVertex: false, PixelGlsl, cbuffer: 1, withAttributes: false);

        // --- parameters (indices must match the cbuffer tables above) ---
        w.Write(5);
        WriteVec4Param(w, "MatrixCol0");
        WriteVec4Param(w, "MatrixCol1");
        WriteVec4Param(w, "MatrixCol2");
        WriteVec4Param(w, "MatrixCol3");
        WriteVec4Param(w, "PsParams");

        // --- techniques ---
        w.Write(1);
        w.Write("TerrainCarve");
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
