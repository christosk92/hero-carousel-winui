# Klankhuis.Hero

![HeroCarousel demo screenshot](screenshot.png)

`Klankhuis.Hero` is a reusable WinUI 3 hero carousel control built on Composition + Win2D + ComputeSharp, modelled on the Microsoft Store hero rail. Heavy effects are GPU-baked once per slide; slide motion runs as off-thread `ExpressionAnimation`s keyed off a single `InteractionTracker`, so flicks stay at the compositor's refresh rate even while the UI thread is busy.

## Projects

- `src/Klankhuis.Hero/Klankhuis.Hero.csproj` — reusable control library.
- `samples/Klankhuis.Hero.Sample/Klankhuis.Hero.Sample.csproj` — packaged WinUI 3 demo app.

The solution is `Klankhuis.slnx` at the repository root.

## Features

- `HeroCarousel` — templated `Control` that hosts an arbitrary number of slides.
- `HeroCarouselItem` — POCO data record (title / tagline / subtitle / source / cover URI / accent / tag).
- `SideCard` — companion templated control for the side rail (Microsoft-Store-style category tile, accent wash + cover image).
- `HeroHalo` — attached-property helper that renders an accent-colour ambient halo around any element on the page; tracks the carousel's continuous accent without waiting for the slide to settle.
- GPU-baked slide backdrops via `Win2D` + `ComputeSharp.D2D1` shaders (NoiseShader for grain, accent radial wash, vignette).
- Cover artwork loaded asynchronously via `LoadedImageSurface`, masked into rounded shape via `CompositionMaskBrush`, with a `CommunityToolkit.WinUI.Controls.Shimmer` placeholder during load.
- `InteractionTracker`-driven slide motion with snap-point inertia. Touch, trackpad, mouse wheel, pointer drag, and keyboard (Left / Right) all flow through the same tracker.
- Animated `PipsPager` page indicator with a sliding accent pill that lerps in lockstep with the tracker.
- Per-frame accent crossfade (`HeroCarousel.CurrentAccent`) — RGB-lerps between adjacent slides as the user scrubs, no jump on settle.
- Responsive title typography: title `FontSize` is clamp-scaled to slide width.
- Autoplay with cancel-on-user-input and configurable interval.
- Theme-aware (light / dark) noise range, DPI-reactive surface re-bake.

## Requirements

- Windows App SDK 2.0+
- WinUI 3
- .NET 10 Windows target framework (`net10.0-windows10.0.26100.0`)
- Windows 10 1809 or newer (matches `TargetPlatformMinVersion`)
- ARM64 or x64 build platform (the sample is currently configured for ARM64)

## Use the control

Reference the library project from a WinUI app:

```xml
<ProjectReference Include="..\..\src\Klankhuis.Hero\Klankhuis.Hero.csproj" />
```

Add the XAML namespace (controls live in `Klankhuis.Hero.Controls`):

```xml
<Page
    x:Class="MyApp.MainPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:kh="using:Klankhuis.Hero.Controls">

    <kh:HeroCarousel x:Name="Hero" />
</Page>
```

Build slides as `HeroCarouselItem`s and assign to `ItemsSource`:

```csharp
using Klankhuis.Hero.Controls;
using Windows.UI;

Hero.ItemsSource = new[]
{
    new HeroCarouselItem
    {
        Title    = "Sam Solo",
        Tagline  = "Solo gesprekken, eerlijk en rauw",
        Subtitle = "Sam Hofman",
        Source   = "Audio",
        ImageUri = new Uri("https://example.com/cover.jpg"),
        Accent   = Color.FromArgb(255, 0xC9, 0xA4, 0x5A),
    },
    // …
};
```

### Add an outer halo

`HeroHalo` is an attached-property helper that renders the carousel's current accent as a Composition `DropShadow` on a separate **backdrop** element behind the carousel. The backdrop pattern is required because Composition shadows hosted via `SetElementChildVisual` are bounded by the host element's render slot — to let the halo extend past the carousel, the shadow has to live on a different element whose own slot is large enough.

```xml
<Grid Padding="48,40,48,80">
    <!-- Backdrop spans every row, drawn first so it sits behind all
         page content. Its render slot defines the halo's max extent. -->
    <Grid x:Name="HaloBackdrop"
          Grid.RowSpan="3"
          Background="Transparent"
          IsHitTestVisible="False" />

    <!-- … your page content … -->

    <kh:HeroCarousel x:Name="Hero" />
</Grid>
```

Wire the source in code-behind (`x:Bind` on attached properties is unreliable in WinAppSDK 2.0):

```csharp
HeroHalo.SetSource(HaloBackdrop, Hero);
```

`HeroHalo` watches `Hero.CurrentAccent` (per-frame RGB lerp), positions the shadow at `Hero.TransformToVisual(HaloBackdrop)`, and re-syncs on every layout pass. Override defaults with `HeroHalo.SetBlurRadius(...)` / `SetCornerRadius(...)` / `SetIntensity(...)`.

### Side-rail tiles

