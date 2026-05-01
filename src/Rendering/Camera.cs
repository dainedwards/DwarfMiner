using System;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Rendering;

/// <summary>
/// Orthographic camera that pans to a target and rotates so a chosen "up" vector points to screen-up.
/// The rotation makes the planet feel circular: the player stays upright while the world rolls past.
/// </summary>
public sealed class Camera
{
    public Vector2 Target;
    public float Rotation;        // radians, world rotates by -Rotation in screen space
    public float Zoom = 2.0f;     // each world pixel becomes Zoom screen pixels
    public Point ViewportSize;
    public float SmoothRotation;

    public void SnapTo(Vector2 target, float rotation)
    {
        Target = target;
        Rotation = rotation;
        SmoothRotation = rotation;
    }

    public void Follow(Vector2 target, Vector2 up, float dt)
    {
        Target = Vector2.Lerp(Target, target, MathHelper.Clamp(dt * 12f, 0f, 1f));

        // Desired rotation: rotate the world so `up` aligns with screen-up (-Y).
        // If up = (ux, uy), the angle from (-Y) is atan2(ux, -uy). Rotating world by -that angle aligns it.
        var desired = MathF.Atan2(up.X, -up.Y);
        Rotation = ShortLerpAngle(Rotation, desired, MathHelper.Clamp(dt * 6f, 0f, 1f));
        SmoothRotation = Rotation;
    }

    public Matrix View
    {
        get
        {
            var center = new Vector3(ViewportSize.X * 0.5f, ViewportSize.Y * 0.5f, 0);
            return Matrix.CreateTranslation(-Target.X, -Target.Y, 0) *
                   Matrix.CreateRotationZ(-SmoothRotation) *
                   Matrix.CreateScale(Zoom) *
                   Matrix.CreateTranslation(center);
        }
    }

    public Vector2 ScreenToWorld(Vector2 screen)
    {
        var inv = Matrix.Invert(View);
        return Vector2.Transform(screen, inv);
    }

    private static float ShortLerpAngle(float a, float b, float t)
    {
        var diff = MathF.IEEERemainder(b - a, MathHelper.TwoPi);
        return a + diff * t;
    }
}
