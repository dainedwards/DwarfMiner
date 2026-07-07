using System;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.Systems;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// The crafting menu overlay: open/cursor state, key handling, and rendering. While open,
/// the world keeps simulating but Game1 routes key events here instead of to movement.
/// Crafting itself stays in Game1's item registry — this class hands the chosen recipe to
/// the <c>craft</c> callback and asks <c>isOwned</c> when dimming rows.
/// </summary>
public sealed class CraftingMenu
{
    public bool Open { get; private set; }
    private int _cursor;

    /// <summary>Open with the cursor on the first recipe (the C-key toggle).</summary>
    public void Show()
    {
        Open = true;
        _cursor = 0;
    }

    /// <summary>Close and rewind — called on run start so a new planet begins clean.</summary>
    public void Reset()
    {
        Open = false;
        _cursor = 0;
    }

    /// <summary>Menu input — scrolls with up/down (Shift jumps 5), crafts with Enter/Space,
    /// closes with C/Esc. Affordability and gates are checked by the craft callback.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys, Action<Recipe> craft)
    {
        if (Pressed(keys, prevKeys, Keys.C) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            return;
        }
        var step = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift) ? 5 : 1;
        if (Pressed(keys, prevKeys, Keys.Down) || Pressed(keys, prevKeys, Keys.S))
            _cursor = (_cursor + step) % Crafting.All.Count;
        if (Pressed(keys, prevKeys, Keys.Up) || Pressed(keys, prevKeys, Keys.W))
            _cursor = (_cursor - step + Crafting.All.Count) % Crafting.All.Count;
        if (Pressed(keys, prevKeys, Keys.Enter) || Pressed(keys, prevKeys, Keys.Space))
        {
            craft(Crafting.All[_cursor]);
        }
    }

    /// <summary>Render the overlay. Recipes scroll vertically with the cursor row
    /// highlighted; cost is colour-coded (green affordable, red not). Owned recipes are
    /// dimmed but still listed for reference.</summary>
    public void Draw(Renderer renderer, Inventory inv, Func<string, bool> isOwned,
                     int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;
        sb.Begin();
        // Dim backdrop so the world reads as paused even though it's still ticking.
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 170));
        sb.End();

        // Layout: a centred panel with header + scrollable list. Cursor stays on the same row
        // index but the visible window scrolls so a long recipe list still fits.
        const int panelW = 540;
        const int rowH = 16;
        const int visibleRows = 18;
        var panelH = 60 + visibleRows * rowH;
        var panelX = (viewportWidth - panelW) / 2;
        var panelY = (viewportHeight - panelH) / 2;

        sb.Begin();
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(15, 15, 25, 230));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(140, 130, 200));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(140, 130, 200));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(140, 130, 200));
        sb.Draw(renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(140, 130, 200));
        sb.End();

        renderer.DrawDebugLabel("CRAFTING — Up/Down to scroll, Enter to craft, C/Esc to close",
            new Vector2(panelX + 12, panelY + 12), Color.White);

        // Scroll the visible window so the cursor stays in view. Cursor at the top half of
        // the visible block? Show from 0; near the end? Lock the bottom; otherwise centre.
        var total = Crafting.All.Count;
        var firstVisible = MathHelper.Clamp(_cursor - visibleRows / 2, 0, Math.Max(0, total - visibleRows));

        for (var i = 0; i < visibleRows && firstVisible + i < total; i++)
        {
            var idx = firstVisible + i;
            var r = Crafting.All[idx];
            var rowY = panelY + 44 + i * rowH;

            var owned = isOwned(r.Id);
            var afford = Crafting.CanAfford(r, inv) && !owned;

            // Row highlight bar
            if (idx == _cursor)
            {
                sb.Begin();
                sb.Draw(renderer.Pixel,
                    new Rectangle(panelX + 4, rowY - 1, panelW - 8, rowH),
                    afford ? new Color(60, 90, 50, 200) : new Color(70, 50, 50, 200));
                sb.End();
            }

            // Name column. Dimmed (60% grey) for owned recipes; normal for affordable; red
            // for can't-afford. Shows recipe name + cost line under the name.
            var nameCol = owned ? new Color(120, 120, 120)
                       : afford ? new Color(220, 240, 200)
                                : new Color(230, 170, 170);
            var costStr = BuildCostString(r);
            renderer.DrawDebugLabel(r.Name, new Vector2(panelX + 16, rowY), nameCol);
            renderer.DrawDebugLabel(costStr, new Vector2(panelX + 280, rowY),
                owned ? new Color(110, 110, 110) : afford ? new Color(180, 220, 160) : new Color(220, 130, 130));
        }
    }

    private static string BuildCostString(Recipe r)
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var (id, count) in r.Cost)
        {
            if (!first) sb.Append(' ');
            sb.Append(count).Append('×').Append(Tiles.ResourceLabel(id));
            first = false;
        }
        return sb.ToString();
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && !prev.IsKeyDown(k);
}
