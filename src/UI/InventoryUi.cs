using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// The inventory panel + toolbelt strip: drag-and-drop carry state, click hit-testing, and
/// rendering. Hit-test rectangles are cached during Draw and consumed by HandleClick on the
/// next Update — the layout doesn't move per frame so 1-frame staleness is invisible.
/// </summary>
public sealed class InventoryUi
{
    /// <summary>Drag-and-drop carry state. While non-null, the player has picked up an item:
    /// <c>Id</c> is the inventory id; <c>FromSlot</c> is the toolbelt slot it came from (-1 if
    /// from the inventory panel). Dropping clears this; click-outside cancels.</summary>
    private (string Id, int FromSlot)? _carry;

    private readonly Dictionary<string, Rectangle> _invHitTest = new();
    private readonly Rectangle[] _beltHitTest = new Rectangle[Toolbelt.SlotCount];

    /// <summary>True while an item is being dragged — Game1 suppresses world LMB actions.</summary>
    public bool Carrying => _carry is not null;

    /// <summary>Drop any in-flight drag — called on run start.</summary>
    public void Reset() => _carry = null;

    /// <summary>Drag-and-drop click handler. Returns true iff the click landed on a UI element
    /// (so the world doesn't also receive it as an LMB world-action this frame). Click on
    /// inventory/toolbelt → pick up; click on toolbelt slot while carrying → drop. Right-click
    /// a toolbelt slot → unequip back to inventory (stackables only). Click outside any UI
    /// while carrying → cancel.</summary>
    public bool HandleClick(Vector2 screenPos, bool lmbPressed, bool rmbPressed, Player player)
    {
        if (!lmbPressed && !rmbPressed) return false;

        // RMB on a toolbelt slot: unequip stackable items back to inventory. Permanent slots
        // stay put — there's nowhere else for them to live.
        if (rmbPressed)
        {
            for (var s = 0; s < Toolbelt.SlotCount; s++)
            {
                if (!_beltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
                var id = player.Toolbelt.Slots[s];
                if (id is null) return true;
                if (Toolbelt.IsPermanent(id)) return true;
                player.Toolbelt.Slots[s] = null;
                return true;
            }
            return false;
        }

        // LMB: pick-up vs. drop based on whether we're already carrying.
        if (_carry is null)
        {
            // Click on a toolbelt slot just selects it. Rearranging is done via RMB-unequip +
            // inventory-drag — picking up directly from a slot would conflict with select.
            for (var s = 0; s < Toolbelt.SlotCount; s++)
            {
                if (!_beltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
                player.Toolbelt.Selected = s;
                return true;
            }
            // Click on an inventory row picks up that id (drag begins). The pickup is non-
            // destructive — inventory count stays the same; dropping just installs a slot
            // pointer to the same inventory entry.
            foreach (var (id, rect) in _invHitTest)
            {
                if (rect.Contains((int)screenPos.X, (int)screenPos.Y))
                {
                    _carry = (id, -1);
                    return true;
                }
            }
            return false;
        }

        // Carrying — drop on a toolbelt slot, or cancel by clicking elsewhere.
        var carry = _carry.Value;
        for (var s = 0; s < Toolbelt.SlotCount; s++)
        {
            if (!_beltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
            var prev = player.Toolbelt.Slots[s];
            player.Toolbelt.Slots[s] = carry.Id;
            // If the destination held a permanent tool, push it to the first empty slot so
            // the player doesn't lose it. Stackable displacement is fine — it lives in the
            // inventory regardless of belt presence.
            if (prev is not null && Toolbelt.IsPermanent(prev))
            {
                var empty = player.Toolbelt.FirstEmpty();
                if (empty >= 0) player.Toolbelt.Slots[empty] = prev;
            }
            player.Toolbelt.Selected = s;
            _carry = null;
            return true;
        }
        // Click outside any toolbelt slot → cancel the drag. Source state is unchanged.
        _carry = null;
        return true;
    }

    /// <summary>Render the toolbelt strip across the bottom of the screen — slot squares
    /// with icons, count badges, and a highlighted active slot.
    ///
    /// Layout: each slot is 36×36 with a 4-px gap; the strip is centred horizontally and
    /// pinned to the bottom with a 16-px bottom margin. The active slot is drawn with a
    /// brighter inner ring and a number-key tag above it. Empty slots are dim outlines.</summary>
    public void DrawToolbelt(Renderer renderer, Player player, int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;
        const int slotGap = 4;
        // Slots are 36 px by default but shrink to keep the whole strip inside the viewport
        // (with a 20-px margin each side) once the count grows past what fits at full size —
        // so bumping SlotCount for future items never runs the belt off-screen.
        var slotSize = Math.Min(36, (viewportWidth - 40) / Toolbelt.SlotCount - slotGap);
        var rowH = slotSize + 18;   // includes label tag above
        var totalW = Toolbelt.SlotCount * slotSize + (Toolbelt.SlotCount - 1) * slotGap;
        var x0 = (viewportWidth - totalW) / 2;
        var y0 = viewportHeight - rowH - 8;

        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            _beltHitTest[i] = new Rectangle(sx, sy, slotSize, slotSize);

            var isActive = i == player.Toolbelt.Selected;
            var bg = isActive ? new Color(60, 70, 90, 240) : new Color(20, 22, 32, 220);
            var border = isActive ? new Color(255, 220, 120) : new Color(110, 115, 130);
            sb.Draw(renderer.Pixel, new Rectangle(sx, sy, slotSize, slotSize), bg);
            sb.Draw(renderer.Pixel, new Rectangle(sx, sy, slotSize, 1), border);
            sb.Draw(renderer.Pixel, new Rectangle(sx, sy + slotSize - 1, slotSize, 1), border);
            sb.Draw(renderer.Pixel, new Rectangle(sx, sy, 1, slotSize), border);
            sb.Draw(renderer.Pixel, new Rectangle(sx + slotSize - 1, sy, 1, slotSize), border);
        }
        sb.End();

        // Pass 2: icon + count badge + slot number, with PointClamp sampling so the 16×16
        // pixel-art icons stay crisp at 2× scale (32×32 inside the 36-px slot).
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            var id = player.Toolbelt.Slots[i];

            // Slot number above each cell.
            var numStr = (i + 1).ToString();
            sb.End();   // brief flip so DrawDebugLabel can begin its own
            renderer.DrawDebugLabel(numStr,
                new Vector2(sx + 4, sy - 12),
                i == player.Toolbelt.Selected ? Color.White : new Color(150, 150, 160));
            sb.Begin(samplerState: SamplerState.PointClamp);

            if (id is null) continue;

            var tex = Icons.GetForSlot(id, player.PickaxeTier);
            if (tex is not null)
            {
                sb.Draw(tex, new Rectangle(sx + 2, sy + 2, slotSize - 4, slotSize - 4), Color.White);
            }
            else
            {
                // Fallback: solid swatch in the resource colour. Means an unknown id, useful
                // while wiring new recipes before authoring an icon.
                sb.Draw(renderer.Pixel, new Rectangle(sx + 8, sy + 8, slotSize - 16, slotSize - 16),
                    Tiles.ResourceColor(id));
            }
        }
        sb.End();

        // Pass 3: count badges (bottom-right corner of each slot for stackable items).
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            var id = player.Toolbelt.Slots[i];
            if (id is null) continue;
            if (Toolbelt.IsPermanent(id)) continue;
            var count = player.Inventory.Count(id);
            if (count <= 0) continue;
            renderer.DrawDebugLabel(count.ToString(),
                new Vector2(sx + slotSize - 14, sy + slotSize - 12),
                count > 0 ? Color.White : new Color(255, 120, 120));
        }
    }

