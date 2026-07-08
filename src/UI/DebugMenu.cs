using System;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.UI;

/// <summary>
/// A tiny developer overlay for spawning any boss on demand. Toggled with F9 while playing;
/// Up/Down (or the number row) pick a <see cref="TitanKind"/> and Enter/Space (or the number
/// key) spawns it next to the player via the <c>spawn</c> callback, replacing the current boss.
/// Purely a testing aid — never surfaced in normal play.
/// </summary>
public sealed class DebugMenu
{
    public bool Open { get; private set; }
    private int _cursor;

    private static readonly (TitanKind Kind, string Name)[] Entries =
    {
        (TitanKind.Godzilla, "Cinderwyrm  (fire breath)"),
        (TitanKind.Mecha,    "Mecha-Titan (drill laser)"),
        (TitanKind.Sandworm, "Shai-Hulud  (burrow/breach)"),
        (TitanKind.Kong,     "Stone Ape   (leap slam)"),
    };

    public void Toggle() => Open = !Open;
    public void Close() => Open = false;

    /// <summary>Menu input. Up/Down move the cursor, Enter/Space spawns the highlighted boss,
    /// number keys 1-4 spawn directly, F9/Esc closes. The chosen kind is handed to
    /// <paramref name="spawn"/>.</summary>
    public void Update(KeyboardState keys, KeyboardState prevKeys, Action<TitanKind> spawn)
    {
        if (Pressed(keys, prevKeys, Keys.F9) || Pressed(keys, prevKeys, Keys.Escape))
        {
            Open = false;
            return;
        }
        if (Pressed(keys, prevKeys, Keys.Down) || Pressed(keys, prevKeys, Keys.S))
            _cursor = (_cursor + 1) % Entries.Length;
        if (Pressed(keys, prevKeys, Keys.Up) || Pressed(keys, prevKeys, Keys.W))
            _cursor = (_cursor - 1 + Entries.Length) % Entries.Length;

        for (var i = 0; i < Entries.Length; i++)
            if (Pressed(keys, prevKeys, Keys.D1 + i))
            {
                spawn(Entries[i].Kind);
                Open = false;
                return;
            }

        if (Pressed(keys, prevKeys, Keys.Enter) || Pressed(keys, prevKeys, Keys.Space))
        {
            spawn(Entries[_cursor].Kind);
            Open = false;
        }
    }

    public void Draw(Renderer renderer, int viewportWidth, int viewportHeight)
    {
        var sb = renderer.Batch;

        const int panelW = 360;
        const int rowH = 18;
        var panelH = 64 + Entries.Length * rowH;
        var panelX = (viewportWidth - panelW) / 2;
        var panelY = (viewportHeight - panelH) / 2;

        sb.Begin();
        sb.Draw(renderer.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), new Color(0, 0, 0, 150));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(15, 15, 25, 235));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(200, 130, 90));
        sb.Draw(renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(200, 130, 90));

        for (var i = 0; i < Entries.Length; i++)
        {
            var rowY = panelY + 48 + i * rowH;
            if (i == _cursor)
                sb.Draw(renderer.Pixel, new Rectangle(panelX + 4, rowY - 2, panelW - 8, rowH), new Color(70, 55, 40, 220));
        }
        sb.End();

        renderer.DrawDebugLabel("DEBUG: SPAWN BOSS  (Up/Down, Enter, or 1-4 — F9/Esc to close)",
            new Vector2(panelX + 12, panelY + 14), new Color(255, 200, 150));

        for (var i = 0; i < Entries.Length; i++)
        {
            var rowY = panelY + 48 + i * rowH;
            var col = i == _cursor ? new Color(255, 240, 210) : new Color(190, 190, 200);
            renderer.DrawDebugLabel($"{i + 1}. {Entries[i].Name}", new Vector2(panelX + 16, rowY), col);
        }
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && prev.IsKeyUp(k);
}
