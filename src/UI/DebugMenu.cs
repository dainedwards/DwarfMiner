using System;
using DwarfMiner.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// A tiny developer overlay for spawning things on demand — bosses, creatures, disasters,
/// a ready-to-launch rocket, and so on. Toggled with F9 while playing; Left/Right (or Tab,
/// or clicking a header) switch tabs, Up/Down (or the number row) pick an entry and
/// Enter/Space (or the number key) run its action. Long tabs scroll: the cursor drags a
/// window over the list. Entries are supplied by the caller via <see cref="SetTabs"/> /
/// <see cref="SetEntries"/> so the menu stays generic. Purely a testing aid — never
/// surfaced in normal play.
/// </summary>
public sealed class DebugMenu
{
    /// <summary>One menu row: a display label and the action to run when it's chosen.</summary>
    public readonly record struct Entry(string Name, Action Run);

    /// <summary>Rows visible at once; longer tabs scroll under the cursor.</summary>
    private const int MaxRows = 16;

    public bool Open { get; private set; }
    private int _tab;
    private int _cursor;
    private int _scroll;
    private (string Name, Entry[] Entries)[] _tabs = Array.Empty<(string, Entry[])>();

    /// <summary>Replace the menu with a single unnamed tab — the simple callers (space
    /// screen) keep their old shape.</summary>
    public void SetEntries(Entry[] entries) => SetTabs(new[] { ("", entries) });

    /// <summary>Replace the menu's tabs. Cursor and tab are clamped so a shorter layout
    /// can't leave them dangling.</summary>
    public void SetTabs((string Name, Entry[] Entries)[] tabs)
    {
        _tabs = tabs;
        _tab = tabs.Length == 0 ? 0 : Math.Clamp(_tab, 0, tabs.Length - 1);
        ClampCursor();
    }

    private Entry[] Current => _tabs.Length == 0 ? Array.Empty<Entry>() : _tabs[_tab].Entries;

