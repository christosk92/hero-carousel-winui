using System;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// One metadata chip rendered beneath a slide's CTA row — small rounded
/// pill with an optional icon (image or font glyph) plus a short label.
/// Mirrors the Microsoft Store hero rail's PEGI / age / category pills.
/// </summary>
/// <remarks>
/// Set exactly one of <see cref="IconUri"/> or <see cref="IconGlyph"/>.
/// If both are set, <see cref="IconUri"/> wins. If neither is set, the
/// chip renders as text-only.
/// </remarks>
public sealed class HeroCarouselMetadataItem
{
    /// <summary>Short visible label, e.g., <c>"PEGI 3"</c> or <c>"4.7 ★"</c>.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional bitmap icon URI (e.g., a PEGI rating badge image).</summary>
    public Uri? IconUri { get; set; }

    /// <summary>
    /// Optional Segoe Fluent Icons codepoint (e.g., <c>""</c> for the
    /// star glyph). Used when <see cref="IconUri"/> is <see langword="null"/>.
    /// </summary>
    public string? IconGlyph { get; set; }
}
