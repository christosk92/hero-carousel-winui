using System.Collections.Generic;
using Klankhuis.Hero.Controls;
using Klankhuis.Hero.Surfaces;
using Klankhuis_Hero_Sample.Models;
using Microsoft.UI.Xaml.Controls;

namespace Klankhuis_Hero_Sample;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();

        var podcasts = (IList<HeroCarouselItem>)PodcastSeed.All;

        // Wire CTA commands for any seed item that declared CTA text. The
        // seed itself stays free of UI logic — this sample turns each click
        // into a TeachingTip flash so the showcase reads as a working
        // demo rather than dead buttons.
        foreach (var item in podcasts)
        {
            if (!string.IsNullOrEmpty(item.PrimaryCtaText))
            {
                item.PrimaryCtaCommand = new RelayCommand(_ =>
                    ShowCtaFeedback(item.Title, item.PrimaryCtaText));
            }
            if (!string.IsNullOrEmpty(item.SecondaryCtaText))
            {
                item.SecondaryCtaCommand = new RelayCommand(_ =>
                    ShowCtaFeedback(item.Title, item.SecondaryCtaText));
            }
        }

        Hero.ItemsSource = podcasts;

        // Side cards: pick three category-flavored covers from the seed set,
        // mirroring the React design. Image URIs go in synchronously; the
        // diagonal-wash accent is fetched async via HeroAccentExtractor so
        // it matches the *image's* dominant colour rather than the
        // hand-curated seed accent (which can clash visually — e.g., the
        // NRC seed is peach but the actual cover is dominated by red).
        BigCard.ImageUri    = podcasts[10].ImageUri;        // NRC Vandaag → "Nieuws & politiek"
        BigCard.Accent      = podcasts[10].Accent;          // seed fallback while extract runs
        SportCard.ImageUri  = podcasts[3].ImageUri;         // KieftJansenEgmondGijp → "Sport"
        SportCard.Accent    = podcasts[3].Accent;
        ComedyCard.ImageUri = podcasts[7].ImageUri;         // We Love Nederlands → "Comedy"
        ComedyCard.Accent   = podcasts[7].Accent;
        _ = WireSideCardAccentsAsync(podcasts);

        // Wire the halo backdrop to the carousel. Done in code-behind
        // because x:Bind on attached properties is unreliable in
        // WinAppSDK 2.0 — the setter sometimes silently no-ops.
        // `HaloBackdrop` is the outer-Grid-level transparent host where
        // the halo's Composition SpriteVisual lives; its position within
        // the backdrop is derived from `Hero.TransformToVisual(HaloBackdrop)`.
        HeroHalo.SetSource(HaloBackdrop, Hero);
    }

    private void ShowCtaFeedback(string slideTitle, string action)
    {
        CtaTeachingTip.Title = action;
        CtaTeachingTip.Subtitle = slideTitle;
        CtaTeachingTip.IsOpen = true;
    }

    /// <summary>
    /// Replaces the side-card seed accents with the actual image-dominant
    /// extracted from each cover, so the diagonal wash on each card
    /// matches the cover's prominent colour rather than the seed's
    /// curated swatch.
    /// </summary>
    private async System.Threading.Tasks.Task WireSideCardAccentsAsync(IList<HeroCarouselItem> podcasts)
    {
        if (podcasts[10].ImageUri is { } big)    BigCard.Accent    = await HeroAccentExtractor.ExtractAsync(big);
        if (podcasts[3].ImageUri  is { } sport)  SportCard.Accent  = await HeroAccentExtractor.ExtractAsync(sport);
        if (podcasts[7].ImageUri  is { } comedy) ComedyCard.Accent = await HeroAccentExtractor.ExtractAsync(comedy);
    }
}
