using System;
using System.Collections.Generic;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// The rules a multi-point easing curve's <see cref="CurveKey"/> list has to obey, plus the edits
/// that keep obeying them: insert, remove, move an anchor, move a handle. Lives outside the editor
/// UI on purpose, because these are the invariants <see cref="CubicBezier.EvaluateCurve"/> is
/// evaluated against, so they are worth testing without an ImGui frame in the way.
/// <para>
/// The invariants, in one place:
/// the first anchor is pinned at (0,0) and the last at (1,1) (a morph always starts at its origin
/// and ends at its destination, and time always spans the full run); interior anchors keep a
/// strictly increasing x so the curve stays a function of time; a handle never leaves its own
/// segment's x span, which is what stops a segment from doubling back on itself; and y, for anchors
/// and handles alike, is free within <see cref="YMin"/>..<see cref="YMax"/> so overshoot shapes
/// still work.
/// </para>
/// </summary>
public static class CurveKeys
{
    /// <summary>
    /// Cap on anchors per curve. The request that prompted multi-point curves asked for 3-4; this
    /// is deliberately more generous, while still fitting an editor that draws every anchor and
    /// keeping <see cref="CubicBezier.EvaluateCurve"/>'s linear segment scan trivially cheap.
    /// </summary>
    public const int MaxKeys = 8;

    /// <summary>
    /// Vertical span an anchor or handle may occupy, matching the easing editor's canvas. Wider
    /// than the [0,1] unit square so overshoot curves (the "Back" preset and friends) have real
    /// room, but still bounded: a point outside the drawn canvas is a point the user cannot grab
    /// back.
    /// </summary>
    public const float YMin = -1f;
    public const float YMax =  2.5f;

    /// <summary>
    /// Minimum x gap enforced between neighbouring anchors. Keeps every segment solvable (see
    /// <see cref="CubicBezier.MinSegmentSpan"/>) and every anchor separately grabbable in the
    /// editor, and is comfortably above the ~0.05 of a morph that one Sync network frame covers,
    /// so a segment can never be too narrow for a remote viewer to see at all.
    /// </summary>
    public const float MinGap = 0.02f;

    /// <summary>
    /// The two-anchor key list equivalent to the classic <c>cubic-bezier(x1, y1, x2, y2)</c> pair,
    /// the projection every pre-multi-point saved curve is read through
    /// (<see cref="EasingCurve.Resolve"/>). Identical shape, expressed as anchors and handles.
    /// </summary>
    public static CurveKey[] FromControlPoints(Vector2 p1, Vector2 p2) =>
    [
        new CurveKey(Vector2.Zero, Vector2.Zero,      p1),
        new CurveKey(Vector2.One,  p2 - Vector2.One,  Vector2.Zero),
    ];

    /// <summary>True while the curve has room for another anchor.</summary>
    public static bool CanInsert(IReadOnlyList<CurveKey> keys) => keys is { Count: >= 2 } && keys.Count < MaxKeys;

    /// <summary>
    /// Inserts an anchor at time <paramref name="x"/>, <em>on</em> the curve and without changing
    /// its shape: the containing segment is split with de Casteljau, which yields the new anchor's
    /// position and the handles for both halves exactly. Returns null when there is no room, no
    /// segment contains <paramref name="x"/>, or the split would land within
    /// <see cref="MinGap"/> of a neighbouring anchor.
    /// </summary>
    public static List<CurveKey>? Insert(IReadOnlyList<CurveKey> keys, float x)
    {
        if (!CanInsert(keys)) return null;

        for (int i = 0; i < keys.Count - 1; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            if (x <= a.Pos.X || x >= b.Pos.X) continue;
            if (x - a.Pos.X < MinGap || b.Pos.X - x < MinGap) return null;

            float u     = CubicBezier.ParameterAt(x, a.Pos, a.OutPoint, b.InPoint, b.Pos);
            var   split = CubicBezier.SplitSegment(a.Pos, a.OutPoint, b.InPoint, b.Pos, u);

            var list = new List<CurveKey>(keys);
            list[i]     = a with { Out = split.LeftC1  - a.Pos };
            list[i + 1] = b with { In  = split.RightC2 - b.Pos };
            list.Insert(i + 1, new CurveKey(split.Mid,
                                            split.LeftC2  - split.Mid,
                                            split.RightC1 - split.Mid));
            return list;
        }

        return null;
    }

    /// <summary>
    /// Removes an interior anchor, merging the two segments it joined. Returns null for the pinned
    /// endpoints, an out-of-range index, or a curve already down to its two endpoints. Unlike
    /// <see cref="Insert"/> this does change the shape: the neighbours keep their own tangents and
    /// the curve between them is re-drawn from those, there is no exact inverse of a split.
    /// </summary>
    public static List<CurveKey>? Remove(IReadOnlyList<CurveKey> keys, int index)
    {
        if (keys is null || keys.Count <= 2) return null;
        if (index <= 0 || index >= keys.Count - 1) return null;

        var list = new List<CurveKey>(keys);
        list.RemoveAt(index);
        ClampHandles(list);
        return list;
    }