    /// <summary>The inventory panel with click-to-pick-up. Each row is hit-test-recorded so
    /// HandleClick can detect which inventory id was clicked. Tool ids that live exclusively
    /// as toolbelt slots are skipped — drag-and-drop is for stackable items.</summary>
    public void DrawInventoryPanel(Renderer renderer, Player player, int viewportWidth)
    {
        _invHitTest.Clear();
        var inv = player.Inventory;

        var rows = new List<(string id, int count)>();
        foreach (var id in Tiles.ResourceOrder)
        {
            var c = inv.Count(id);
            if (c > 0 && ShouldShow(player, id)) rows.Add((id, c));
        }
        foreach (var (id, count) in inv.Items)
        {
            if (count <= 0) continue;
            var known = false;
            foreach (var k in Tiles.ResourceOrder) if (k == id) { known = true; break; }
            if (!known && ShouldShow(player, id)) rows.Add((id, count));
        }
        if (rows.Count == 0) return;

        const int swatchSize = 14;
        const int rowHeight = 18;
        const int padX = 8;
        const int padY = 6;

        const int panelW = 200;
        var panelH = padY + rows.Count * rowHeight + padY;
        var panelX = viewportWidth - panelW - 12;
        var panelY = 12;

        var sb = renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(0, 0, 0, 170));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(255, 255, 255, 60));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(255, 255, 255, 60));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(255, 255, 255, 60));
        sb.Draw(renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(255, 255, 255, 60));

        for (var i = 0; i < rows.Count; i++)
        {
            var (id, count) = rows[i];
            var rowY = panelY + padY + i * rowHeight;
            var rowRect = new Rectangle(panelX + 2, rowY - 1, panelW - 4, rowHeight);
            _invHitTest[id] = rowRect;

            // Hover highlight — handy feedback without a full hover system.
            var mouse = Screen.Mouse();
            if (rowRect.Contains(mouse.X, mouse.Y))
                sb.Draw(renderer.Pixel, rowRect, new Color(120, 130, 200, 60));

            // Icon takes priority over swatch — looks like a real tool entry. If the id
            // doesn't have an icon, fall back to the resource colour swatch (raw mats).
            var iconX = panelX + padX;
            var iconY = rowY + 1;
            var tex = Icons.GetForSlot(id, player.PickaxeTier);
            if (tex is not null)
            {
                sb.Draw(tex, new Rectangle(iconX - 1, iconY - 2, swatchSize + 4, swatchSize + 4), Color.White);
            }
            else
            {
                sb.Draw(renderer.Pixel, new Rectangle(iconX - 1, iconY - 1, swatchSize + 2, swatchSize + 2), new Color(0, 0, 0, 200));
                sb.Draw(renderer.Pixel, new Rectangle(iconX, iconY, swatchSize, swatchSize), Tiles.ResourceColor(id));
            }
        }
        sb.End();

        // Labels in a separate pass — DrawDebugLabel handles its own Begin/End.
        for (var i = 0; i < rows.Count; i++)
        {
            var (id, count) = rows[i];
            var rowY = panelY + padY + i * rowHeight;
            var line = $"{Tiles.ResourceLabel(id)}  {count}";
            renderer.DrawDebugLabel(line, new Vector2(panelX + padX + swatchSize + 8, rowY + 2), Color.White);
        }
    }

    /// <summary>Render the icon currently being carried at the cursor — a clear visual that
    /// something is in hand. No-op when nothing is carried. Uses the same icon lookup the
    /// slot/inventory uses so the appearance matches.</summary>
    public void DrawCarry(Renderer renderer, Player player)
    {
        if (_carry is not { } carry) return;
        var mouse = Mouse.GetState();
        var sb = renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        var tex = Icons.GetForSlot(carry.Id, player.PickaxeTier);
        if (tex is not null)
            sb.Draw(tex, new Rectangle(mouse.X - 16, mouse.Y - 16, 32, 32), new Color(255, 255, 255, 220));
        else
            sb.Draw(renderer.Pixel, new Rectangle(mouse.X - 8, mouse.Y - 8, 16, 16), Tiles.ResourceColor(carry.Id));
        sb.End();
    }

    /// <summary>Filter for the inventory panel: permanent tool markers (drill / hammer /
    /// cannon / core_drill) are visible only when *not* currently on the toolbelt. Stackable
    /// items always show — the toolbelt slot is a *shortcut* to the inventory stack, not a
    /// separate cache, so the count is meaningfully visible in both places at once.</summary>
    private static bool ShouldShow(Player player, string id)
    {
        if (Toolbelt.IsPermanent(id) && player.Toolbelt.Contains(id)) return false;
        return true;
    }
}
