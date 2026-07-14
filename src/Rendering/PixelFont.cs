using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Tiny in-code 5x7 bitmap font. Avoids the MonoGame content pipeline so the project
/// stays a single .csproj with no .mgcb assets. Only renders ASCII upper, digits, and a
/// few punctuation marks — enough for the HUD.
/// </summary>
public sealed class PixelFont
{
    private const int W = 5;
    private const int H = 7;
    private readonly Texture2D _atlas;
    private readonly Dictionary<char, int> _index = new();

    public int LineHeight => H;

    public PixelFont(GraphicsDevice gd)
    {
        var glyphs = Glyphs;
        _atlas = new Texture2D(gd, W * glyphs.Count, H);
        var data = new Color[_atlas.Width * _atlas.Height];
        var i = 0;
        foreach (var (ch, rows) in glyphs)
        {
            _index[ch] = i;
            for (var y = 0; y < H; y++)
            {
                var row = rows[y];
                for (var x = 0; x < W; x++)
                {
                    var bit = (row >> (W - 1 - x)) & 1;
                    data[y * _atlas.Width + i * W + x] = bit == 1 ? Color.White : Color.Transparent;
                }
            }
            i++;
        }
        _atlas.SetData(data);
    }

    public void Draw(SpriteBatch sb, string text, Vector2 pos, Color color, int scale = 2)
    {
        var x = pos.X;
        var y = pos.Y;
        foreach (var ch in text)
        {
            var c = char.ToUpperInvariant(ch);
            // Typographic dashes fold to the hyphen glyph — recipe/UI copy uses em-dashes
            // and U+2212 minus signs freely and they should read as dashes, not '?'.
            if (c is '—' or '–' or '−') c = '-';
            if (c == '\n') { y += (H + 1) * scale; x = pos.X; continue; }
            if (!_index.TryGetValue(c, out var idx))
            {
                if (c == ' ') { x += (W + 1) * scale; continue; }
                idx = _index['?'];
            }
            sb.Draw(
                _atlas,
                new Rectangle((int)x, (int)y, W * scale, H * scale),
                new Rectangle(idx * W, 0, W, H),
                color);
            x += (W + 1) * scale;
        }
    }

    public int Measure(string text, int scale = 2)
    {
        var max = 0; var run = 0;
        foreach (var ch in text)
        {
            if (ch == '\n') { if (run > max) max = run; run = 0; continue; }
            run += (W + 1) * scale;
        }
        return run > max ? run : max;
    }

    // Glyph rows: 7 rows of 5-bit values. Bit 4 = leftmost column.
    private static readonly List<(char ch, int[] rows)> Glyphs = new()
    {
        ('0', new[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 }),
        ('1', new[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 }),
        ('2', new[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 }),
        ('3', new[] { 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110 }),
        ('4', new[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 }),
        ('5', new[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 }),
        ('6', new[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 }),
        ('7', new[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 }),
        ('8', new[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 }),
        ('9', new[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 }),

        ('A', new[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 }),
        ('B', new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 }),
        ('C', new[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 }),
        ('D', new[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110 }),
        ('E', new[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 }),
        ('F', new[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 }),
        ('G', new[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 }),
        ('H', new[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 }),
        ('I', new[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 }),
        ('J', new[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100 }),
        ('K', new[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 }),
        ('L', new[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 }),
        ('M', new[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 }),
        ('N', new[] { 0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001 }),
        ('O', new[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 }),
        ('P', new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 }),
        ('Q', new[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 }),
        ('R', new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 }),
        ('S', new[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 }),
        ('T', new[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 }),
        ('U', new[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 }),
        ('V', new[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 }),
        ('W', new[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010 }),
        ('X', new[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 }),
        ('Y', new[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 }),
        ('Z', new[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 }),

        ('.', new[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100 }),
        (',', new[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b00100, 0b01000 }),
        (':', new[] { 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000 }),
        ('-', new[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 }),
        ('+', new[] { 0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000 }),
        ('/', new[] { 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000 }),
        ('!', new[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100 }),
        ('?', new[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100 }),
        ('(', new[] { 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010 }),
        (')', new[] { 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000 }),
        ('[', new[] { 0b00111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00111 }),
        (']', new[] { 0b11100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11100 }),
        ('=', new[] { 0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000 }),
        ('*', new[] { 0b00000, 0b01010, 0b00100, 0b11111, 0b00100, 0b01010, 0b00000 }),
        ('%', new[] { 0b11001, 0b11010, 0b00010, 0b00100, 0b01000, 0b01011, 0b10011 }),
        ('×', new[] { 0b00000, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b00000 }),
    };
}
