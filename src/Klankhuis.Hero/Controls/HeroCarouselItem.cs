using System;
using System.Collections.Generic;
using System.Windows.Input;
using Windows.UI;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// Data record for one slide in <see cref="HeroCarousel"/>. POCO so it works
/// with any view-model layer. The carousel reads <see cref="ImageUri"/> +
/// <see cref="Accent"/> to drive the GPU-baked backdrop, binds the text
/// fields into the slide overlay's XAML <c>TextBlock</c>s, and renders the
/// optional badge / CTA / metadata slots when any are populated. Empty or
/// <see langword="null"/> slots are simply omitted from the layout — every
/// extra field is opt-in, so a minimal item with just <see cref="Title"/>
/// and <see cref="ImageUri"/> renders the same as before.
/// </summary>
public sealed class HeroCarouselItem
{
    /// <summary>Headline text — the largest line on the slide.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One-line description shown beneath the title.</summary>
    public string Tagline { get; set; } = string.Empty;

    /// <summary>Author / network / publisher line — rendered in mono text.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>
    /// Free-form chip text rendered above the title (e.g., <c>"FEATURED · AUDIO"</c>,
    /// <c>"€ 99,00"</c>, <c>"NEW"</c>). Empty hides the chip entirely.
    /// </summary>
    public string Eyebrow { get; set; } = string.Empty;

    /// <summary>
    /// Legacy classifier (e.g., <c>"Audio"</c>). Kept for binding
    /// compatibility but no longer used by the default overlay layout —
    /// set <see cref="Eyebrow"/> directly for the chip text.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>HTTPS URI of the cover artwork. Loaded asynchronously and baked into the slide backdrop.</summary>
    public Uri? ImageUri { get; set; }

    /// <summary>
    /// Optional small badge image rendered above the eyebrow chip — the
    /// "app icon" in Microsoft Store hero rail terms (e.g., a 56-px-square
    /// product logo). <see langword="null"/> hides the badge slot.
    /// </summary>
    public Uri? IconUri { get; set; }

    /// <summary>
    /// Per-slide accent colour — drives the GPU-baked tint overlay, the cover
    /// glow halo, and (if enabled) the carousel-frame outer-bleed shadow.
    /// </summary>
    public Color Accent { get; set; } = Color.FromArgb(255, 0x60, 0xCD, 0xFF);

    /// <summary>
    /// Primary CTA button label (e.g., <c>"Install"</c>). Empty hides the
    /// button. Renders as a filled button with high contrast against the
    /// slide backdrop.
    /// </summary>
    public string PrimaryCtaText { get; set; } = string.Empty;

    /// <summary>Command invoked when the primary CTA is tapped.</summary>
    public ICommand? PrimaryCtaCommand { get; set; }

    /// <summary>Optional parameter passed to <see cref="PrimaryCtaCommand"/>.</summary>
    public object? PrimaryCtaCommandParameter { get; set; }

    /// <summary>
    /// Secondary CTA button label (e.g., <c>"See details"</c>). Empty hides
    /// the button. Renders as a ghost / outlined button next to the primary.
    /// </summary>
    public string SecondaryCtaText { get; set; } = string.Empty;

    /// <summary>Command invoked when the secondary CTA is tapped.</summary>
    public ICommand? SecondaryCtaCommand { get; set; }

    /// <summary>Optional parameter passed to <see cref="SecondaryCtaCommand"/>.</summary>
    public object? SecondaryCtaCommandParameter { get; set; }

    /// <summary>
    /// Optional metadata chips rendered beneath the CTA row — e.g., a PEGI
    /// rating, a star score, a "subscription required" tag. Each chip can
    /// have an icon (image or Segoe Fluent Icons glyph) and a label.
    /// <see langword="null"/> or empty hides the row.
    /// </summary>
    public IList<HeroCarouselMetadataItem>? Metadata { get; set; }

    /// <summary>Arbitrary payload the host can attach (e.g. a podcast URI).</summary>
    public object? Tag { get; set; }
}
