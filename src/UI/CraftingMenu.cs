using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.Systems;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// The crafting overlay, Terraria-style: category tabs across the top, a scrollable GRID of
/// recipe icons on the left, and a detail panel on the right showing the selected recipe's
/// big icon, name, blurb, and its ingredient list with live have/need counts and a CRAFT
/// button. Fully mouse-driven (hover to inspect, click an icon to select, click CRAFT or the
/// selected icon again to make it) with a keyboard fallback (arrows move, Enter crafts,
/// C/Esc closes). Crafting itself still lives in Game1's item registry — this hands the
/// chosen recipe to the <c>craft</c> callback and asks <c>isOwned</c> when dimming rows.
/// </summary>
public sealed class CraftingMenu
{
    public bool Open { get; private set; }

    private ItemCategory _tab = ItemCategory.All;
    private string? _selectedId;
    private int _scroll;                       // first visible grid row
    private double _lastCraftTime;             // for the craft-flash feedback

    // Layout constants (virtual pixels).
    private const int Cols = 5;
    private const int Cell = 44;
    private const int CellGap = 6;
    private const int VisibleRows = 6;

    // Hit-rects captured at draw time so Update tests exactly what was drawn.
    private readonly List<(Rectangle rect, ItemCategory cat)> _tabRects = new();
    private readonly List<(Rectangle rect, string id)> _iconRects = new();
    private Rectangle _craftRect;
    private Rectangle _scrollUpRect, _scrollDownRect;

    /// <summary>Open with the first tab and no selection (the C-key toggle).</summary>
    public void Show()
    {
        Open = true;
        _tab = ItemCategory.All;
        _scroll = 0;
        // Pre-select the first recipe so the detail panel reads on open instead of blank.
        _selectedId = Crafting.All.Count > 0 ? Crafting.All[0].Id : null;
    }

    /// <summary>Close and rewind — called on run start so a new planet begins clean.</summary>
    public void Reset()
    {
        Open = false;
        _tab = ItemCategory.All;
        _scroll = 0;
        _selectedId = null;
    }

    /// <summary>The recipes shown under the active tab, in table order.</summary>
    private List<Recipe> Filtered()
    {
        var list = new List<Recipe>();
        foreach (var r in Crafting.All)
            if (_tab == ItemCategory.All || ItemInfo.CategoryOf(r.Id) == _tab)
                list.Add(r);
        return list;
    }

