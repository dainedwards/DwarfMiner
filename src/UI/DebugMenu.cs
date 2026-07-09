using System;
using DwarfMiner.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// A tiny developer overlay for spawning things on demand — bosses, a ready-to-launch rocket,
/// and so on. Toggled with F9 while playing; Up/Down (or the number row) pick an entry and
/// Enter/Space (or the number key) runs its action. Entries are supplied by the caller via
/// <see cref="SetEntries"/> so the menu stays generic. Purely a testing aid — never surfaced
/// in normal play.
/// </summary>
public sealed class DebugMenu
{
    /// <summary>One menu row: a display label and the action to run when it's chosen.</summary>
    public readonly record struct Entry(string Name, Action Run);

    public bool Open { get; private set; }
    private int _cursor;
    private Entry[] _entries = Array.Empty<Entry>();

    /// <summary>Replace the menu's rows. The cursor is clamped into the new range so a shorter
    /// list can't leave it dangling.</summary>
    public void SetEntries(Entry[] entries)
    {
        _entries = entries;
        _cursor = entries.Length == 0 ? 0 : Math.Clamp(_cursor, 0, entries.Length - 1);
    }

    public void Toggle() => Open = !Open;
    public void Close() => Open = false;

    /// <summary>Menu input. Up/Down move the cursor, Enter/Space runs the highlighted entry,
    /// number keys 1-9 run directly, F9/Esc closes.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys)
    {
        if (Pressed(keys, prevKeys, Keys.F9) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            return;
        }
        if (_entries.Length == 0) return;

        if (Pressed(keys, prevKeys, Keys.Down) || Pressed(keys, prevKeys, Keys.S))
            _cursor = (_cursor + 1) % _entries.Length;
        if (Pressed(keys, prevKeys, Keys.Up) || Pressed(keys, prevKeys, Keys.W))
            _cursor = (_cursor - 1 + _entries.Length) % _entries.Length;

        for (var i = 0; i < Math.Min(9, _entries.Length); i++)
            if (Pressed(keys, prevKeys, Keys.D1 + i))
            {
                _entries[i].Run();
                Open = false;
                return;
            }

        if (Pressed(keys, prevKeys, Keys.Enter) || Pressed(keys, prevKeys, Keys.Space))
        {
            _entries[_cursor].Run();
            Open = false;
        }
    }

    public void Draw(Renderer renderer, int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;

        const int panelW = 360;
        const int rowH = 18;
        var panelH = 64 + _entries.Length * rowH;
        var panelX = (viewportWidth - panelW) / 2;
        var panelY = (viewportHeight - panelH) / 2;

        sb.Begin();
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 150));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(15, 15, 25, 235));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(200, 130, 90));

        for (var i = 0; i < _entries.Length; i++)
        {
            var rowY = panelY + 48 + i * rowH;
            if (i == _cursor)
                sb.Draw(renderer.Pixel, new Rectangle(panelX + 4, rowY - 2, panelW - 8, rowH), new Color(70, 55, 40, 220));
        }
        sb.End();

        renderer.DrawDebugLabel("DEBUG: SPAWN  (Up/Down, Enter, or number — F9/Esc to close)",
            new Vector2(panelX + 12, panelY + 14), new Color(255, 200, 150));

        for (var i = 0; i < _entries.Length; i++)
        {
            var rowY = panelY + 48 + i * rowH;
            var col = i == _cursor ? new Color(255, 240, 210) : new Color(190, 190, 200);
            renderer.DrawDebugLabel($"{i + 1}. {_entries[i].Name}", new Vector2(panelX + 16, rowY), col);
        }
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && prev.IsKeyUp(k);
}
