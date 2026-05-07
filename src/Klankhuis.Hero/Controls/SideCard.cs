using System;
using System.Numerics;
using Klankhuis.Hero.Theming;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// Companion to <see cref="HeroCarousel"/> for the side-rail tile pattern from
/// the Microsoft Store ("Camo Studio" / "Gave nieuwe games" cards). Renders as
/// a single rounded card with a Composition gradient wash drawn from the
/// supplied <see cref="Accent"/> colour, plus an optional <see cref="ImageUri"/>
/// cover that's anchored to the right and fades into the wash on its left
/// edge via a <see cref="CompositionMaskBrush"/>.
/// </summary>
[TemplatePart(Name = PartCompositionHost, Type = typeof(Grid))]
public sealed class SideCard : Control
{
    private const string PartCompositionHost = "PART_CompositionHost";

    private Compositor? _compositor;
    private SpriteVisual? _washVisual;
    private SpriteVisual? _highlightVisual;
    private SpriteVisual? _coverVisual;
    private LoadedImageSurface? _coverSurface;
    private Grid? _compositionHost;

    public SideCard()
    {
        DefaultStyleKey = typeof(SideCard);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateCoverSize();
    }

    /// <summary>Large-format card (taller, bigger label). Default false.</summary>
    public static readonly DependencyProperty BigProperty = DependencyProperty.Register(
        nameof(Big), typeof(bool), typeof(SideCard),
        new PropertyMetadata(false, (d, _) => ((SideCard)d).UpdateCoverSize()));
    public bool Big
    {
        get => (bool)GetValue(BigProperty);
        set => SetValue(BigProperty, value);
    }

