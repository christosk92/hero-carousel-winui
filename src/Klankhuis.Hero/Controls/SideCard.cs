using System;
using System.Numerics;
using Klankhuis.Hero.Theming;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

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
public sealed partial class SideCard : Control
{
    private const string PartCompositionHost = "PART_CompositionHost";

    private Compositor? _compositor;
    private SpriteVisual? _washVisual;
    private SpriteVisual? _highlightVisual;
    private SpriteVisual? _coverVisual;
    private SpriteVisual? _spotlightVisual;
    private LoadedImageSurface? _coverSurface;
    private Grid? _compositionHost;
    private bool _isPointerOver;
    private bool _isPressed;

    /// <summary>
    /// Raised when the user clicks the card (mouse, touch, or pen) or
    /// activates it via keyboard (Enter / Space). Standard click semantics:
    /// fires on PointerReleased only when the release happens while the
    /// pointer is still over the card.
    /// </summary>
    public event TypedEventHandler<SideCard, RoutedEventArgs>? Click;

    public SideCard()
    {
        DefaultStyleKey = typeof(SideCard);

        // Interactivity. The composition host inside the template has
        // IsHitTestVisible="False" so pointer events bubble up to this
        // Control. Default cursor is Hand to surface the affordance —
        // SideCard is always clickable when wired up by a consumer.
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateCoverSize();

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        KeyDown += OnKeyDown;
    }

    /// <summary>Large-format card (taller, bigger label). Default false.</summary>
    public static readonly DependencyProperty BigProperty = DependencyProperty.Register(
        nameof(Big), typeof(bool), typeof(SideCard),
        new PropertyMetadata(false, (d, _) =>
        {
            var card = (SideCard)d;
            card.UpdateCoverSize();
            card.ApplySizeState();
        }));
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
        ApplySizeState();
    }

    private void ApplySizeState()
    {
        // Generic.xaml's SideCard template defines a VisualStateGroup named
        // "SizeStates" with "DefaultState" and "BigState"; Big toggles the
        // Label/Eyebrow font sizes. Call with useTransitions=false so the
        // size change is instantaneous (no fade between font sizes).
        VisualStateManager.GoToState(this, Big ? "BigState" : "DefaultState", useTransitions: false);
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

        // Layer 3: hover spotlight — centered white radial gradient, hidden
        // at rest (Opacity=0). Fades in on PointerEntered to ~0.22 opacity
        // and back to 0 on PointerExited. Sits on top of every other layer
        // so its glow reads over both the cover and the accent wash.
        _spotlightVisual = _compositor.CreateSpriteVisual();
        _spotlightVisual.RelativeSizeAdjustment = Vector2.One;
        _spotlightVisual.Opacity = 0f;
        var spot = _compositor.CreateRadialGradientBrush();
        spot.MappingMode = CompositionMappingMode.Relative;
        spot.EllipseCenter = new Vector2(0.5f, 0.5f);
        spot.EllipseRadius = new Vector2(0.75f, 0.75f);
        var ss0 = _compositor.CreateColorGradientStop();
        ss0.Offset = 0f;
        ss0.Color = Windows.UI.Color.FromArgb(140, 255, 255, 255);
        var ss1 = _compositor.CreateColorGradientStop();
        ss1.Offset = 1f;
        ss1.Color = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        spot.ColorStops.Add(ss0);
        spot.ColorStops.Add(ss1);
        _spotlightVisual.Brush = spot;

        // Stack order, back to front:
        //   1. Cover image (full image, no mask) — bottom layer.
        //   2. Wash — translucent accent overlay on top of the cover,
        //      heavy on the left and fading to transparent on the right
        //      so the cover and wash *merge* in the middle (carousel
        //      image-bg pattern). Wash is rendered ON TOP of the image,
        //      not below, so its alpha lets the image bleed through.
        //   3. Highlight — small bright top-left spot, optional accent.
        //   4. Spotlight — centered hover glow, top layer.
        root.Children.InsertAtTop(_coverVisual);
        root.Children.InsertAtTop(_washVisual);
        root.Children.InsertAtTop(_highlightVisual);
        root.Children.InsertAtTop(_spotlightVisual);

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
        _spotlightVisual = null;
        _compositor = null;
        _isPointerOver = false;
        _isPressed = false;
    }

    // ─── Interactivity: hover, press, click ──────────────────────────

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        ApplyVisualState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        _isPressed = false;
        ApplyVisualState();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only left mouse button / pen barrel / touch should count as a press;
        // ignore right-click so the card doesn't visibly "press" during context-menu invocation.
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed || props.IsXButton1Pressed || props.IsXButton2Pressed)
            return;

        _isPressed = true;
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        ApplyVisualState();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasPressed = _isPressed;
        _isPressed = false;
        if (e.Pointer is not null)
            ReleasePointerCapture(e.Pointer);
        ApplyVisualState();
        if (wasPressed && _isPointerOver)
            RaiseClick();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        ApplyVisualState();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Standard Button-like keyboard activation.
        if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space || e.Key == VirtualKey.GamepadA)
        {
            e.Handled = true;
            RaiseClick();
        }
    }

    private void RaiseClick()
    {
        Click?.Invoke(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Drives composition scale + spotlight opacity off the
    /// <see cref="_isPointerOver"/> and <see cref="_isPressed"/> flags.
    /// Scales the SideCard's own visual (which includes the templated
    /// Border chrome + composition gradient stack) so the whole card lifts
    /// as a unit. Animations use a 160 ms ease-out for hover and 90 ms
    /// for press so the press feels snappier than the hover settle.
    /// </summary>
    private void ApplyVisualState()
    {
        if (_compositor is null || _spotlightVisual is null) return;

        var controlVisual = ElementCompositionPreview.GetElementVisual(this);
        controlVisual.CenterPoint = new Vector3((float)ActualWidth * 0.5f, (float)ActualHeight * 0.5f, 0f);

        float scaleTarget = _isPressed ? 0.985f : (_isPointerOver ? 1.03f : 1.0f);
        float spotlightTarget = _isPointerOver ? (_isPressed ? 0.12f : 0.22f) : 0f;
        var scaleDuration = TimeSpan.FromMilliseconds(_isPressed ? 90 : 160);
        var spotlightDuration = TimeSpan.FromMilliseconds(_isPointerOver ? 160 : 220);

        var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(1f, new Vector3(scaleTarget, scaleTarget, 1f));
        scaleAnim.Duration = scaleDuration;
        controlVisual.StartAnimation(nameof(Visual.Scale), scaleAnim);

        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(1f, spotlightTarget);
        opacityAnim.Duration = spotlightDuration;
        _spotlightVisual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
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
