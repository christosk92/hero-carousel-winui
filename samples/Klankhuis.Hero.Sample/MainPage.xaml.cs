using System.Collections.Generic;
using Klankhuis.Hero.Controls;
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
        // mirroring the React design.
        BigCard.ImageUri = podcasts[10].ImageUri;          // NRC Vandaag → "Nieuws & politiek"
        BigCard.Accent   = podcasts[10].Accent;
        SportCard.ImageUri = podcasts[3].ImageUri;          // KieftJansenEgmondGijp → "Sport"
        SportCard.Accent   = podcasts[3].Accent;
        ComedyCard.ImageUri = podcasts[7].ImageUri;         // We Love Nederlands → "Comedy"
        ComedyCard.Accent   = podcasts[7].Accent;

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
}
