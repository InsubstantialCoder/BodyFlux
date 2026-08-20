using System;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// A user-authored easing curve: a cubic bezier from (0,0) to (1,1) with two interior control
/// points, same convention as CSS's <c>cubic-bezier(x1, y1, x2, y2)</c>. Evaluated via
/// <see cref="CubicBezier"/>.
/// </summary>
public sealed record EasingCurve(Guid Id, string Name, Vector2 P1, Vector2 P2);