    private void ClampCursor()
    {
        var n = Current.Length;
        _cursor = n == 0 ? 0 : Math.Clamp(_cursor, 0, n - 1);
        // Keep the cursor inside the visible window.
        if (_cursor < _scroll) _scroll = _cursor;
        if (_cursor >= _scroll + MaxRows) _scroll = _cursor - MaxRows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, n - MaxRows));
    }

    public void Toggle() => Open = !Open;
    public void Close() => Open = false;

    /// <summary>Row hit-rects captured at draw time (toolbelt pattern) so Update can do
    /// hover + click against exactly what was rendered. Index is the VISIBLE row.</summary>
    private Rectangle[] _rowRects = Array.Empty<Rectangle>();
    private Rectangle[] _tabRects = Array.Empty<Rectangle>();

    /// <summary>Menu input. Left/Right/Tab switch tabs, Up/Down move the cursor,
    /// Enter/Space runs the highlighted entry, number keys 1-9 run visible rows directly,
    /// hover moves the cursor and click runs the row, F9/Esc closes.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys,
        MouseState mouse, MouseState prevMouse)
    {
        if (Pressed(keys, prevKeys, Keys.F9) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            return;
        }
        if (_tabs.Length > 1)
        {
            if (Pressed(keys, prevKeys, Keys.Right) || Pressed(keys, prevKeys, Keys.Tab))
            { _tab = (_tab + 1) % _tabs.Length; _cursor = 0; _scroll = 0; }
            if (Pressed(keys, prevKeys, Keys.Left))
            { _tab = (_tab - 1 + _tabs.Length) % _tabs.Length; _cursor = 0; _scroll = 0; }
        }
        var entries = Current;
        if (entries.Length == 0) return;

        if (Pressed(keys, prevKeys, Keys.Down) || Pressed(keys, prevKeys, Keys.S))
            _cursor = (_cursor + 1) % entries.Length;
        if (Pressed(keys, prevKeys, Keys.Up) || Pressed(keys, prevKeys, Keys.W))
            _cursor = (_cursor - 1 + entries.Length) % entries.Length;
        // Mouse wheel scrolls long tabs directly.
        var wheel = mouse.ScrollWheelValue - prevMouse.ScrollWheelValue;
        if (wheel != 0) _scroll -= Math.Sign(wheel) * 3;
        ClampCursor();

        var clicked = mouse.LeftButton == ButtonState.Pressed
                   && prevMouse.LeftButton != ButtonState.Pressed;
        for (var i = 0; i < Math.Min(_tabRects.Length, _tabs.Length); i++)
        {
            if (!_tabRects[i].Contains(mouse.X, mouse.Y)) continue;
            if (clicked && _tab != i) { _tab = i; _cursor = 0; _scroll = 0; return; }
        }
        var visible = Math.Min(MaxRows, entries.Length - _scroll);
        for (var i = 0; i < Math.Min(_rowRects.Length, visible); i++)
        {
            if (!_rowRects[i].Contains(mouse.X, mouse.Y)) continue;
            _cursor = _scroll + i;
            if (clicked)
            {
                entries[_cursor].Run();
                Open = false;
                return;
            }
        }

        for (var i = 0; i < Math.Min(9, visible); i++)
            if (Pressed(keys, prevKeys, Keys.D1 + i))
            {
                entries[_scroll + i].Run();
                Open = false;
                return;
            }

        if (Pressed(keys, prevKeys, Keys.Enter) || Pressed(keys, prevKeys, Keys.Space))
        {
            entries[_cursor].Run();
            Open = false;
        }
    }

    public void Draw(Renderer renderer, int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;
        var entries = Current;
        var visible = Math.Min(MaxRows, entries.Length - _scroll);

        const int panelW = 400;
        const int rowH = 18;
        var hasTabs = _tabs.Length > 1;
        var listTop = hasTabs ? 66 : 48;
        var panelH = listTop + 16 + visible * rowH;
        var panelX = (viewportWidth - panelW) / 2;
        var panelY = (viewportHeight - panelH) / 2;

        sb.Begin();
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 150));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(15, 15, 25, 235));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(200, 130, 90));

        // Tab headers: evenly split chips across the panel top.
        if (hasTabs)
        {
            if (_tabRects.Length != _tabs.Length) _tabRects = new Rectangle[_tabs.Length];
            var tabW = (panelW - 16) / _tabs.Length;
            for (var i = 0; i < _tabs.Length; i++)
            {
                _tabRects[i] = new Rectangle(panelX + 8 + i * tabW, panelY + 34, tabW - 4, 20);
                sb.Draw(renderer.Pixel, _tabRects[i],
                    i == _tab ? new Color(90, 65, 45, 235) : new Color(35, 35, 50, 235));
            }
        }

        if (_rowRects.Length != MaxRows) _rowRects = new Rectangle[MaxRows];
        for (var i = 0; i < visible; i++)
        {
            var rowY = panelY + listTop + i * rowH;
            _rowRects[i] = new Rectangle(panelX + 4, rowY - 2, panelW - 8, rowH);
            if (_scroll + i == _cursor)
                sb.Draw(renderer.Pixel, _rowRects[i], new Color(70, 55, 40, 220));
        }
        sb.End();

        renderer.DrawDebugLabel("DEBUG  (Left/Right tabs, Up/Down + Enter, or click — F9/Esc closes)",
            new Vector2(panelX + 12, panelY + 14), new Color(255, 200, 150));

        if (hasTabs)
            for (var i = 0; i < _tabs.Length; i++)
                renderer.DrawDebugLabel(_tabs[i].Name,
                    new Vector2(_tabRects[i].X + 6, _tabRects[i].Y + 6),
                    i == _tab ? new Color(255, 240, 210) : new Color(160, 160, 175));

        for (var i = 0; i < visible; i++)
        {
            var rowY = panelY + listTop + i * rowH;
            var idx = _scroll + i;
            var col = idx == _cursor ? new Color(255, 240, 210) : new Color(190, 190, 200);
            renderer.DrawDebugLabel($"{i + 1}. {entries[idx].Name}", new Vector2(panelX + 16, rowY), col);
        }
        // Scroll affordances when the list continues past the window.
        if (_scroll > 0)
            renderer.DrawDebugLabel("^ more", new Vector2(panelX + panelW - 58, panelY + listTop - 14),
                new Color(160, 160, 175));
        if (_scroll + visible < entries.Length)
            renderer.DrawDebugLabel("v more", new Vector2(panelX + panelW - 58, panelY + panelH - 14),
                new Color(160, 160, 175));
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && prev.IsKeyUp(k);
}
