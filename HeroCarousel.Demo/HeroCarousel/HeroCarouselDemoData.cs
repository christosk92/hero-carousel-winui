using System;
using System.Collections.Generic;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.UI;

namespace HeroCarousel;

internal static class HeroCarouselDemoData
{
    public static IReadOnlyList<HeroCarouselSlide> CreateSlides(ResourceLoader resources)
    {
        return
        [
            new()
            {
                ImageUri = new Uri("https://picsum.photos/id/1018/2400/1400"),
                Tag = resources.GetString("HeroCarouselDemoAlpineTag"),
                Title = resources.GetString("HeroCarouselDemoAlpineTitle"),
                Subtitle = resources.GetString("HeroCarouselDemoAlpineSubtitle"),
                CtaText = resources.GetString("HeroCarouselDemoDetailsCta"),
                AccentColor = Color.FromArgb(255, 116, 89, 255),
                GlowColor = Color.FromArgb(214, 180, 70, 40),
                UseScrim = true,
                CtaUsesGlass = true,
            },
            new()
            {
                ImageUri = new Uri("https://picsum.photos/id/1025/2400/1400"),
                Tag = resources.GetString("HeroCarouselDemoStudioTag"),
                Title = resources.GetString("HeroCarouselDemoStudioTitle"),
                Subtitle = resources.GetString("HeroCarouselDemoStudioSubtitle"),
                CtaText = resources.GetString("HeroCarouselDemoGetCta"),
                AccentColor = Color.FromArgb(255, 224, 116, 24),
                GlowColor = Color.FromArgb(220, 230, 160, 90),
                UseDarkText = true,
                UseScrim = false,
                CtaUsesGlass = false,
                Rating = new HeroCarouselRating
                {
                    Age = 3,
                    Color = Color.FromArgb(255, 0, 166, 81),
                    Text = resources.GetString("HeroCarouselDemoRatingGeneral"),
                },
            },
            new()
            {
                ImageUri = new Uri("https://picsum.photos/id/1039/2400/1400"),
                Title = resources.GetString("HeroCarouselDemoChromaTitle"),
                Subtitle = resources.GetString("HeroCarouselDemoChromaSubtitle"),
                CtaText = resources.GetString("HeroCarouselDemoInstallCta"),
                AccentColor = Color.FromArgb(255, 194, 51, 74),
                GlowColor = Color.FromArgb(176, 110, 35, 135),
                UseScrim = true,
                CtaUsesGlass = false,
                Rating = new HeroCarouselRating
                {
                    Age = 7,
                    Color = Color.FromArgb(255, 255, 204, 0),
                    Text = resources.GetString("HeroCarouselDemoRatingMild"),
                },
            },
            new()
            {
                ImageUri = new Uri("https://picsum.photos/id/1043/2400/1400"),
                Tag = resources.GetString("HeroCarouselDemoHyperdriveTag"),
                Title = resources.GetString("HeroCarouselDemoHyperdriveTitle"),
                Subtitle = resources.GetString("HeroCarouselDemoHyperdriveSubtitle"),
                CtaText = resources.GetString("HeroCarouselDemoDetailsCta"),
                AccentColor = Color.FromArgb(255, 58, 141, 255),
                GlowColor = Color.FromArgb(191, 50, 110, 220),
                UseScrim = true,
                CtaUsesGlass = true,
            },
            new()
            {
                ImageUri = new Uri("https://picsum.photos/id/1067/2400/1400"),
                Title = resources.GetString("HeroCarouselDemoVoxshiftTitle"),
                Subtitle = resources.GetString("HeroCarouselDemoVoxshiftSubtitle"),
                CtaText = resources.GetString("HeroCarouselDemoInstallCta"),
                AccentColor = Color.FromArgb(255, 151, 71, 255),
                GlowColor = Color.FromArgb(212, 180, 80, 220),
                UseScrim = true,
                CtaUsesGlass = false,
            },
        ];
    }
}
