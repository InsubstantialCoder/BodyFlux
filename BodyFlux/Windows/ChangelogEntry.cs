using System;

namespace BodyFlux.Windows;

/// <summary>One version's worth of "what's new" — a title plus a flat bullet list.</summary>
public sealed record ChangelogEntry(Version Version, string Title, string[] Highlights);
