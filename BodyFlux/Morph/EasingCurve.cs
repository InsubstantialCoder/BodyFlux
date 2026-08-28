using System;
using System.Collections.Generic;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// A user-authored easing curve, evaluated via <see cref="CubicBezier"/>.
/// <para>
/// Two storage shapes, one meaning. <see cref="Keys"/> holds the multi-point form: anchors the
/// curve passes through, each carrying its own tangent handles. <see cref="P1"/>/<see cref="P2"/>
/// hold the original two-control-point form, same convention as CSS's
/// <c>cubic-bezier(x1, y1, x2, y2)</c>, and are what every curve saved before multi-point support
/// still has. <see cref="Resolve"/> is the single way to read either: a curve with no
/// <see cref="Keys"/> is projected onto the exactly equivalent two-anchor list, so nothing needs
/// migrating on load and an old curve keeps rendering identically.
/// </para>
/// <para>
/// <see cref="P1"/>/<see cref="P2"/> are also kept up to date when a multi-point curve is saved
/// (see <see cref="FromKeys"/>) purely as a downgrade path: an older BodyFlux reading a newer
/// config ignores <see cref="Keys"/> and still finds a sane approximation of the shape rather than
/// a flat line.
/// </para>
/// </summary>
public sealed record EasingCurve(Guid Id, string Name, Vector2 P1, Vector2 P2, List<CurveKey>? Keys = null)
{
    /// <summary>True once the curve carries more than the two pinned endpoints.</summary>
    public bool IsMultiPoint => Keys is { Count: > 2 };

    /// <summary>Number of anchors the curve is drawn from, however it happens to be stored.</summary>
    public int PointCount => Keys is { Count: >= 2 } ? Keys.Count : 2;

    /// <summary>
    /// The curve as a key list, whichever shape it is stored in. Returns a copy, so a running morph
    /// can never be re-shaped mid-flight by the editor mutating the stored list.
    /// </summary>
    public CurveKey[] Resolve() => Keys is { Count: >= 2 }
        ? [.. Keys]
        : CurveKeys.FromControlPoints(P1, P2);

    /// <summary>
    /// Builds a curve from an authored key list, filling <see cref="P1"/>/<see cref="P2"/> with the
    /// closest two-point reading of it (the first and last segments' handles, clamped to the unit
    /// x range the old form allowed) for the downgrade path described on the type.
    /// </summary>
    public static EasingCurve FromKeys(Guid id, string name, IReadOnlyList<CurveKey> keys)
    {
        var list = CurveKeys.Sanitize(keys);
        var p1   = list[0].OutPoint;
        var p2   = list[list.Count - 1].InPoint;
        return new EasingCurve(id, name,
            new Vector2(Math.Clamp(p1.X, 0f, 1f), p1.Y),
            new Vector2(Math.Clamp(p2.X, 0f, 1f), p2.Y),
            list);
    }
}
