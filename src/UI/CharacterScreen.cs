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
/// The character screen (I key): a paper-doll pane of equipment slots — torch, head, chest,
/// legs, feet, two weapons, and a mining tool — beside a backpack grid of everything carried.
/// Gear is drag-and-dropped between the two; hovering any item shows a detailed tooltip and
/// lights up the doll slots it can be equipped to.
///
/// Slots are pointers into the inventory (same rule as the toolbelt): equipping never
/// changes counts, and unequipping just clears the pointer. Hit-test rectangles are cached
/// during Draw and consumed by the next Update — the layout doesn't move per frame so the
/// 1-frame staleness is invisible.
/// </summary>
public sealed class CharacterScreen
{
    public bool Open { get; private set; }

    /// <summary>Drag carry state: the id in hand and, when it was lifted off the doll, the
    /// slot it came from (so invalid drops can restore items that live nowhere else).</summary>
    private (string Id, EquipSlot? FromSlot)? _carry;

    private readonly Rectangle[] _slotRects = new Rectangle[Equipment.SlotCount];
    private readonly List<(string id, Rectangle rect)> _bagRects = new();

    private const int SlotSize = 44;
    private const int CellSize = 36;
    private const int CellGap = 4;
    private const int BagCols = 6;

    private static readonly string[] SlotLabels =
        { "TORCH", "HEAD", "CHEST", "LEGS", "FEET", "WEAPON 1", "WEAPON 2", "TOOL" };

    public void Show() { Open = true; _carry = null; }

    /// <summary>Close and drop any in-flight drag — called on run start.</summary>
    public void Reset() { Open = false; _carry = null; }

