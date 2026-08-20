using System;
using System.Numerics;

namespace BodyFlux.Morph;

public static class EasingHelper
{
    public static float Apply(float t, EasingMode mode) => mode switch
    {
        EasingMode.EaseIn    => t * t,
        EasingMode.EaseOut   => 1f - (1f - t) * (1f - t),
        EasingMode.EaseInOut => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f,
        _                    => t,
    };

    /// <summary>(t, easedT) points along a built-in curve — mirrors <see cref="CubicBezier.Sample"/>,
    /// for drawing the same reference-shape preview a custom curve gets.</summary>
    public static Vector2[] Sample(EasingMode mode, int steps)
    {
        var points = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            points[i] = new Vector2(t, Apply(t, mode));
        }
        return points;
    }
}
