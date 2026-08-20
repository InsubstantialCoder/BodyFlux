using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// Named control-point pairs matching CSS's built-in easing keywords (plus one overshoot example),
/// offered as starting points in the curve editor's "Start from" picker so a curve tweaked from here
/// feels familiar to anyone who's used <c>cubic-bezier()</c> before.
/// </summary>
public static class EasingCurvePresets
{
    public static readonly (string Name, Vector2 P1, Vector2 P2)[] All =
    [
        ("Linear",      new Vector2(0f,    0f),    new Vector2(1f,    1f)),
        ("Ease",        new Vector2(0.25f, 0.1f),  new Vector2(0.25f, 1f)),
        ("Ease In",     new Vector2(0.42f, 0f),    new Vector2(1f,    1f)),
        ("Ease Out",    new Vector2(0f,    0f),    new Vector2(0.58f, 1f)),
        ("Ease In-Out", new Vector2(0.42f, 0f),    new Vector2(0.58f, 1f)),
        ("Back",        new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f)),
    ];
}
