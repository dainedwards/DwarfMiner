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
/// The character screen (I key): a Terraria-styled paper-doll of equipment slots — torch,
/// head, chest, legs, feet, gloves, two weapons, a mining tool, and two accessory slots —
/// beside a backpack grid of everything carried. Gear is drag-and-dropped between the two;
/// hovering any item shows a rarity-coloured tooltip and pulses the doll slots it fits.
///
/// Slots are pointers into the inventory (same rule as the toolbelt): equipping never
/// changes counts, and unequipping just clears the pointer. Empty slots draw a ghost of a
/// representative item so their role reads without labels. Hit-test rectangles are cached
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
    private readonly Rectangle[] _hotbarRects = new Rectangle[9];
    private readonly List<(ItemCategory cat, Rectangle rect)> _tabRects = new();

    /// <summary>Active backpack filter tab + scroll row (mouse wheel drives it).</summary>
    private ItemCategory _bagTab = ItemCategory.All;
    private int _bagScroll;
    private int _bagMaxScroll;

    /// <summary>Item context menu (RMB on a backpack cell): equip / upgrade / drop.</summary>
    private (string Id, Point Pos)? _ctx;
    private readonly List<(string action, Rectangle rect, bool enabled)> _ctxRects = new();

    /// <summary>Wired by Game1: upgrade-path probe (label + affordability; null = no
    /// upgrade path / maxed), the upgrade craft itself, and the world-side item drop.</summary>
    public Func<string, (string label, bool can)?>? UpgradeInfo;
    public Action<string>? DoUpgrade;
    public Action<string, int>? DropAction;

    /// <summary>Doll slots retired from display — weapons and the mining tool live on the
    /// hotbar row now. (The enum entries stay for save compatibility.)</summary>
    private static bool HiddenSlot(EquipSlot s) =>
        s is EquipSlot.Weapon1 or EquipSlot.Weapon2 or EquipSlot.MiningTool;

    private const int SlotSize = 46;
    private const int SlotPitch = 62;
    private const int CellSize = 38;
    private const int CellGap = 4;
    private const int BagCols = 8;

    // ── Terraria-flavoured palette: deep blue panel, beveled indigo slots, gold accents ──
    private static readonly Color PanelBg      = new(28, 30, 66, 244);
    private static readonly Color PanelEdgeOut = new(9, 9, 24);
    private static readonly Color PanelEdgeIn  = new(99, 107, 178);
    private static readonly Color TitleBarBg   = new(46, 50, 100);
    private static readonly Color SlotFill     = new(56, 62, 122, 240);
    private static readonly Color SlotFillHot  = new(84, 94, 165, 245);
    private static readonly Color BevelLight   = new(122, 132, 198);
    private static readonly Color BevelDark    = new(22, 24, 52);
    private static readonly Color Gold         = new(255, 220, 120);
    private static readonly Color TextDim      = new(155, 158, 180);

    /// <summary>Label order matches the EquipSlot enum.</summary>
    private static readonly string[] SlotLabels =
        { "LIGHT SOURCE", "HEAD", "CHEST", "LEGS", "FEET", "WEAPON 1", "WEAPON 2", "TOOL", "GLOVES", "ACC 1", "ACC 2", "BACK" };

    /// <summary>Representative item drawn as a dim ghost in each empty slot, Terraria-style,
    /// so the slot's role reads at a glance.</summary>
    private static readonly string[] GhostIds =
        { "torch", "iron_helmet", "armor", "iron_leggings", "iron_boots", "pistol", "pistol", "pickaxe", "leather_gloves", "magnet_ring", "aegis_pendant", "jetpack" };

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

        const int panelW = 700;
        const int panelH = 412;
        var px = (viewportWidth - panelW) / 2;
        var py = (viewportHeight - panelH) / 2;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 175));
        // Double-edged panel: dark outer line, light inner line, deep blue body + title bar.
        sb.Draw(renderer.Pixel, new Rectangle(px - 2, py - 2, panelW + 4, panelH + 4), PanelEdgeOut);
        sb.Draw(renderer.Pixel, new Rectangle(px - 1, py - 1, panelW + 2, panelH + 2), PanelEdgeIn);
        sb.Draw(renderer.Pixel, new Rectangle(px, py, panelW, panelH), PanelBg);
        sb.Draw(renderer.Pixel, new Rectangle(px, py, panelW, 24), TitleBarBg);
        sb.Draw(renderer.Pixel, new Rectangle(px, py + 24, panelW, 1), PanelEdgeIn);
        // Divider between doll and backpack panes.
        sb.Draw(renderer.Pixel, new Rectangle(px + 318, py + 34, 1, panelH - 46), new Color(99, 107, 178, 110));
        sb.End();

        renderer.DrawText("CHARACTER", new Vector2(px + 10, py + 6), Gold);
        renderer.DrawText("RMB QUICK-EQUIP   I/ESC CLOSE", new Vector2(px + panelW - 186, py + 6), TextDim);

        // What the cursor/carry is proposing to equip — drives the slot highlights.
        var proposing = _carry?.Id ?? HoveredBagId(mouse.X, mouse.Y);

        DrawDoll(renderer, player, px, py, proposing);
        DrawBackpack(renderer, player, px + 334, py + 36, panelW - 334 - 14);
        DrawCarryIcon(renderer, player, mouse);
        DrawTooltip(renderer, player, mouse, viewportWidth, viewportHeight);
    }

    // ───────── doll pane ─────────

    /// <summary>Slot layout, relative to the panel origin — three columns. Body armor runs
    /// down the centre (head→boots); held/hung gear on the left (torch, gloves, trinkets);
    /// weapons and the mining tool on the right.</summary>
    private static Rectangle SlotRect(EquipSlot s, int px, int py)
    {
        var cx = px + 136;   // doll centreline
        const int top = 62;
        var (col, row) = s switch
        {
            EquipSlot.Torch      => (-1, 0),
            EquipSlot.Gloves     => (-1, 1),
            EquipSlot.Accessory1 => (-1, 2),
            EquipSlot.Accessory2 => (-1, 3),
            EquipSlot.Head       => (0, 0),
            EquipSlot.Chest      => (0, 1),
            EquipSlot.Legs       => (0, 2),
            EquipSlot.Feet       => (0, 3),
            EquipSlot.Weapon1    => (1, 0),
            EquipSlot.Weapon2    => (1, 1),
            EquipSlot.MiningTool => (1, 2),
            _                    => (1, 3),   // Back (jetpack)
        };
        return new Rectangle(cx - 23 + col * 84, py + top + row * SlotPitch, SlotSize, SlotSize);
    }

    /// <summary>Terraria-style beveled slot: filled box, 2-px light bevel on the top/left,
    /// 2-px dark on the bottom/right, optional gold eligibility ring.</summary>
    private static void DrawSlotBox(SpriteBatch sb, Texture2D pixel, Rectangle r, bool hot, Color? ring)
    {
        sb.Draw(pixel, r, hot ? SlotFillHot : SlotFill);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, 2), BevelLight);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, 2, r.Height), BevelLight);
        sb.Draw(pixel, new Rectangle(r.X, r.Y + r.Height - 2, r.Width, 2), BevelDark);
        sb.Draw(pixel, new Rectangle(r.X + r.Width - 2, r.Y, 2, r.Height), BevelDark);
        if (ring is { } c)
        {
            DrawBorder(sb, pixel, new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), c);
            DrawBorder(sb, pixel, new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), c);
        }
    }

    private void DrawDoll(Renderer renderer, Player player, int px, int py, string? proposing)
    {
        var sb = renderer.Batch;
        var mouse = Screen.Mouse();
        renderer.DrawText("EQUIPMENT", new Vector2(px + 14, py + 36), Gold);

        sb.Begin(samplerState: SamplerState.PointClamp);
        var pulse = 0.55f + 0.45f * MathF.Sin(Environment.TickCount64 / 160f);
        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            var slot = (EquipSlot)s;
            var r = SlotRect(slot, px, py);
            _slotRects[s] = r;

            var worn = player.Equipment.Slots[s];
            var eligible = proposing is not null && Equipment.Fits(proposing, slot);
            var hovered = r.Contains(mouse.X, mouse.Y);
            var ring = eligible ? Color.Lerp(new Color(130, 105, 40), Gold, pulse) : (Color?)null;
            DrawSlotBox(sb, renderer.Pixel, r, hovered, ring);

            // Worn icon, or a dim ghost of a representative item in an empty slot.
            var iconId = worn ?? GhostIds[s];
            var tex = Icons.GetForSlot(iconId, worn is null ? 1 : player.PickaxeTier);
            var tint = worn is null ? new Color(255, 255, 255, 42) : Color.White;
            if (tex is not null)
                sb.Draw(tex, new Rectangle(r.X + 5, r.Y + 5, r.Width - 10, r.Height - 10), tint);
            else if (worn is not null)
                sb.Draw(renderer.Pixel, new Rectangle(r.X + 13, r.Y + 13, r.Width - 26, r.Height - 26),
                    Tiles.ResourceColor(worn));
        }
        sb.End();

        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            var r = _slotRects[s];
            var label = SlotLabels[s];
            renderer.DrawText(label,
                new Vector2(r.X + (r.Width - renderer.MeasureText(label)) / 2f, r.Y + r.Height + 3),
                TextDim);
        }

        // Worn-gear summary — every live effect in one block, gold like a Terraria set bonus.
        var y = py + 62 + 4 * SlotPitch + 6;
        var reduction = (int)MathF.Round(player.Equipment.ArmorReduction * 100f);
        var lightName = player.EffectiveLightTier switch
        {
            0 => "NONE", 1 => "TORCH", 2 => "LANTERN", 3 => "HEADLAMP", _ => "SUNSTONE",
        };
        var swing = (int)MathF.Round((1f - player.Equipment.MineSpeedMul) * 100f);
        renderer.DrawText($"DAMAGE TAKEN −{reduction}%    LIGHT: {lightName}", new Vector2(px + 14, y), new Color(190, 220, 170));
        var line2 = $"HP {(int)player.Health}/{(int)player.MaxHealth}    MINE POWER {player.EffectivePickaxePower}";
        if (swing > 0) line2 += $"    SWING +{swing}%";
        renderer.DrawText(line2, new Vector2(px + 14, y + 15), new Color(190, 220, 170));
        var perks = new List<string>();
        if (player.Equipment.HasAccessory("band_regen")) perks.Add("REGEN");
        if (player.Equipment.HasAccessory("magnet_ring")) perks.Add("ORE MAGNET");
        if (player.Equipment.HasAccessory("miners_charm")) perks.Add("+1 POWER");
        if (player.Equipment.HasAccessory("aegis_pendant")) perks.Add("AEGIS");
        if (perks.Count > 0)
            renderer.DrawText("PERKS: " + string.Join("  ", perks), new Vector2(px + 14, y + 30), Gold);
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
        renderer.DrawText("BACKPACK", new Vector2(bx, by), Gold);

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

            var hovered = cell.Contains(mouse.X, mouse.Y);
            DrawSlotBox(sb, renderer.Pixel, cell, hovered, null);

            var tex = Icons.GetForSlot(id, player.PickaxeTier);
            if (tex is not null)
                sb.Draw(tex, new Rectangle(cell.X + 3, cell.Y + 3, cell.Width - 6, cell.Height - 6), Color.White);
            else
            {
                // Raw materials: colour chip with a darker base — reads as a little pile.
                var chip = Tiles.ResourceColor(id);
                sb.Draw(renderer.Pixel, new Rectangle(cell.X + 9, cell.Y + 11, cell.Width - 18, cell.Height - 20),
                    new Color(chip.R / 2, chip.G / 2, chip.B / 2));
                sb.Draw(renderer.Pixel, new Rectangle(cell.X + 11, cell.Y + 9, cell.Width - 22, cell.Height - 20), chip);
            }

            // Gold pip: this piece is currently on the doll.
            if (player.Equipment.IsEquipped(id))
                sb.Draw(renderer.Pixel, new Rectangle(cell.X + 4, cell.Y + 4, 5, 5), Gold);
        }
        sb.End();

        // Count badges: right-aligned small text with a 1-px drop shadow (no black plate).
        for (var i = 0; i < ids.Count; i++)
        {
            var count = player.Inventory.Count(ids[i]);
            if (count <= 1) continue;
            var cell = _bagRects[i].rect;
            var str = count > 9999 ? "9999" : count.ToString();
            var tw = renderer.MeasureText(str);
            var pos = new Vector2(cell.X + cell.Width - tw - 3, cell.Y + cell.Height - 10);
            renderer.DrawText(str, pos + new Vector2(1, 1), new Color(0, 0, 0, 220));
            renderer.DrawText(str, pos, Color.White);
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
            sb.Draw(tex, new Rectangle(mouse.X - 18, mouse.Y - 18, 36, 36), new Color(255, 255, 255, 225));
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

    /// <summary>Terraria-style rarity tint for the tooltip title line.</summary>
    private static Color Rarity(string id) => id switch
    {
        // Top shelf — the late-game jewels.
        "sun_crystal" or "laser_cannon" or "mining_laser" or "core_drill" or "aegis_pendant" or "nuke"
            => new Color(210, 140, 255),
        // High tier.
        "helm_lamp" or "laser" or "rocket_launcher" or "iron_gauntlets" or "miners_charm" or "harpoon"
            => new Color(255, 180, 90),
        // Mid tier.
        "lantern" or "machine_gun" or "drill" or "hammer" or "cannon" or "magnet_ring" or "band_regen"
            or "chitin_armor" or "chitin_helmet" or "chitin_leggings" or "chitin_boots" or "sentry"
            => new Color(150, 255, 140),
        // Base gear.
        "torch" or "pistol" or "pickaxe" or "leather_gloves"
            or "armor" or "iron_helmet" or "iron_leggings" or "iron_boots"
            => new Color(140, 180, 255),
        _ => Color.White,
    };

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
            (Tiles.ResourceLabel(id), Rarity(id)),
        };
        foreach (var line in Describe(id)) lines.Add((line, new Color(215, 218, 232)));

        if (Equipment.IsEquippable(id))
        {
            var slots = new List<string>();
            for (var s = 0; s < Equipment.SlotCount; s++)
                if (Equipment.Fits(id, (EquipSlot)s)) slots.Add(SlotLabels[s]);
            lines.Add(($"EQUIPS TO: {string.Join(" / ", slots)}", Gold));
            if (player.Equipment.IsEquipped(id)) lines.Add(("CURRENTLY WORN", new Color(160, 235, 160)));
            else lines.Add(("DRAG TO THE LIT SLOT (OR RMB)", TextDim));
        }
        var count = player.Inventory.Count(id);
        if (count > 0) lines.Add(($"IN PACK: {count}", TextDim));

        var w = 0;
        foreach (var (text, _) in lines) w = Math.Max(w, renderer.MeasureText(text));
        w += 16;
        var h = 10 + lines.Count * 13;
        var x = Math.Min(mouse.X + 18, viewportWidth - w - 4);
        var y = Math.Min(mouse.Y + 18, viewportHeight - h - 4);

        var sb = renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(renderer.Pixel, new Rectangle(x - 1, y - 1, w + 2, h + 2), PanelEdgeOut);
        sb.Draw(renderer.Pixel, new Rectangle(x, y, w, h), new Color(24, 18, 54, 250));
        DrawBorder(sb, renderer.Pixel, new Rectangle(x, y, w, h), new Color(130, 110, 200));
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
        "leather_gloves"  => new[] { "Soft hide grips.", "Mining swings 15% faster, −5% damage." },
        "iron_gauntlets"  => new[] { "Articulated iron fists.", "Mining swings 30% faster, −5% damage." },
        // Accessories
        "band_regen"    => new[] { "A warm mossy band.", "Slowly restores health while worn." },
        "magnet_ring"   => new[] { "Hums near ore.", "Loose drops leap into the pack", "from four times the reach." },
        "miners_charm"  => new[] { "A prospector's lucky token.", "+1 mining power while worn." },
        "aegis_pendant" => new[] { "A ward of platinum and sapphire.", "−10% damage taken while worn." },
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
        "hide"   => new[] { "Hunted. Binds armor and gloves." },
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