    /// <summary>
    /// Moves one anchor towards <paramref name="desired"/>, clamped to what the invariants allow:
    /// endpoints do not move at all, and an interior anchor stays <see cref="MinGap"/> clear of both
    /// neighbours on x and inside <see cref="YMin"/>..<see cref="YMax"/> on y. Handles travel with
    /// the anchor (they are stored as offsets) and are re-clamped to the segments' new widths.
    /// </summary>
    public static List<CurveKey> MoveAnchor(IReadOnlyList<CurveKey> keys, int index, Vector2 desired)
    {
        var list = new List<CurveKey>(keys);
        if (index <= 0 || index >= list.Count - 1) return list; // pinned endpoints

        float minX = list[index - 1].Pos.X + MinGap;
        float maxX = list[index + 1].Pos.X - MinGap;
        var   pos  = new Vector2(
            minX <= maxX ? Math.Clamp(desired.X, minX, maxX) : list[index].Pos.X,
            Math.Clamp(desired.Y, YMin, YMax));

        list[index] = list[index] with { Pos = pos };
        ClampHandles(list);
        return list;
    }

    /// <summary>
    /// Moves one handle to <paramref name="desiredOffset"/> (an offset from its own anchor),
    /// clamped into that handle's segment: an outgoing handle reaches forward at most to the next
    /// anchor, an incoming one back at most to the previous. A handle on the side where the anchor
    /// has no segment (the first anchor's In, the last one's Out) is left alone.
    /// </summary>
    public static List<CurveKey> MoveHandle(IReadOnlyList<CurveKey> keys, int index, bool outgoing, Vector2 desiredOffset)
    {
        var list = new List<CurveKey>(keys);
        if (index < 0 || index >= list.Count) return list;
        if (outgoing && index == list.Count - 1) return list;
        if (!outgoing && index == 0) return list;

        var  key  = list[index];
        float span = outgoing
            ? list[index + 1].Pos.X - key.Pos.X
            : key.Pos.X - list[index - 1].Pos.X;
        span = MathF.Max(span, 0f);

        var offset = outgoing
            ? ClampOffset(key.Pos, desiredOffset, 0f, span)
            : ClampOffset(key.Pos, desiredOffset, -span, 0f);

        list[index] = outgoing ? key with { Out = offset } : key with { In = offset };
        return list;
    }

    /// <summary>
    /// Forces an arbitrary key list back onto every invariant: at most <see cref="MaxKeys"/>
    /// anchors, sorted and strictly increasing on x, endpoints pinned to (0,0)/(1,1), y and handles
    /// inside their bounds. Run on a curve loaded from disk (a config is a text file a user can
    /// hand-edit) and on one being opened in the editor, the same self-healing the two-point editor
    /// already did when it clamped a loaded curve into the canvas.
    /// </summary>
    public static List<CurveKey> Sanitize(IReadOnlyList<CurveKey>? keys)
    {
        if (keys is null || keys.Count < 2)
            return [.. FromControlPoints(Vector2.Zero, Vector2.One)]; // linear fallback

        var list = new List<CurveKey>(keys);
        if (list.Count > MaxKeys)
            list.RemoveRange(MaxKeys - 1, list.Count - MaxKeys); // drop surplus interior keys, keep the last

        list.Sort((l, r) => l.Pos.X.CompareTo(r.Pos.X));

        list[0]              = list[0]              with { Pos = Vector2.Zero };
        list[list.Count - 1] = list[list.Count - 1] with { Pos = Vector2.One  };

        for (int i = 1; i < list.Count - 1; i++)
        {
            // Leave MinGap of room for every anchor still to come, so a run of coincident x values
            // spreads out forward instead of piling up against the pinned last anchor.
            float minX = list[i - 1].Pos.X + MinGap;
            float maxX = 1f - MinGap * (list.Count - 1 - i);
            list[i] = list[i] with
            {
                Pos = new Vector2(
                    minX <= maxX ? Math.Clamp(list[i].Pos.X, minX, maxX) : minX,
                    Math.Clamp(list[i].Pos.Y, YMin, YMax)),
            };
        }

        ClampHandles(list);
        return list;
    }

    // Pulls every handle back inside its own segment's x span and the canvas's y bounds.
    private static void ClampHandles(List<CurveKey> list)
    {
        for (int i = 0; i < list.Count - 1; i++)
        {
            var   a    = list[i];
            var   b    = list[i + 1];
            float span = MathF.Max(b.Pos.X - a.Pos.X, 0f);

            list[i]     = a with { Out = ClampOffset(a.Pos, a.Out,  0f,    span) };
            list[i + 1] = b with { In  = ClampOffset(b.Pos, b.In,  -span,  0f)   };
        }
    }

    private static Vector2 ClampOffset(Vector2 pos, Vector2 offset, float minX, float maxX)
    {
        // A handle's y bound applies to its absolute position, but the offset is what gets stored,
        // so an in-bounds handle must be handed back untouched rather than round-tripped through
        // (pos.Y + offset.Y) - pos.Y: that subtraction is only exact when one of the two terms is
        // zero, and otherwise drifts by an ULP every time a curve is sanitized. Small, but it would
        // mean Sanitize is not idempotent, and it would quietly push preset authors into only ever
        // writing handles that sit flat or hang off y = 0.
        float absoluteY = pos.Y + offset.Y;
        float clampedY  = Math.Clamp(absoluteY, YMin, YMax);

        return new Vector2(
            Math.Clamp(offset.X, minX, maxX),
            clampedY == absoluteY ? offset.Y : clampedY - pos.Y);
    }
}
