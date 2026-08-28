using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// One anchor of a multi-point easing curve. <see cref="Pos"/> is a point the curve passes
/// <em>through</em> (curve space: x = input progress, y = eased output), unlike a bezier control
/// point, which only pulls at the curve from outside it.
/// <para>
/// <see cref="In"/> and <see cref="Out"/> are this anchor's tangent handles: the control points of
/// the segment arriving at it and of the segment leaving it, stored as an <em>offset</em> from
/// <see cref="Pos"/> rather than in absolute curve space. That is what makes dragging an anchor
/// carry its own tangents along instead of warping the shape around it. The first key's
/// <see cref="In"/> and the last key's <see cref="Out"/> are unused (no segment on that side).
/// </para>
/// </summary>
public sealed record CurveKey(Vector2 Pos, Vector2 In, Vector2 Out)
{
    /// <summary>Absolute curve-space position of the incoming handle.</summary>
    public Vector2 InPoint => Pos + In;

    /// <summary>Absolute curve-space position of the outgoing handle.</summary>
    public Vector2 OutPoint => Pos + Out;
}
