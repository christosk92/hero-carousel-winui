using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// Static helper that renders the ambient warm-accent halo for a
/// <see cref="HeroCarousel"/> using the "backdrop + cast" pattern that
/// matches the Community Toolkit's <c>AttachedDropShadow</c>: the
/// element this attached property is set on becomes the **backdrop** —
/// a transparent host on which the Composition <see cref="DropShadow"/>
/// is rendered — and the <see cref="SourceProperty"/> value points at
/// the carousel whose position, size, and accent the shadow tracks.
/// </summary>
/// <remarks>
/// <para>Why a separate backdrop, not <c>SetElementChildVisual</c> on a
/// sibling of the carousel: the WinUI render pipeline bounds Composition
/// children of an XAML element to the element's render slot, regardless
/// of whether you oversize the <see cref="SpriteVisual"/> or push it
/// back with a negative <c>Offset</c>. The only way to make the shadow
/// extend past the carousel's slot is to render it on a different element
/// whose own slot is large enough — e.g., an outer-Grid-level transparent
/// host that spans the page's content area. The shadow's position within
/// that host is then derived via
/// <see cref="UIElement.TransformToVisual"/>.</para>
/// <para>The shadow's colour tracks the carousel's
/// <see cref="HeroCarousel.CurrentAccentProperty"/>, which the carousel
/// updates every
/// <see cref="Composition.HeroInteraction.PositionChanged"/> tick by
/// RGB-lerping between adjacent slides' seed accents — so the halo hue
/// rides the scrub continuously rather than snapping on idle.</para>
/// <para>Usage in the consumer's page:</para>
/// <code>
/// &lt;Grid Padding="48,40,48,80"&gt;
///   &lt;Grid.RowDefinitions&gt;...&lt;/Grid.RowDefinitions&gt;
///   &lt;!-- Backdrop spans every row, drawn first so it sits behind
///        all other page content. --&gt;
///   &lt;Grid x:Name="HaloBackdrop"
///         Grid.RowSpan="3"
///         Background="Transparent"
///         IsHitTestVisible="False"/&gt;
///   ...
///   &lt;kh:HeroCarousel x:Name="Hero" .../&gt;
/// &lt;/Grid&gt;
/// </code>
/// <para>Wire the source in code-behind (<c>x:Bind</c> on attached
/// properties is unreliable in WinAppSDK 2.0):</para>
/// <code>
/// HeroHalo.SetSource(HaloBackdrop, Hero);
/// </code>
/// </remarks>
public static class HeroHalo
{
    /// <summary>
    /// The <see cref="HeroCarousel"/> whose <see cref="HeroCarousel.CurrentAccent"/>
    /// drives this element's halo colour. Setting to <see langword="null"/>
    /// detaches the halo and frees its Composition resources.
    /// </summary>
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(HeroCarousel), typeof(HeroHalo),
            new PropertyMetadata(null, OnSourceChanged));

    public static HeroCarousel? GetSource(DependencyObject obj) =>
        (HeroCarousel?)obj.GetValue(SourceProperty);
    public static void SetSource(DependencyObject obj, HeroCarousel? value) =>
        obj.SetValue(SourceProperty, value);

    /// <summary>
    /// Composition <see cref="DropShadow.BlurRadius"/>. Default 100 px.
    /// The visible halo extent is roughly <c>BlurRadius × 1.5</c>; raising
    /// this softens the falloff and reaches further from the host element.
    /// </summary>
    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.RegisterAttached(
            "BlurRadius", typeof(double), typeof(HeroHalo),
            new PropertyMetadata(100.0, OnBlurRadiusChanged));

    public static double GetBlurRadius(DependencyObject obj) =>
        (double)obj.GetValue(BlurRadiusProperty);
    public static void SetBlurRadius(DependencyObject obj, double value) =>
        obj.SetValue(BlurRadiusProperty, value);

    /// <summary>
    /// Corner radius of the rounded-rect mask the shadow casts from.
    /// Should match the carousel frame's own <c>CornerRadius</c>
    /// (default 12) so the halo's softened edge tracks the visible card.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(double), typeof(HeroHalo),
            new PropertyMetadata(12.0, OnCornerRadiusChanged));

    public static double GetCornerRadius(DependencyObject obj) =>
        (double)obj.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject obj, double value) =>
        obj.SetValue(CornerRadiusProperty, value);

    /// <summary>
    /// Halo opacity multiplier (0.0 – 1.0). Default 0.4 — gives a soft
    /// ambient ring that picks up the active slide's accent without
    /// competing with the carousel itself for attention. Raise toward
    /// 0.7+ for a saturated "now-playing" glow; down to 0.2 for a
    /// barely-there warm cast.
    /// </summary>
    public static readonly DependencyProperty IntensityProperty =
        DependencyProperty.RegisterAttached(
            "Intensity", typeof(double), typeof(HeroHalo),
            new PropertyMetadata(0.4, OnIntensityChanged));

    public static double GetIntensity(DependencyObject obj) =>
        (double)obj.GetValue(IntensityProperty);
    public static void SetIntensity(DependencyObject obj, double value) =>
        obj.SetValue(IntensityProperty, value);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State", typeof(HaloState), typeof(HeroHalo),
            new PropertyMetadata(null));

    private static HaloState? GetState(DependencyObject d) =>
        (HaloState?)d.GetValue(StateProperty);
    private static void SetState(DependencyObject d, HaloState? v) =>
        d.SetValue(StateProperty, v);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement target) return;

        // Existing state (if any) belongs to the previous Source.
        // Detach it before swapping in the new one.
        var existing = GetState(target);
        existing?.Dispose();
        SetState(target, null);

        if (e.NewValue is HeroCarousel newSource)
        {
            var state = new HaloState(target, newSource);
            SetState(target, state);
        }
    }

    private static void OnBlurRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        GetState(d)?.UpdateBlurRadius((float)(double)e.NewValue);

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        GetState(d)?.UpdateCornerRadius((float)(double)e.NewValue);

    private static void OnIntensityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        GetState(d)?.RefreshAccentColor();

    /// <summary>
    /// Per-target Composition state. One <see cref="HaloState"/> exists
    /// for each <see cref="FrameworkElement"/> that has
    /// <see cref="HeroHalo.SourceProperty"/> set. Owns the mask geometry,
    /// drop shadow, and brushless <see cref="SpriteVisual"/>; subscribes
    /// to the source carousel's <see cref="HeroCarousel.CurrentAccentProperty"/>
    /// via <see cref="DependencyObject.RegisterPropertyChangedCallback"/>
    /// and writes the lerped colour into the shadow on every change.
    /// </summary>
    private sealed class HaloState
    {
        private readonly FrameworkElement _target;   // the backdrop element (CastTo)
        private readonly HeroCarousel _source;       // the carousel whose accent + bounds we track
        private SpriteVisual? _haloVisual;
        private DropShadow? _shadow;
        private CompositionRoundedRectangleGeometry? _maskGeom;
        private ShapeVisual? _maskShape;
        private CompositionVisualSurface? _maskSurface;
        private long _accentToken;
        private bool _attached;

        public HaloState(FrameworkElement target, HeroCarousel source)
        {
            _target = target;
            _source = source;
            target.Loaded += OnLoaded;
            target.Unloaded += OnUnloaded;
            if (target.IsLoaded)
                Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private void Attach()
        {
            if (_attached) return;
            var backdropVisual = ElementCompositionPreview.GetElementVisual(_target);
            var compositor = backdropVisual.Compositor;

            // ── Mask source ─────────────────────────────────────────────
            // ShapeVisual that paints a single white rounded rect at
            // *carousel* size. Sized imperatively from `_source.ActualSize`
            // in SyncSizeAndPosition along with the visual surface and the
            // halo's own size/offset. All four (ShapeVisual.Size,
            // VisualSurface.SourceSize, RoundedRectangleGeometry.Size,
            // SpriteVisual.Size) must agree or the visual surface captures
            // a zero-pixel image and the shadow has no shape to cast from.
            var maskShape = compositor.CreateShapeVisual();
            _maskShape = maskShape;

            var cornerRadius = (float)GetCornerRadius(_target);
            var maskGeom = compositor.CreateRoundedRectangleGeometry();
            maskGeom.CornerRadius = new Vector2(cornerRadius, cornerRadius);
            _maskGeom = maskGeom;

            var fill = compositor.CreateSpriteShape(maskGeom);
            fill.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
            maskShape.Shapes.Add(fill);

            var visualSurface = compositor.CreateVisualSurface();
            visualSurface.SourceVisual = maskShape;
            _maskSurface = visualSurface;

            var maskBrush = compositor.CreateSurfaceBrush(visualSurface);

            // ── Drop shadow ────────────────────────────────────────────
            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = (float)GetBlurRadius(_target);
            shadow.Offset = Vector3.Zero;
            shadow.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            shadow.Mask = maskBrush;
            _shadow = shadow;

            // ── Halo visual ────────────────────────────────────────────
            // Brushless SpriteVisual hosted as a child of the *backdrop*
            // (`_target`). Position and size are written imperatively
            // each time SyncSizeAndPosition runs, derived from the source
            // carousel's ActualSize and its position within the backdrop
            // via TransformToVisual.
            var haloVisual = compositor.CreateSpriteVisual();
            haloVisual.Shadow = shadow;
            _haloVisual = haloVisual;
            ElementCompositionPreview.SetElementChildVisual(_target, haloVisual);

            // Sync size+offset on EVERY layout pass that touches either
            // the source or the backdrop. LayoutUpdated alone is the
            // documented hook, but it doesn't always fire on the very
            // first pass after Loaded (especially when the source is
            // already laid out by the time we attach). SizeChanged on
            // both elements catches the bootstrap case + any resize.
            _source.LayoutUpdated += OnLayoutUpdated;
            _source.SizeChanged += OnSizeChanged;
            _target.SizeChanged += OnSizeChanged;

            // Subscribe to accent updates and seed the initial colour from
            // whatever value the carousel currently has.
            _accentToken = _source.RegisterPropertyChangedCallback(
                HeroCarousel.CurrentAccentProperty, OnAccentChanged);
            RefreshAccentColor();

            // Initial sync — may no-op if source hasn't been measured
            // yet; the LayoutUpdated/SizeChanged handlers will pick it up.
            SyncSizeAndPosition();

            _attached = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            _source.LayoutUpdated -= OnLayoutUpdated;
            _source.SizeChanged -= OnSizeChanged;
            _target.SizeChanged -= OnSizeChanged;
            _source.UnregisterPropertyChangedCallback(
                HeroCarousel.CurrentAccentProperty, _accentToken);
            ElementCompositionPreview.SetElementChildVisual(_target, null);
            _haloVisual = null;
            _shadow = null;
            _maskGeom = null;
            _maskShape = null;
            _maskSurface = null;
            _accentToken = 0;
            _attached = false;
        }

        public void Dispose()
        {
            Detach();
            _target.Loaded -= OnLoaded;
            _target.Unloaded -= OnUnloaded;
        }

        private void OnLayoutUpdated(object? sender, object e) => SyncSizeAndPosition();
        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => SyncSizeAndPosition();

        /// <summary>
        /// Mirrors the source carousel's render size and its position in
        /// backdrop coordinates onto the halo SpriteVisual + mask geometry.
        /// Called on every <see cref="FrameworkElement.LayoutUpdated"/>
        /// tick — that event is debounced to actual layout changes by
        /// the framework, so the cost is bounded.
        /// </summary>
        private void SyncSizeAndPosition()
        {
            if (_haloVisual is null || _maskGeom is null
                || _maskShape is null || _maskSurface is null) return;
            var size = _source.ActualSize;
            if (size.X <= 0 || size.Y <= 0) return;

            // Position of the source's top-left corner in the backdrop's
            // coordinate system. Both elements must be loaded for
            // TransformToVisual to return a meaningful transform — if
            // either isn't, swallow the COMException and try again on the
            // next layout tick.
            Point topLeft;
            try
            {
                topLeft = _source
                    .TransformToVisual(_target)
                    .TransformPoint(new Point(0, 0));
            }
            catch
            {
                return;
            }

            _haloVisual.Size = size;
            _haloVisual.Offset = new Vector3((float)topLeft.X, (float)topLeft.Y, 0);
            _maskGeom.Size = size;
            // The mask pipeline has TWO size knobs that both need to
            // match `size` or the rounded rect captured into the visual
            // surface comes out as a zero-pixel (or wrong-sized) image,
            // and the DropShadow has no usable alpha to cast from.
            _maskShape.Size = size;
            _maskSurface.SourceSize = size;
        }

        public void UpdateBlurRadius(float value)
        {
            if (_shadow is not null) _shadow.BlurRadius = value;
        }

        public void UpdateCornerRadius(float value)
        {
            if (_maskGeom is not null)
                _maskGeom.CornerRadius = new Vector2(value, value);
        }

        /// <summary>
        /// Re-applies <see cref="HeroCarousel.CurrentAccent"/> through the
        /// configured <see cref="IntensityProperty"/> alpha multiplier.
        /// Called both from the carousel's accent callback and when
        /// <c>Intensity</c> itself changes.
        /// </summary>
        public void RefreshAccentColor()
        {
            if (_shadow is null) return;
            var c = _source.CurrentAccent;
            var intensity = Math.Clamp(GetIntensity(_target), 0.0, 1.0);
            var shadowAlpha = (byte)Math.Round(255.0 * intensity);
            _shadow.Color = Windows.UI.Color.FromArgb(shadowAlpha, c.R, c.G, c.B);
        }

        private void OnAccentChanged(DependencyObject sender, DependencyProperty dp)
            => RefreshAccentColor();
    }
}