    /// <summary>Input while open. I/Esc closes. LMB picks up / drops gear; RMB is the quick
    /// path: unequip from a slot, or equip a backpack item to its first fitting slot.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys,
                       MouseState mouse, MouseState prevMouse, Player player)
    {
        if (Pressed(keys, prevKeys, Keys.I) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            _carry = null;
            return;
        }

        var lmb = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton != ButtonState.Pressed;
        var rmb = mouse.RightButton == ButtonState.Pressed && prevMouse.RightButton != ButtonState.Pressed;
        if (!lmb && !rmb) return;
        var mx = mouse.X; var my = mouse.Y;

        if (rmb)
        {
            for (var s = 0; s < Equipment.SlotCount; s++)
            {
                if (!_slotRects[s].Contains(mx, my)) continue;
                if (player.Equipment.Slots[s] is { } worn && player.Inventory.Count(worn) > 0)
                    player.Equipment.Slots[s] = null;   // intrinsics (pickaxe) stay put
                return;
            }
            foreach (var (id, rect) in _bagRects)
            {
                if (!rect.Contains(mx, my)) continue;
                if (!Equipment.IsEquippable(id) || player.Equipment.IsEquipped(id)) return;
                // First empty fitting slot, else displace the first fitting one.
                if (!player.Equipment.AutoEquip(id))
                    for (var s = 0; s < Equipment.SlotCount; s++)
                        if (Equipment.Fits(id, (EquipSlot)s)) { player.Equipment.Slots[s] = id; break; }
                return;
            }
            return;
        }

        // LMB while carrying: drop. On a fitting slot → equip (displacing back to the pack);
        // on a non-fitting slot or empty space → cancel, restoring items that only live on
        // the doll (the intrinsic pickaxe has no backpack entry to fall back to).
        if (_carry is { } carry)
        {
            for (var s = 0; s < Equipment.SlotCount; s++)
            {
                if (!_slotRects[s].Contains(mx, my)) continue;
                if (Equipment.Fits(carry.Id, (EquipSlot)s))
                    player.Equipment.Slots[s] = carry.Id;
                else if (carry.FromSlot is { } src)
                    player.Equipment.Set(src, carry.Id);
                _carry = null;
                return;
            }
            if (carry.FromSlot is { } from && player.Inventory.Count(carry.Id) <= 0)
                player.Equipment.Set(from, carry.Id);
            _carry = null;
            return;
        }

        // LMB pick-up: off the doll (clearing the slot so the drag reads as "in hand"), or
        // out of the backpack (non-destructive — the count stays, the drop installs a pointer).
        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            if (!_slotRects[s].Contains(mx, my) || player.Equipment.Slots[s] is not { } id) continue;
            _carry = (id, (EquipSlot)s);
            player.Equipment.Slots[s] = null;
            return;
        }
        foreach (var (id, rect) in _bagRects)
        {
            if (!rect.Contains(mx, my)) continue;
            if (Equipment.IsEquippable(id)) _carry = (id, null);
            return;
        }
    }

    public void Draw(Renderer renderer, Player player, int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;
        var mouse = Screen.Mouse();

        const int panelW = 660;
        const int panelH = 420;
        var px = (viewportWidth - panelW) / 2;
        var py = (viewportHeight - panelH) / 2;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 170));
        sb.Draw(renderer.Pixel, new Rectangle(px, py, panelW, panelH), new Color(15, 15, 25, 235));
        DrawBorder(sb, renderer.Pixel, new Rectangle(px, py, panelW, panelH), new Color(140, 130, 200));
        // Divider between doll and backpack panes.
        sb.Draw(renderer.Pixel, new Rectangle(px + 300, py + 40, 1, panelH - 52), new Color(140, 130, 200, 90));
        sb.End();

        renderer.DrawText("CHARACTER — drag gear between doll and backpack   RMB quick-equip   I/ESC close",
            new Vector2(px + 12, py + 12), Color.White);

        // What the cursor/carry is proposing to equip — drives the slot highlights.
        var proposing = _carry?.Id ?? HoveredBagId(mouse.X, mouse.Y);

        DrawDoll(renderer, player, px, py, proposing);
        DrawBackpack(renderer, player, px + 316, py + 44, panelW - 316 - 16);
        DrawCarryIcon(renderer, player, mouse);
        DrawTooltip(renderer, player, mouse, viewportWidth, viewportHeight);
    }

    // ───────── doll pane ─────────

    /// <summary>Slot rectangle layout, relative to the panel origin. Body slots run down the
    /// silhouette (head/chest/legs/feet); torch hangs off the left hand; weapons and the
    /// mining tool stack on the right, where the belt would sling them.</summary>
    private static Rectangle SlotRect(EquipSlot s, int px, int py)
    {
        var cx = px + 128;   // silhouette centreline
        return s switch
        {
            EquipSlot.Torch      => new Rectangle(cx - 106, py + 84, SlotSize, SlotSize),
            EquipSlot.Head       => new Rectangle(cx - 22, py + 56, SlotSize, SlotSize),
            EquipSlot.Chest      => new Rectangle(cx - 22, py + 118, SlotSize, SlotSize),
            EquipSlot.Legs       => new Rectangle(cx - 22, py + 180, SlotSize, SlotSize),
            EquipSlot.Feet       => new Rectangle(cx - 22, py + 242, SlotSize, SlotSize),
            EquipSlot.Weapon1    => new Rectangle(cx + 62, py + 84, SlotSize, SlotSize),
            EquipSlot.Weapon2    => new Rectangle(cx + 62, py + 152, SlotSize, SlotSize),
            EquipSlot.MiningTool => new Rectangle(cx + 62, py + 220, SlotSize, SlotSize),
            _ => Rectangle.Empty,
        };
    }

    private void DrawDoll(Renderer renderer, Player player, int px, int py, string? proposing)
    {
        var sb = renderer.Batch;
        var cx = px + 128;

        sb.Begin(samplerState: SamplerState.PointClamp);
        // Faint dwarf silhouette behind the body slots so the pane reads as a figure, not a
        // grid: head knob, broad torso, stub legs, boots.
        var body = new Color(58, 62, 82, 130);
        sb.Draw(renderer.Pixel, new Rectangle(cx - 12, py + 62, 24, 26), body);   // head
        sb.Draw(renderer.Pixel, new Rectangle(cx - 20, py + 92, 40, 84), body);   // torso
        sb.Draw(renderer.Pixel, new Rectangle(cx - 32, py + 98, 12, 52), body);   // arm L
        sb.Draw(renderer.Pixel, new Rectangle(cx + 20, py + 98, 12, 52), body);   // arm R
        sb.Draw(renderer.Pixel, new Rectangle(cx - 16, py + 178, 13, 66), body);  // leg L
        sb.Draw(renderer.Pixel, new Rectangle(cx + 3, py + 178, 13, 66), body);   // leg R

        // Gold pulse on every slot the hovered/carried item could equip to.
        var pulse = 0.55f + 0.45f * MathF.Sin(Environment.TickCount64 / 160f);
        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            var slot = (EquipSlot)s;
            var r = SlotRect(slot, px, py);
            _slotRects[s] = r;

            var worn = player.Equipment.Slots[s];
            var eligible = proposing is not null && Equipment.Fits(proposing, slot);
            var hovered = r.Contains(Screen.Mouse().X, Screen.Mouse().Y);

            sb.Draw(renderer.Pixel, r, worn is null ? new Color(22, 24, 36, 230) : new Color(45, 50, 70, 240));
            var border = eligible ? Color.Lerp(new Color(120, 100, 40), new Color(255, 220, 120), pulse)
                       : hovered ? new Color(200, 205, 225)
                       : new Color(110, 115, 130);
            DrawBorder(sb, renderer.Pixel, r, border);
            if (eligible)   // second ring so the highlight carries at a glance
                DrawBorder(sb, renderer.Pixel, new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), border);

            if (worn is not null)
            {
                var tex = Icons.GetForSlot(worn, player.PickaxeTier);
                if (tex is not null)
                    sb.Draw(tex, new Rectangle(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), Color.White);
                else
                    sb.Draw(renderer.Pixel, new Rectangle(r.X + 12, r.Y + 12, r.Width - 24, r.Height - 24),
                        Tiles.ResourceColor(worn));
            }
        }
        sb.End();

        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            var r = _slotRects[s];
            var label = SlotLabels[s];
            renderer.DrawText(label,
                new Vector2(r.X + (r.Width - renderer.MeasureText(label)) / 2f, r.Y + r.Height + 3),
                new Color(150, 150, 165));
        }

        // Worn-gear summary under the doll — makes the pieces' effects legible at a glance.
        var reduction = (int)MathF.Round(player.Equipment.ArmorReduction * 100f);
        var lightName = player.EffectiveLightTier switch
        {
            0 => "NONE", 1 => "TORCH", 2 => "LANTERN", 3 => "HEADLAMP", _ => "SUNSTONE",
        };
        renderer.DrawText($"DAMAGE TAKEN −{reduction}%   LIGHT: {lightName}",
            new Vector2(px + 16, py + 320), new Color(190, 220, 170));
        renderer.DrawText($"HP {(int)player.Health}/{(int)player.MaxHealth}",
            new Vector2(px + 16, py + 336), new Color(200, 200, 215));
    }

    // ───────── backpack pane ─────────

    /// <summary>Backpack rows in inventory-panel order: the known resource catalogue first,
    /// then anything else carried (crafted gear lands here).</summary>
    private static List<string> BagIds(Player player)
    {
        var ids = new List<string>();
        foreach (var id in Tiles.ResourceOrder)
            if (player.Inventory.Count(id) > 0) ids.Add(id);
        foreach (var (id, count) in player.Inventory.Items)
        {
            if (count <= 0) continue;
            if (Array.IndexOf(Tiles.ResourceOrder, id) < 0) ids.Add(id);
        }
        return ids;
    }

    private void DrawBackpack(Renderer renderer, Player player, int bx, int by, int width)
    {
        renderer.DrawText("BACKPACK", new Vector2(bx, by), Color.White);

        var sb = renderer.Batch;
        var ids = BagIds(player);
        _bagRects.Clear();

        sb.Begin(samplerState: SamplerState.PointClamp);
        var mouse = Screen.Mouse();
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var cell = new Rectangle(
                bx + (i % BagCols) * (CellSize + CellGap),
                by + 18 + (i / BagCols) * (CellSize + CellGap),
                CellSize, CellSize);
            _bagRects.Add((id, cell));

            var equippable = Equipment.IsEquippable(id);
            var hovered = cell.Contains(mouse.X, mouse.Y);
            sb.Draw(renderer.Pixel, cell, hovered ? new Color(60, 70, 95, 240) : new Color(24, 26, 38, 230));
            // Equippable gear gets a brighter frame than raw materials so it reads as gear.
            DrawBorder(sb, renderer.Pixel, cell,
                hovered ? new Color(220, 225, 240)
                : equippable ? new Color(170, 165, 200)
                : new Color(90, 95, 110));

            var tex = Icons.GetForSlot(id, player.PickaxeTier);
            if (tex is not null)
                sb.Draw(tex, new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4), Color.White);
            else
                sb.Draw(renderer.Pixel, new Rectangle(cell.X + 10, cell.Y + 10, cell.Width - 20, cell.Height - 20),
                    Tiles.ResourceColor(id));

            // Gold pip: this piece is currently on the doll.
            if (player.Equipment.IsEquipped(id))
                sb.Draw(renderer.Pixel, new Rectangle(cell.X + 3, cell.Y + 3, 5, 5), new Color(255, 220, 120));
        }
        sb.End();

        // Count badges over stacks (labels handle their own batch).
        for (var i = 0; i < ids.Count; i++)
        {
            var count = player.Inventory.Count(ids[i]);
            if (count <= 1) continue;
            var cell = _bagRects[i].rect;
            renderer.DrawDebugLabel(count > 9999 ? "9999" : count.ToString(),
                new Vector2(cell.X + cell.Width - 16, cell.Y + cell.Height - 12), Color.White);
        }
    }

    // ───────── carry + tooltip ─────────

    private void DrawCarryIcon(Renderer renderer, Player player, MouseState mouse)
    {
        if (_carry is not { } carry) return;
        var sb = renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        var tex = Icons.GetForSlot(carry.Id, player.PickaxeTier);
        if (tex is not null)
            sb.Draw(tex, new Rectangle(mouse.X - 16, mouse.Y - 16, 32, 32), new Color(255, 255, 255, 220));
        else
            sb.Draw(renderer.Pixel, new Rectangle(mouse.X - 8, mouse.Y - 8, 16, 16), Tiles.ResourceColor(carry.Id));
        sb.End();
    }

    private string? HoveredBagId(int mx, int my)
    {
        foreach (var (id, rect) in _bagRects)
            if (rect.Contains(mx, my)) return Equipment.IsEquippable(id) ? id : null;
        return null;
    }

    private void DrawTooltip(Renderer renderer, Player player, MouseState mouse, int viewportWidth, int viewportHeight)
    {
        if (_carry is not null) return;   // mid-drag, the carry icon is the feedback

        string? id = null;
        for (var s = 0; s < Equipment.SlotCount && id is null; s++)
            if (_slotRects[s].Contains(mouse.X, mouse.Y)) id = player.Equipment.Slots[s];
        if (id is null)
            foreach (var (bagId, rect) in _bagRects)
                if (rect.Contains(mouse.X, mouse.Y)) { id = bagId; break; }
        if (id is null) return;

        var lines = new List<(string text, Color color)>
        {
            (Tiles.ResourceLabel(id), new Color(255, 230, 160)),
        };
        foreach (var line in Describe(id)) lines.Add((line, new Color(210, 215, 230)));

        if (Equipment.IsEquippable(id))
        {
            var slots = new List<string>();
            for (var s = 0; s < Equipment.SlotCount; s++)
                if (Equipment.Fits(id, (EquipSlot)s)) slots.Add(SlotLabels[s]);
            lines.Add(($"EQUIPS TO: {string.Join(" / ", slots)}", new Color(255, 220, 120)));
            if (player.Equipment.IsEquipped(id)) lines.Add(("CURRENTLY WORN", new Color(160, 235, 160)));
            else lines.Add(("DRAG TO THE LIT SLOT (OR RMB)", new Color(150, 150, 165)));
        }
        var count = player.Inventory.Count(id);
        if (count > 0) lines.Add(($"IN PACK: {count}", new Color(150, 150, 165)));

        var w = 0;
        foreach (var (text, _) in lines) w = Math.Max(w, renderer.MeasureText(text));
        w += 16;
        var h = 10 + lines.Count * 13;
        var x = Math.Min(mouse.X + 18, viewportWidth - w - 4);
        var y = Math.Min(mouse.Y + 18, viewportHeight - h - 4);

        var sb = renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(renderer.Pixel, new Rectangle(x, y, w, h), new Color(8, 8, 16, 245));
        DrawBorder(sb, renderer.Pixel, new Rectangle(x, y, w, h), new Color(255, 220, 120, 160));
        sb.End();
        for (var i = 0; i < lines.Count; i++)
            renderer.DrawText(lines[i].text, new Vector2(x + 8, y + 6 + i * 13), lines[i].color);
    }

    /// <summary>Detailed hover text per item id. Gear gets stats; everything else gets a
    /// one-line role description so the whole backpack is inspectable.</summary>
    private static string[] Describe(string id) => id switch
    {
        // Lights
        "torch"       => new[] { "Carried flame — light tier I.", "Sheds a modest glow below the dirt band." },
        "lantern"     => new[] { "Steady oil glow — light tier II.", "Half again the torch's reach." },
        "helm_lamp"   => new[] { "Hands-free beam — light tier III.", "Wide, bright, and always pointed ahead." },
        "sun_crystal" => new[] { "Bottled daylight — light tier IV.", "Cold white blaze; the deep dark gives up." },
        // Armor
        "armor"           => new[] { "Iron chest plate.", "−40% damage taken while worn." },
        "chitin_armor"    => new[] { "Chitin chest plate, hunted not mined.", "−40% damage taken while worn." },
        "iron_helmet"     => new[] { "Iron helmet.", "−10% damage taken while worn." },
        "chitin_helmet"   => new[] { "Chitin helmet.", "−10% damage taken while worn." },
        "iron_leggings"   => new[] { "Iron leggings.", "−10% damage taken while worn." },
        "chitin_leggings" => new[] { "Chitin leggings.", "−10% damage taken while worn." },
        "iron_boots"      => new[] { "Iron boots.", "−5% damage taken while worn." },
        "chitin_boots"    => new[] { "Chitin boots.", "−5% damage taken while worn." },
        // Weapons
        "pistol"          => new[] { "Sidearm — solid single shots.", "No ammo cost; slow but dependable." },
        "machine_gun"     => new[] { "Hold LMB to spray.", "High rate of fire, shreds packs." },
        "laser"           => new[] { "Piercing energy beam.", "Punches through lines of creatures.", "Fried while EMP'd." },
        "laser_cannon"    => new[] { "Heavy lance — drills through walls", "and whatever stands behind them.", "Fried while EMP'd." },
        "rocket_launcher" => new[] { "Fires crafted rockets.", "Big blast; consumes 1 rocket per shot." },
        "cannon"          => new[] { "Shoulder cannon.", "Fires the best shell in the pack,", "falling back to a basic shot." },
        // Mining tools
        "pickaxe"      => new[] { "The dwarf's own pick.", "Swing at rock; tier sets power and reach." },
        "drill"        => new[] { "Continuous mining — hold LMB.", "Chews steadily through soft rock." },
        "hammer"       => new[] { "Shatters bedrock the pick can't.", "Slow, heavy swings." },
        "mining_laser" => new[] { "Disintegrates rock at range.", "Hold LMB for the beam.", "Fried while EMP'd." },
        "core_drill"   => new[] { "The only tool that pierces", "a planet's core." },
        // Throwables / consumables / ammo
        "dynamite"  => new[] { "Thrown charge (Z). Clears a crater." },
        "tnt"       => new[] { "Short toss, huge blast." },
        "nuke"      => new[] { "Titan-killing device.", "Do not stand near the flash." },
        "harpoon"   => new[] { "Anti-titan harpoon (Y)." },
        "rocket"    => new[] { "Launcher ammo." },
        "poultice"  => new[] { "+30 HP (press H)." },
        "feast"     => new[] { "+60 HP, cooked from hunted meat." },
        "ammo_silver"   => new[] { "Piercing cannon shell." },
        "ammo_ruby"     => new[] { "Incendiary cannon shell." },
        "ammo_sapphire" => new[] { "Freezing cannon shell." },
        "ammo_diamond"  => new[] { "Heavy AoE cannon shell." },
        // Placeables
        "ladder"             => new[] { "Climb without carving." },
        "rail"               => new[] { "Speed boost where laid." },
        "support"            => new[] { "Props tunnels against cave-ins." },
        "reinforced_support" => new[] { "Anchors a 3×3 area." },
        "glowshroom"         => new[] { "Placeable green light." },
        "beacon"             => new[] { "Placeable recall point (T returns)." },
        "sentry"             => new[] { "Auto-firing turret, placed at your feet." },
        // Creature parts
        "meat"   => new[] { "Hunted. Cooks into feasts." },
        "hide"   => new[] { "Hunted. Binds chitin armor." },
        "chitin" => new[] { "Hunted. Carves into armor pieces." },
        "fuel"   => new[] { "Rocket fuel ore." },
        _ => new[] { "Raw material — used in crafting." },
    };

    // ───────── helpers ─────────

    private static void DrawBorder(SpriteBatch sb, Texture2D pixel, Rectangle r, Color c)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
        sb.Draw(pixel, new Rectangle(r.X, r.Y + r.Height - 1, r.Width, 1), c);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
        sb.Draw(pixel, new Rectangle(r.X + r.Width - 1, r.Y, 1, r.Height), c);
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && !prev.IsKeyDown(k);
}
