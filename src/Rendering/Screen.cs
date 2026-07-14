using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner.Rendering;

/// <summary>
/// Virtual-resolution mapping. The whole game renders to a fixed 1280×720 scene target and
/// is scaled (aspect-fit, letterboxed) onto whatever the real window/display is — so every
/// UI coordinate in the codebase stays in the fixed virtual space. Game1 refreshes
/// Scale/Offset each present; Mouse() hands back a MouseState already remapped into virtual
/// coordinates, so callers never see window-space positions.
/// </summary>
public static class Screen
{
    /// <summary>Window pixels per virtual pixel of the presented scene.</summary>
    public static float Scale = 1f;
    /// <summary>Top-left of the letterboxed scene inside the window, in window pixels.</summary>
    public static Point Offset;

    /// <summary>Mouse.GetState() with X/Y remapped from window space into the 1280×720
    /// virtual space. All input/UI code reads the mouse through this.</summary>
    public static MouseState Mouse()
    {
        var m = Microsoft.Xna.Framework.Input.Mouse.GetState();
        var x = (int)((m.X - Offset.X) / Scale);
        var y = (int)((m.Y - Offset.Y) / Scale);
        return new MouseState(x, y, m.ScrollWheelValue,
            m.LeftButton, m.MiddleButton, m.RightButton, m.XButton1, m.XButton2);
    }
}
