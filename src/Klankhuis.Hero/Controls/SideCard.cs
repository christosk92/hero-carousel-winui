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

        root.Children.InsertAtTop(_washVisual);
        root.Children.InsertAtTop(_highlightVisual);
        root.Children.InsertAtTop(_coverVisual);

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

        // Diagonal base — full accent at the top-left → 88%-black-mixed at the
        // bottom-right (matches the CSS 125deg gradient).
        var linear = _compositor.CreateLinearGradientBrush();
        linear.MappingMode = CompositionMappingMode.Relative;
        // 125° in CSS measures clockwise from the top — that's roughly the
        // top-left → bottom-right diagonal on a card.
        linear.StartPoint = new Vector2(0f, 0f);
        linear.EndPoint = new Vector2(1f, 1f);
        var stop0 = _compositor.CreateColorGradientStop();
        stop0.Offset = 0f;
        stop0.Color = Accent;
        var stop1 = _compositor.CreateColorGradientStop();
        stop1.Offset = 0.45f;
        stop1.Color = AccentMath.WithBlack(Accent, 0.55);
        var stop2 = _compositor.CreateColorGradientStop();
        stop2.Offset = 1f;
        stop2.Color = AccentMath.WithBlack(Accent, 0.88);
        linear.ColorStops.Add(stop0);
        linear.ColorStops.Add(stop1);
        linear.ColorStops.Add(stop2);
        _washVisual.Brush = linear;

        // Top-left highlight — a small bright spot fading to transparent.
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
        // Cover anchored to the right, ~60% (small) / 55% (big) wide, full height.
        var widthFraction = Big ? 0.55f : 0.60f;
        _coverVisual.RelativeSizeAdjustment = new Vector2(widthFraction, 1f);
        _coverVisual.RelativeOffsetAdjustment = new Vector3(1f - widthFraction, 0f, 0f);
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
        var imageBrush = _compositor.CreateSurfaceBrush(_coverSurface);
        imageBrush.Stretch = CompositionStretch.UniformToFill;

        // Mask brush — true linear ramp from transparent at the left edge
        // to opaque at the right edge. The previous version reached full
        // opacity at 30–35 % and then plateaued, which read as a hard
        // shoulder where the wash visibly cut off into the image. Using
        // two stops at 0 and 1 spreads the transition across the entire
        // card width so the wash decays smoothly into the cover.
        var mask = _compositor.CreateLinearGradientBrush();
        mask.MappingMode = CompositionMappingMode.Relative;
        mask.StartPoint = new Vector2(0f, 0.5f);
        mask.EndPoint = new Vector2(1f, 0.5f);
        var ms0 = _compositor.CreateColorGradientStop();
        ms0.Offset = 0f;
        ms0.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var ms1 = _compositor.CreateColorGradientStop();
        ms1.Offset = 1f;
        ms1.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        mask.ColorStops.Add(ms0);
        mask.ColorStops.Add(ms1);

        var maskBrush = _compositor.CreateMaskBrush();
        maskBrush.Source = imageBrush;
        maskBrush.Mask = mask;

        _coverVisual.Brush = maskBrush;
    }
}
