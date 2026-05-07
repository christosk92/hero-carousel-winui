using System;
using System.Collections.Generic;
using Klankhuis.Hero.Controls;
using Windows.UI;

namespace Klankhuis_Hero_Sample.Models;

/// <summary>
/// Same 12 podcasts the React prototype uses — accents are the hand-tuned
/// colours from the design's <c>data.ts</c>. The image URIs point at the
/// public Spotify CDN (CORS-friendly).
/// </summary>
/// <remarks>
/// A handful of items also showcase the optional CTA / metadata / icon
/// slots on <see cref="HeroCarouselItem"/> so the demo renders every
/// hero-rail variant from "title + tagline only" up to "logo + price chip
/// + title + tagline + primary CTA + secondary CTA + rating chip". CTAs
/// are deliberately left without commands here — <c>MainPage.xaml.cs</c>
/// wires them at app start, so this seed file stays free of UI logic.
/// </remarks>
internal static class PodcastSeed
{
    public static IReadOnlyList<HeroCarouselItem> All { get; } = BuildAll();

    private static IReadOnlyList<HeroCarouselItem> BuildAll()
    {
        var items = new[]
        {
            Item("Geuze & Gorgels",                 "Twee vrienden, één microfoon",              "Monica & Kaj / Tonny Media",         "Audio", "ab6765630000ba8a1faf54beac63b1a60650a655", 0xE8, 0xA8, 0x7C),
            Item("Boekestijn en De Wijk",           "Geopolitiek, scherp ontleed",               "BNR Nieuwsradio",                     "Audio", "ab6765630000ba8ab1fa707a6fb128f0014b76ab", 0x7A, 0x9C, 0xC6),
            Item("Sam Solo",                        "Solo gesprekken, eerlijk en rauw",          "Sam Hofman",                          "Audio", "ab6765630000ba8aa97300901b3abb8ec0afa20b", 0xC9, 0xA4, 0x5A),
            Item("KieftJansenEgmondGijp",           "Voetbal, met scherpe meningen",             "KieftJansenEgmondGijp",               "Audio", "ab6765630000ba8a14a1932dcafbfa00ca27607b", 0xD9, 0x77, 0x57),
            Item("Maarten van Rossem en Tom Jessen","Geschiedenis trifft actualiteit",           "Tom Jessen en Maarten van Rossem",    "Mixed", "ab6765630000ba8a6a1f28d7810bb3c867c78672", 0x8B, 0x73, 0x55),
            Item("Parool Misdaadpodcast",           "Onderzoeksjournalistiek over misdaad",      "Het Parool",                          "Mixed", "ab6765630000ba8a065ca02b4ce58c0f9e9db77e", 0xA8, 0x57, 0x51),
            Item("Vandaag Inside",                  "Nederland kijkt mee",                       "Vandaag Inside",                      "Audio", "ab6765630000ba8ad284e080d8206479022afb93", 0xC8, 0x9B, 0x3C),
            Item("We Love Nederlands",              "Een liefdesbrief aan de taal",              "Tonny Media",                         "Audio", "ab6765630000ba8accfd91e099beeeb542d08165", 0xE0, 0x7A, 0x8B),
            Item("Napleiten",                       "Recht en onrecht na het vonnis",            "Wouter Laumans, Christian Flokstra",  "Audio", "ab6765630000ba8a264fa6968d2b05d900a1a37e", 0x6B, 0x8E, 0x9C),
            Item("Bubbel van Steph & Rijk",         "Twee bubbels, één gesprek",                 "Rijk en Steph",                       "Audio", "ab6765630000ba8abb74388ae75fe36ce752b066", 0xB8, 0x7F, 0xA8),
            Item("NRC Vandaag",                     "Het belangrijkste nieuws, uitgelegd",       "NRC",                                 "Audio", "ab6765630000ba8a12b685f0b53b4caac3911f7b", 0xD4, 0xA3, 0x73),
            Item("Amerika in 15 minuten",           "De VS in een kwartier",                     "Raymond Mens",                        "Audio", "ab6765630000ba8ad005675ec4321cba26f8fa7d", 0x9C, 0x5C, 0x8A),
        };

        // ── Showcase: enrich a handful of items with the optional slots ─

        // Geuze & Gorgels — secondary CTA + rating chip + a category chip.
        items[0].SecondaryCtaText = "More episodes";
        items[0].Metadata = new[]
        {
            // Segoe Fluent Icons U+E735 = FavoriteStar (outlined star).
            // Use U+E734 for FavoriteStarFill if a solid star reads better.
            new HeroCarouselMetadataItem { Label = "4.6", IconGlyph = "" },
            new HeroCarouselMetadataItem { Label = "Comedy" },
        };

        // Sam Solo — primary + secondary CTAs + rating; the full hero-rail look.
        items[2].PrimaryCtaText = "Listen";
        items[2].SecondaryCtaText = "Episodes";
        items[2].Metadata = new[]
        {
            new HeroCarouselMetadataItem { Label = "4.8", IconGlyph = "" },
        };

        // Parool Misdaadpodcast — icon badge above the chip + custom
        // eyebrow text + primary CTA + multi-chip metadata. Re-uses the
        // cover URL as the badge image since it's a stable HTTPS asset
        // (production apps would supply a dedicated logo).
        items[5].IconUri = items[5].ImageUri;
        items[5].Eyebrow = "TRUE CRIME";
        items[5].PrimaryCtaText = "Listen";
        items[5].Metadata = new[]
        {
            // U+E721 = Search (magnifying glass).
            new HeroCarouselMetadataItem { Label = "Investigative", IconGlyph = "" },
            new HeroCarouselMetadataItem { Label = "16+" },
        };

        // Vandaag Inside — secondary CTA only — the "Learn more" variant
        // from the Microsoft Store hero rail.
        items[6].SecondaryCtaText = "Watch online";

        // Bubbel van Steph & Rijk — primary CTA only.
        items[9].PrimaryCtaText = "Subscribe";

        return items;
    }

    private static HeroCarouselItem Item(string title, string tagline, string subtitle, string source, string coverId, byte r, byte g, byte b)
        => new()
        {
            Title = title,
            Tagline = tagline,
            Subtitle = subtitle,
            Source = source,
            // OverlayBuilder no longer auto-prefixes "FEATURED · " from
            // Source — Eyebrow is now a free-form chip the consumer sets
            // directly. Re-create the original demo eyebrow text here so
            // the seed renders the same chip as before.
            Eyebrow = string.IsNullOrEmpty(source)
                ? "FEATURED"
                : $"FEATURED · {source.ToUpperInvariant()}",
            ImageUri = new Uri($"https://i.scdn.co/image/{coverId}"),
            Accent = Color.FromArgb(255, r, g, b),
        };
}
