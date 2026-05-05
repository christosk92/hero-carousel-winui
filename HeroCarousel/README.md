# HeroCarousel.WinUI

Reusable WinUI 3 hero carousel control with GPU-backed glow and color-wash effects, shimmer image placeholders, looping navigation, pips, keyboard, pointer, touch, and trackpad gesture support.

## Install In A WinUI App

Reference the project:

```xml
<ProjectReference Include="..\HeroCarousel\HeroCarousel.csproj" />
```

Then add the namespace and provide items:

```xml
<Page
    ...
    xmlns:hero="using:HeroCarousel">

    <hero:HeroCarouselView ItemsSource="{x:Bind Slides}" />
</Page>
```

The built-in `HeroCarouselSlide` model is the fastest path:

```csharp
public IReadOnlyList<HeroCarouselSlide> Slides { get; } =
[
    new()
    {
        Image = new Uri("https://example.com/hero.jpg"),
        Title = "Featured App",
        Subtitle = "A reusable hero card.",
        CtaText = "See details",
    },
];
```

For custom item models, set `ContentTemplate` for the overlay content and optionally provide a custom `IHeroCarouselImageProvider` to resolve images asynchronously.

## Loading Behavior

Each slide always reserves the hero surface size and shows a placeholder overlay first. The default placeholder is a shimmer using the slide accent/glow colors. Images fade in only after the source has been decoded and yielded back through the UI dispatcher, which avoids layout flicker from image-size changes.

## Common Options

- `ItemsSource`: any enumerable item source.
- `Slides`: compatibility collection for `HeroCarouselSlide` items when `ItemsSource` is not set.
- `ContentTemplate`: optional custom content overlay.
- `PlaceholderTemplate`: optional custom image placeholder.
- `ImageProvider`: optional async image resolver.
- `ImageStretch`: defaults to `UniformToFill`.
- `IsLoopingEnabled`: defaults to `true`.
- `IsAutoAdvanceEnabled`: defaults to `false`.
- `AutoAdvanceInterval`: defaults to `00:00:06`.
- `PauseAutoAdvanceOnInteraction`: defaults to `true`.
- `ShowNavigationButtons`: defaults to `true`.
- `ShowPips`: compatibility boolean, defaults to `true`.
- `PipsVisibility`: WinUI visibility for the styled `PipsPager`, defaults to `Visible`.
- `UseGlow`: defaults to `true`.
- `UseColorWash`: defaults to `true`.
- `UseSpotlight`: defaults to `true`.
- `UseButtonReveal`: defaults to `true`.
- `ImageCacheCapacity`: defaults to `24`.