    /// <summary>Primary label (e.g. "Nieuws &amp; politiek").</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SideCard),
        new PropertyMetadata(string.Empty));
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Optional eyebrow shown above the label (e.g. "CATEGORIE").</summary>
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(
        nameof(Eyebrow), typeof(string), typeof(SideCard),
        new PropertyMetadata(string.Empty));
    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    /// <summary>
    /// Accent colour driving the gradient wash. The wash runs diagonally from
    /// a near-accent top-left to an 88%-black-mixed bottom-right, matching the
    /// React design's <c>radial(top-left highlight) + linear(125deg)</c> stack.
    /// </summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Windows.UI.Color), typeof(SideCard),
        new PropertyMetadata(
            Windows.UI.Color.FromArgb(255, 0x60, 0xCD, 0xFF),
            (d, _) => ((SideCard)d).UpdateWashColors()));
    public Windows.UI.Color Accent
    {
        get => (Windows.UI.Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>
    /// Optional cover image — anchored to the right edge with a left-fade
    /// mask so it dissolves into the accent wash.
    /// </summary>
    public static readonly DependencyProperty ImageUriProperty = DependencyProperty.Register(
        nameof(ImageUri), typeof(Uri), typeof(SideCard),
        new PropertyMetadata(null, (d, _) => ((SideCard)d).LoadCoverImage()));
    public Uri? ImageUri
    {
        get => (Uri?)GetValue(ImageUriProperty);
        set => SetValue(ImageUriProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _compositionHost = GetTemplateChild(PartCompositionHost) as Grid;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_compositionHost is null) return;
        _compositor = ElementCompositionPreview.GetElementVisual(_compositionHost).Compositor;

        var root = _compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;

        // Layer 0: linear diagonal wash — accent → near-black
        _washVisual = _compositor.CreateSpriteVisual();
        _washVisual.RelativeSizeAdjustment = Vector2.One;

        // Layer 1: top-left radial highlight
        _highlightVisual = _compositor.CreateSpriteVisual();
        _highlightVisual.RelativeSizeAdjustment = Vector2.One;

        // Layer 2: cover image, right-anchored with left-fade mask
        _coverVisual = _compositor.CreateSpriteVisual();

        // Stack order, back to front:
        //   1. Cover image (full image, no mask) — bottom layer.
        //   2. Wash — translucent accent overlay on top of the cover,
        //      heavy on the left and fading to transparent on the right
        //      so the cover and wash *merge* in the middle (carousel
        //      image-bg pattern). Wash is rendered ON TOP of the image,
        //      not below, so its alpha lets the image bleed through.
        //   3. Highlight — small bright top-left spot, optional accent.
        root.Children.InsertAtTop(_coverVisual);
        root.Children.InsertAtTop(_washVisual);
        root.Children.InsertAtTop(_highlightVisual);

        ElementCompositionPreview.SetElementChildVisual(_compositionHost, root);

        UpdateWashColors();
        UpdateCoverSize();
        LoadCoverImage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_compositionHost is not null)
            ElementCompositionPreview.SetElementChildVisual(_compositionHost, null);
        _coverSurface?.Dispose();
        _coverSurface = null;
        _washVisual = null;
        _highlightVisual = null;
        _coverVisual = null;
        _compositor = null;
    }

    private void UpdateWashColors()
    {
        if (_compositor is null || _washVisual is null || _highlightVisual is null) return;

        // The wash is now a tinted version of the COVER IMAGE itself,
        // masked by a horizontal alpha gradient — not a flat colour
        // slab. BlendEffect.Multiply takes the image and the accent
        // colour; the result is the image with its pixels multiplied
        // by the accent (image's lights become accent-coloured, darks
        // stay dark, mid-tones become darkened accent). The mask then
        // shows this tinted image only on the left, fading to fully
        // transparent toward the right where the un-tinted bottom
        // layer (_coverVisual) shows through. Image structure
        // (highlights, edges, shadows) stays visible everywhere — the
        // accent merges INTO the image's pixels rather than sitting
        // ON TOP as a coloured rectangle.
        if (_coverSurface is not null)
        {
            var imageBrush = _compositor.CreateSurfaceBrush(_coverSurface);
            imageBrush.Stretch = CompositionStretch.UniformToFill;

            var accentBrush = _compositor.CreateColorBrush(Accent);

            var blend = new Microsoft.Graphics.Canvas.Effects.BlendEffect
            {
                Mode = Microsoft.Graphics.Canvas.Effects.BlendEffectMode.Multiply,
                Background = new CompositionEffectSourceParameter("image"),
                Foreground = new CompositionEffectSourceParameter("accent"),
            };
            var factory = _compositor.CreateEffectFactory(blend);
            var tintedBrush = factory.CreateBrush();
            tintedBrush.SetSourceParameter("image", imageBrush);
            tintedBrush.SetSourceParameter("accent", accentBrush);

            var mask = _compositor.CreateLinearGradientBrush();
            mask.MappingMode = CompositionMappingMode.Relative;
            mask.StartPoint = new Vector2(0f, 0.5f);
            mask.EndPoint = new Vector2(1f, 0.5f);
            var m0 = _compositor.CreateColorGradientStop();
            m0.Offset = 0f;
            m0.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var m1 = _compositor.CreateColorGradientStop();
            m1.Offset = 0.22f;
            m1.Color = Windows.UI.Color.FromArgb(235, 0, 0, 0);
            var m2 = _compositor.CreateColorGradientStop();
            m2.Offset = 0.50f;
            m2.Color = Windows.UI.Color.FromArgb(110, 0, 0, 0);
            var m3 = _compositor.CreateColorGradientStop();
            m3.Offset = 0.85f;
            m3.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            mask.ColorStops.Add(m0);
            mask.ColorStops.Add(m1);
            mask.ColorStops.Add(m2);
            mask.ColorStops.Add(m3);

            var maskBrush = _compositor.CreateMaskBrush();
            maskBrush.Source = tintedBrush;
            maskBrush.Mask = mask;

            _washVisual.Brush = maskBrush;
        }
        else
        {
            // No image yet — fall back to a faint accent gradient so
            // the card isn't blank during initial layout.
            var fallback = _compositor.CreateLinearGradientBrush();
            fallback.MappingMode = CompositionMappingMode.Relative;
            fallback.StartPoint = new Vector2(0f, 0.5f);
            fallback.EndPoint = new Vector2(1f, 0.5f);
            var f0 = _compositor.CreateColorGradientStop();
            f0.Offset = 0f;
            f0.Color = Windows.UI.Color.FromArgb(180, Accent.R, Accent.G, Accent.B);
            var f1 = _compositor.CreateColorGradientStop();
            f1.Offset = 1f;
            f1.Color = Windows.UI.Color.FromArgb(0, Accent.R, Accent.G, Accent.B);
            fallback.ColorStops.Add(f0);
            fallback.ColorStops.Add(f1);
            _washVisual.Brush = fallback;
        }

        // Highlight — kept subtle. Top-left bright accent spot adds a
        // touch of life; opacity-capped so it doesn't fight the
        // tinted-image wash for attention.
        _highlightVisual.Opacity = 0.25f;
        var radial = _compositor.CreateRadialGradientBrush();
        radial.MappingMode = CompositionMappingMode.Relative;
        radial.EllipseCenter = new Vector2(0.08f, 0.25f);
        radial.EllipseRadius = new Vector2(0.9f, 1.3f);
        var rs0 = _compositor.CreateColorGradientStop();
        rs0.Offset = 0f;
        rs0.Color = AccentMath.WithWhite(Accent, 0.22);
        var rs1 = _compositor.CreateColorGradientStop();
        rs1.Offset = 0.28f;
        rs1.Color = Accent;
        var rs2 = _compositor.CreateColorGradientStop();
        rs2.Offset = 0.7f;
        rs2.Color = Windows.UI.Color.FromArgb(0, Accent.R, Accent.G, Accent.B);
        radial.ColorStops.Add(rs0);
        radial.ColorStops.Add(rs1);
        radial.ColorStops.Add(rs2);
        _highlightVisual.Brush = radial;
    }

    private void UpdateCoverSize()
    {
        if (_coverVisual is null) return;
        // Cover fills the entire card. The right-anchored 55/60-percent
        // sizing the previous version used was meant to confine the
        // image to a "thumbnail slot" on the right, but that left a hard
        // edge where the cover met the wash slab. With the wash now
        // rendered as a horizontal alpha-fade overlay on TOP of the
        // image (carousel image-bg pattern), the image stretches across
        // the full card and the wash tints its left portion — same
        // composition the main hero gets.
        _coverVisual.RelativeSizeAdjustment = Vector2.One;
        _coverVisual.RelativeOffsetAdjustment = Vector3.Zero;
    }

    private void LoadCoverImage()
    {
        if (_compositor is null || _coverVisual is null) return;
        _coverSurface?.Dispose();
        _coverSurface = null;

        if (ImageUri is null)
        {
            _coverVisual.Brush = null;
            return;
        }

        _coverSurface = LoadedImageSurface.StartLoadFromUri(ImageUri);

        // Bottom layer — clean image filling the card. UniformToFill
        // is the user's "stretch since it's 1:1" preference; some
        // crop is acceptable on wide cards. The tinted wash on top
        // (built in UpdateWashColors) renders the SAME image
        // multiplied by the accent and masked horizontally, so left
        // → right reads as smoothly tinted-to-untinted across the
        // SAME cover artwork — no flat-colour rectangle anywhere.
        var imageBrush = _compositor.CreateSurfaceBrush(_coverSurface);
        imageBrush.Stretch = CompositionStretch.UniformToFill;
        _coverVisual.Brush = imageBrush;

        // Wash now depends on _coverSurface — rebuild it now the
        // surface exists.
        UpdateWashColors();
    }
}