`SideCard` is the companion Microsoft-Store-style category tile. Same accent + cover URI as the carousel item, plus an `Eyebrow` chip and `Big` flag for the larger top tile.

```xml
<kh:SideCard x:Name="BigCard"
             Big="True"
             Eyebrow="CATEGORIE"
             Label="Nieuws &amp; politiek" />
```

```csharp
BigCard.ImageUri = new Uri("https://example.com/category-cover.jpg");
BigCard.Accent   = Color.FromArgb(255, 0xD4, 0xA3, 0x73);
```

## Public API

### `HeroCarousel` (Control)

| Property / event | Type | Default | Notes |
|---|---|---|---|
| `ItemsSource` | `IList<HeroCarouselItem>?` | `null` | Setting rebuilds the slide tree. |
| `SelectedIndex` | `int` | `0` | Updated on tracker idle; setting snaps instantly. |
| `CurrentAccent` | `Color` | `(0,0,0,0)` | Read-only-from-consumer. RGB-lerped per frame; alpha = 255 once items load. |
| `Autoplay` | `bool` | `true` | Pauses + restarts on user navigation. |
| `AutoplayInterval` | `TimeSpan` | `5500 ms` | |
| `SelectedIndexChanged` | event `TypedEventHandler<HeroCarousel, int>` | — | Fires on tracker idle, not per-frame. |

### `HeroCarouselItem` (POCO)

| Field | Type | Notes |
|---|---|---|
| `Title` | `string` | Headline. |
| `Tagline` | `string` | One-line description. |
| `Subtitle` | `string` | Author / network — rendered in mono. |
| `Source` | `string` | Eyebrow chip text (e.g., `"Audio"`). |
| `ImageUri` | `Uri?` | HTTPS cover artwork. Loaded async + GPU-baked into the backdrop. |
| `Accent` | `Color` | Drives the radial accent wash + halo + glow. |
| `Tag` | `object?` | Arbitrary host payload (e.g., a podcast URI). |

### `SideCard` (Control)

| Property | Type | Default | Notes |
|---|---|---|---|
| `Big` | `bool` | `false` | Larger top-of-rail variant. |
| `Label` | `string` | `""` | Primary title. |
| `Eyebrow` | `string` | `""` | Optional category chip above the label. |
| `Accent` | `Color` | sky-blue | Drives the diagonal wash + top-left highlight. |
| `ImageUri` | `Uri?` | `null` | Right-anchored cover image, fades into the wash via a `CompositionMaskBrush`. |

### `HeroHalo` (static class — attached properties)

| Property | Type | Default | Notes |
|---|---|---|---|
| `Source` | `HeroCarousel?` | `null` | Setting attaches; `null` detaches and frees Composition resources. |
| `BlurRadius` | `double` | `120` | Visible halo extent ≈ `1.5 × BlurRadius` before alpha decay. |
| `CornerRadius` | `double` | `12` | Should match the carousel's frame `CornerRadius`. |
| `Intensity` | `double` | `0.9` | Halo alpha multiplier (0–1). Raise for accents that match the page bg. |

## Build

From the repository root:

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1"
$Platform = $env:PROCESSOR_ARCHITECTURE
dotnet build .\Klankhuis.slnx -c Debug -p:Platform=$Platform
```

Or just build the sample (the library builds transitively):

```powershell
dotnet build .\samples\Klankhuis.Hero.Sample\Klankhuis.Hero.Sample.csproj -c Debug -p:Platform=ARM64
```

The sample app is MSIX-packaged, so Visual Studio or Windows Developer Mode is required for deploy/run workflows.

## Architecture notes

- **Bake-once strategy** — every slide's backdrop is rendered once into a `CompositionDrawingSurface` via `CanvasComposition.CreateDrawingSession`. The effect graph (Source → `Transform2DEffect` → `GaussianBlurEffect` → `ExposureEffect` → accent radial wash → diagonal accent linear → procedural noise via `PixelShaderEffect<NoiseShader>` wrapped in `PremultiplyEffect` → vignette) only re-runs on accent / source / theme / DPI / device-lost change. Mirrors the Microsoft Store's pattern from the ComputeSharp paper §4.1.
- **Off-thread motion** — slide `Offset.X`, content `Scale`, cover scale and z-ordering are all `ExpressionAnimation`s keyed off `InteractionTracker.Position.X`. Snap-to-slide via `InteractionTrackerInertiaRestingValue`.
- **Cover image** — `LoadedImageSurface` for the bitmap, wrapped in a `CompositionMaskBrush` against a `CompositionVisualSurface`-backed rounded-rect mask so the rounded corners don't clip the drop shadow.
- **Pip indicator** — `PipsPager` for keyboard-accessible page navigation, plus a separate sliding accent pill driven by an `ExpressionAnimation` on `Translation.X` that lerps in lockstep with the tracker.
- **Halo** — `HeroHalo`'s `SpriteVisual` is hosted on the consumer's backdrop element via `ElementCompositionPreview.SetElementChildVisual`, sized to the carousel's `ActualSize` and offset via `TransformToVisual(backdrop)`. Re-synced on every `LayoutUpdated` / `SizeChanged`.