    /// <summary>Menu input. Mouse: tabs, icon grid, scroll arrows, and the CRAFT button.
    /// Keyboard fallback: arrows move the selection, Enter/Space crafts, C/Esc closes,
    /// Tab cycles categories, wheel scrolls the grid.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys, MouseState mouse,
        MouseState prevMouse, Func<string, bool> isOwned, Func<string, bool> canAfford,
        Action<Recipe> craft)
    {
        if (Pressed(keys, prevKeys, Keys.C) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            return;
        }

        var filtered = Filtered();
        var clicked = mouse.LeftButton == ButtonState.Pressed
                   && prevMouse.LeftButton != ButtonState.Pressed;

        // Tab cycling (keyboard).
        if (Pressed(keys, prevKeys, Keys.Tab))
        {
            var vals = (ItemCategory[])Enum.GetValues(typeof(ItemCategory));
            _tab = vals[(Array.IndexOf(vals, _tab) + 1) % vals.Length];
            _scroll = 0;
        }

        // Mouse: tab clicks.
        foreach (var (rect, cat) in _tabRects)
            if (rect.Contains(mouse.X, mouse.Y) && clicked)
            {
                _tab = cat;
                _scroll = 0;
                return;
            }

        // Mouse: scroll arrows + wheel.
        var rows = (filtered.Count + Cols - 1) / Cols;
        var maxScroll = Math.Max(0, rows - VisibleRows);
        if (mouse.ScrollWheelValue != prevMouse.ScrollWheelValue)
            _scroll = Math.Clamp(_scroll + (mouse.ScrollWheelValue < prevMouse.ScrollWheelValue ? 1 : -1),
                0, maxScroll);
        if (clicked && _scrollUpRect.Contains(mouse.X, mouse.Y)) _scroll = Math.Max(0, _scroll - 1);
        if (clicked && _scrollDownRect.Contains(mouse.X, mouse.Y)) _scroll = Math.Min(maxScroll, _scroll + 1);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        // Mouse: icon grid — hover selects, a click on the already-selected icon crafts.
        var hoveredId = (string?)null;
        foreach (var (rect, id) in _iconRects)
            if (rect.Contains(mouse.X, mouse.Y))
            {
                hoveredId = id;
                if (clicked)
                {
                    if (_selectedId == id) TryCraft(id, filtered, isOwned, canAfford, craft);
                    else _selectedId = id;
                    return;
                }
            }

        // Mouse: the CRAFT button.
        if (clicked && _craftRect.Contains(mouse.X, mouse.Y) && _selectedId is { } sel)
        {
            TryCraft(sel, filtered, isOwned, canAfford, craft);
            return;
        }

        // Keyboard: grid navigation over the filtered list.
        if (filtered.Count > 0)
        {
            var idx = _selectedId is null ? -1 : filtered.FindIndex(r => r.Id == _selectedId);
            var moved = false;
            if (Pressed(keys, prevKeys, Keys.Right) || Pressed(keys, prevKeys, Keys.D)) { idx = Math.Min(filtered.Count - 1, Math.Max(0, idx) + 1); moved = true; }
            if (Pressed(keys, prevKeys, Keys.Left) || Pressed(keys, prevKeys, Keys.A)) { idx = Math.Max(0, Math.Max(0, idx) - 1); moved = true; }
            if (Pressed(keys, prevKeys, Keys.Down) || Pressed(keys, prevKeys, Keys.S)) { idx = Math.Min(filtered.Count - 1, Math.Max(0, idx) + Cols); moved = true; }
            if (Pressed(keys, prevKeys, Keys.Up) || Pressed(keys, prevKeys, Keys.W)) { idx = Math.Max(0, Math.Max(0, idx) - Cols); moved = true; }
            if (moved)
            {
                _selectedId = filtered[idx].Id;
                var row = idx / Cols;
                if (row < _scroll) _scroll = row;
                if (row >= _scroll + VisibleRows) _scroll = row - VisibleRows + 1;
            }
        }

        if (Pressed(keys, prevKeys, Keys.Enter) || Pressed(keys, prevKeys, Keys.Space))
            if (_selectedId is { } s) TryCraft(s, filtered, isOwned, canAfford, craft);

        // Hover-inspect without selecting: if nothing selected yet, follow the hover so the
        // detail panel is never blank while the mouse is over an icon.
        if (_selectedId is null && hoveredId is not null) _selectedId = hoveredId;
    }

    private void TryCraft(string id, List<Recipe> filtered, Func<string, bool> isOwned,
        Func<string, bool> canAfford, Action<Recipe> craft)
    {
        var recipe = filtered.Find(r => r.Id == id) ?? Find(id);
        if (recipe is null || isOwned(id) || !canAfford(id)) return;
        craft(recipe);
        _lastCraftTime = _flashClock;
    }

    private static Recipe? Find(string id)
    {
        foreach (var r in Crafting.All) if (r.Id == id) return r;
        return null;
    }

    // A monotone clock fed by Draw so the craft flash can fade without a GameTime here.
    private double _flashClock;

    /// <summary>Render the overlay: tabs, the recipe grid, and the selected detail panel.</summary>
    public void Draw(Renderer renderer, Inventory inv, Func<string, bool> isOwned,
        Func<string, bool> canAfford, MouseState mouse, int viewportWidth, int viewportHeight,
        double time)
    {
        _flashClock = time;
        var sb = renderer.Batch;

        // Dim backdrop so the world reads as paused even though it's still ticking.
        sb.Begin();
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 175));
        sb.End();

        const int gridW = Cols * Cell + (Cols - 1) * CellGap;
        const int detailW = 230;
        var panelW = gridW + detailW + 48;
        var panelH = 78 + VisibleRows * (Cell + CellGap);
        var panelX = (viewportWidth - panelW) / 2;
        var panelY = (viewportHeight - panelH) / 2;

        // Panel frame.
        sb.Begin();
        Frame(sb, renderer, new Rectangle(panelX, panelY, panelW, panelH),
            new Color(18, 16, 28, 236), new Color(150, 138, 205));
        sb.End();

        renderer.DrawText("CRAFTING", new Vector2(panelX + 14, panelY + 10), new Color(235, 225, 255), 2);
        renderer.DrawText("C/ESC CLOSE", new Vector2(panelX + panelW - 108, panelY + 12), new Color(150, 150, 170));

        DrawTabs(renderer, panelX + 14, panelY + 34, mouse);

        var gridX = panelX + 14;
        var gridY = panelY + 56;
        DrawGrid(renderer, inv, isOwned, canAfford, mouse, gridX, gridY);

        var detailX = gridX + gridW + 20;
        DrawDetail(renderer, inv, isOwned, canAfford, mouse, detailX, gridY,
            detailW, VisibleRows * (Cell + CellGap));
    }

    private void DrawTabs(Renderer renderer, int x, int y, MouseState mouse)
    {
        _tabRects.Clear();
        var sb = renderer.Batch;
        var cats = (ItemCategory[])Enum.GetValues(typeof(ItemCategory));
        var cx = x;
        sb.Begin();
        var rects = new List<(Rectangle, ItemCategory, string, Color)>();
        foreach (var cat in cats)
        {
            var label = ItemInfo.LabelOf(cat);
            var w = renderer.MeasureText(label) + 14;
            var rect = new Rectangle(cx, y, w, 16);
            var active = cat == _tab;
            var hover = rect.Contains(mouse.X, mouse.Y);
            var accent = ItemInfo.ColorOf(cat);
            sb.Draw(renderer.Pixel, rect, active ? accent * 0.55f : hover ? new Color(60, 58, 80, 200) : new Color(34, 32, 48, 200));
            sb.Draw(renderer.Pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), active ? accent : new Color(70, 66, 92));
            rects.Add((rect, cat, label, active ? Color.White : new Color(190, 190, 205)));
            _tabRects.Add((rect, cat));
            cx += w + 4;
        }
        sb.End();
        foreach (var (rect, _, label, col) in rects)
            renderer.DrawText(label, new Vector2(rect.X + 7, rect.Y + 4), col);
    }

    private void DrawGrid(Renderer renderer, Inventory inv, Func<string, bool> isOwned,
        Func<string, bool> canAfford, MouseState mouse, int gridX, int gridY)
    {
        _iconRects.Clear();
        var sb = renderer.Batch;
        var filtered = Filtered();
        var rows = (filtered.Count + Cols - 1) / Cols;
        var maxScroll = Math.Max(0, rows - VisibleRows);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        sb.Begin();
        for (var vr = 0; vr < VisibleRows; vr++)
        {
            var row = _scroll + vr;
            for (var c = 0; c < Cols; c++)
            {
                var idx = row * Cols + c;
                if (idx >= filtered.Count) continue;
                var r = filtered[idx];
                var rect = new Rectangle(gridX + c * (Cell + CellGap), gridY + vr * (Cell + CellGap), Cell, Cell);
                _iconRects.Add((rect, r.Id));

                var owned = isOwned(r.Id);
                var afford = canAfford(r.Id) && !owned;
                var selected = r.Id == _selectedId;
                var hover = rect.Contains(mouse.X, mouse.Y);

                // Slot backing: green tint if craftable, grey if not, gold ring if selected.
                var bg = owned ? new Color(40, 44, 40, 230)
                       : afford ? new Color(38, 52, 36, 235)
                                : new Color(44, 34, 34, 235);
                sb.Draw(renderer.Pixel, rect, bg);
                var border = selected ? new Color(255, 215, 120)
                           : hover ? new Color(200, 200, 220)
                           : afford ? new Color(90, 130, 80) : new Color(80, 66, 66);
                Border(sb, renderer.Pixel, rect, border, selected || hover ? 2 : 1);

                var tex = Icons.GetForSlot(r.Id, 1);
                var tint = afford ? Color.White : new Color(255, 255, 255, 110);
                if (tex is not null)
                    sb.Draw(tex, new Rectangle(rect.X + 5, rect.Y + 5, Cell - 10, Cell - 10), tint);
                else
                    sb.Draw(renderer.Pixel, new Rectangle(rect.X + 13, rect.Y + 13, Cell - 26, Cell - 26),
                        Tiles.ResourceColor(r.Id) * (afford ? 1f : 0.45f));

                // Owned check mark corner.
                if (owned)
                    sb.Draw(renderer.Pixel, new Rectangle(rect.Right - 8, rect.Y + 3, 5, 5), new Color(120, 220, 130));
            }
        }
        sb.End();

        // Scroll arrows (drawn when the list overflows).
        _scrollUpRect = _scrollDownRect = Rectangle.Empty;
        if (maxScroll > 0)
        {
            var barX = gridX + Cols * (Cell + CellGap) - Cell + 2;
            _scrollUpRect = new Rectangle(barX, gridY - 16, 20, 14);
            _scrollDownRect = new Rectangle(barX, gridY + VisibleRows * (Cell + CellGap), 20, 14);
            sb.Begin();
            sb.Draw(renderer.Pixel, _scrollUpRect, new Color(50, 48, 66, 220));
            sb.Draw(renderer.Pixel, _scrollDownRect, new Color(50, 48, 66, 220));
            sb.End();
            renderer.DrawText(_scroll > 0 ? "^" : "-", new Vector2(_scrollUpRect.X + 7, _scrollUpRect.Y + 3), Color.White);
            renderer.DrawText(_scroll < maxScroll ? "v" : "-", new Vector2(_scrollDownRect.X + 7, _scrollDownRect.Y + 3), Color.White);
        }
    }

    private void DrawDetail(Renderer renderer, Inventory inv, Func<string, bool> isOwned,
        Func<string, bool> canAfford, MouseState mouse, int x, int y, int w, int h)
    {
        var sb = renderer.Batch;
        sb.Begin();
        Frame(sb, renderer, new Rectangle(x, y, w, h), new Color(24, 22, 36, 235), new Color(90, 84, 120));
        sb.End();

        var recipe = _selectedId is null ? null : Find(_selectedId);
        if (recipe is null)
        {
            renderer.DrawText("SELECT A RECIPE", new Vector2(x + 14, y + 14), new Color(150, 150, 170));
            return;
        }

        var id = recipe.Id;
        var (title, blurb) = SplitName(recipe.Name);
        var owned = isOwned(id);
        var afford = canAfford(id) && !owned;
        var accent = ItemInfo.ColorOf(ItemInfo.CategoryOf(id));

        // Big icon.
        var iconRect = new Rectangle(x + 14, y + 14, 40, 40);
        sb.Begin();
        sb.Draw(renderer.Pixel, iconRect, new Color(14, 13, 22));
        Border(sb, renderer.Pixel, iconRect, accent, 1);
        var tex = Icons.GetForSlot(id, 1);
        if (tex is not null)
            sb.Draw(tex, new Rectangle(iconRect.X + 4, iconRect.Y + 4, 32, 32), Color.White);
        else
            sb.Draw(renderer.Pixel, new Rectangle(iconRect.X + 10, iconRect.Y + 10, 20, 20), Tiles.ResourceColor(id));
        sb.End();

        renderer.DrawText(title.ToUpperInvariant(), new Vector2(x + 62, y + 16), Color.White);
        DrawWrapped(renderer, blurb, x + 62, y + 32, w - 74, new Color(180, 180, 200));

        // Ingredient list.
        var iy = y + 70;
        renderer.DrawText("REQUIRES", new Vector2(x + 14, iy), accent);
        iy += 18;
        foreach (var (costId, need) in recipe.Cost)
        {
            var have = inv.Count(costId);
            var enough = have >= need;
            var rowRect = new Rectangle(x + 14, iy, 18, 18);
            sb.Begin();
            sb.Draw(renderer.Pixel, rowRect, new Color(14, 13, 22));
            var ctex = Icons.Get(costId);
            if (ctex is not null)
                sb.Draw(ctex, new Rectangle(rowRect.X + 2, rowRect.Y + 2, 14, 14), Color.White);
            else
                sb.Draw(renderer.Pixel, new Rectangle(rowRect.X + 4, rowRect.Y + 4, 10, 10), Tiles.ResourceColor(costId));
            sb.End();
            renderer.DrawText($"{Tiles.ResourceLabel(costId)}", new Vector2(x + 38, iy + 2),
                enough ? new Color(210, 220, 210) : new Color(200, 160, 160));
            var cnt = $"{have}/{need}";
            renderer.DrawText(cnt, new Vector2(x + w - 14 - renderer.MeasureText(cnt), iy + 2),
                enough ? new Color(150, 220, 150) : new Color(230, 130, 130));
            iy += 22;
        }

        // Craft button.
        _craftRect = new Rectangle(x + 14, y + h - 30, w - 28, 22);
        var justCrafted = _flashClock - _lastCraftTime < 0.35;
        var btnHover = _craftRect.Contains(mouse.X, mouse.Y);
        var btnCol = owned ? new Color(50, 54, 50)
                   : !afford ? new Color(64, 44, 44)
                   : justCrafted ? new Color(120, 210, 120)
                   : btnHover ? new Color(90, 150, 80) : new Color(64, 110, 58);
        sb.Begin();
        sb.Draw(renderer.Pixel, _craftRect, btnCol);
        Border(sb, renderer.Pixel, _craftRect, afford ? new Color(150, 220, 140) : new Color(90, 80, 80), 1);
        sb.End();
        var btnLabel = owned ? "OWNED" : afford ? "CRAFT" : "MISSING MATERIALS";
        renderer.DrawText(btnLabel,
            new Vector2(_craftRect.X + (_craftRect.Width - renderer.MeasureText(btnLabel)) / 2f, _craftRect.Y + 6),
            afford || owned ? Color.White : new Color(210, 170, 170));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (string title, string blurb) SplitName(string name)
    {
        // Recipe names read "Title — blurb"; split on the em dash (or a plain hyphen with
        // spaces) so the detail panel can show the two apart.
        var sep = name.IndexOf('—');
        if (sep < 0) sep = name.IndexOf(" - ", StringComparison.Ordinal);
        return sep < 0 ? (name.Trim(), "") : (name[..sep].Trim(), name[(sep + 1)..].Trim());
    }

    private static void DrawWrapped(Renderer renderer, string text, int x, int y, int maxW, Color col)
    {
        if (string.IsNullOrEmpty(text)) return;
        var words = text.Split(' ');
        var line = "";
        var ly = y;
        foreach (var word in words)
        {
            var trial = line.Length == 0 ? word : line + " " + word;
            if (renderer.MeasureText(trial) > maxW && line.Length > 0)
            {
                renderer.DrawText(line, new Vector2(x, ly), col);
                ly += 12;
                line = word;
            }
            else line = trial;
        }
        if (line.Length > 0) renderer.DrawText(line, new Vector2(x, ly), col);
    }

    private static void Frame(Microsoft.Xna.Framework.Graphics.SpriteBatch sb, Renderer renderer,
        Rectangle r, Color fill, Color border)
    {
        sb.Draw(renderer.Pixel, r, fill);
        Border(sb, renderer.Pixel, r, border, 1);
    }

    private static void Border(Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
        Microsoft.Xna.Framework.Graphics.Texture2D pixel, Rectangle r, Color col, int t)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, t), col);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - t, r.Width, t), col);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, t, r.Height), col);
        sb.Draw(pixel, new Rectangle(r.Right - t, r.Y, t, r.Height), col);
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && !prev.IsKeyDown(k);
}
