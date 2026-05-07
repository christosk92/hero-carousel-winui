using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Klankhuis.Hero.Composition;
using Klankhuis.Hero.Surfaces;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// The hero podcast carousel. Composition + Win2D + ComputeSharp port of the
/// React reference design — each slide's heavy backdrop is GPU-baked once
/// to a <see cref="Microsoft.UI.Composition.CompositionDrawingSurface"/>,
/// slide transforms run as off-thread
/// <see cref="Microsoft.UI.Composition.ExpressionAnimation"/>s keyed off a
/// single <see cref="Microsoft.UI.Composition.Interactions.InteractionTracker"/>,
/// and motion never touches the UI thread.
/// </summary>
/// <remarks>
/// Bake-once strategy mirrors the Microsoft Store's pattern from the
/// ComputeSharp paper §4.1: the <c>BackgroundBlur</c> effect graph
/// (Source → Transform2D → GaussianBlur → Saturate → Blend(Accent, Overlay)
/// → Blend(Noise, SoftLight) → Border) renders once to a
/// <c>CompositionDrawingSurface</c> via <c>CanvasComposition</c> and is
/// re-baked only on accent/source change, theme switch, DPI change, or
/// device-lost recovery.
/// </remarks>
[TemplatePart(Name = PartSlideHost, Type = typeof(Grid))]
[TemplatePart(Name = PartOverlayHost, Type = typeof(Grid))]
[TemplatePart(Name = PartPrev, Type = typeof(Button))]
[TemplatePart(Name = PartNext, Type = typeof(Button))]
[TemplatePart(Name = PartPips, Type = typeof(PipsPager))]
[TemplatePart(Name = PartPipIndicator, Type = typeof(Border))]
public sealed class HeroCarousel : Control
{
    private const string PartSlideHost = "PART_SlideHost";
    private const string PartOverlayHost = "PART_OverlayHost";
    private const string PartPrev = "PART_Prev";
    private const string PartNext = "PART_Next";
    private const string PartPips = "PART_Pips";
    private const string PartPipIndicator = "PART_PipIndicator";

    /// <summary>
    /// Width of one pip slot. The retemplated <c>HeroCarouselPipButtonStyle</c>
    /// uses an 18×18 hit area so each pip's centre sits at
    /// <c>index * 18 + 9</c>; the indicator pill is also 18 px wide so a
    /// <c>TranslateX = index * 18</c> aligns the pill with the active pip.
    /// </summary>
    private const double PipSlotWidth = 18.0;

    private Compositor? _compositor;
    private ContainerVisual? _stageRoot;
    private HeroInteraction? _interaction;
    private BakedSurfaceCache? _surfaceCache;
    private readonly List<HeroSlideVisual> _slides = new();
    private readonly List<FrameworkElement> _overlays = new();
    /// <summary>
    /// Per-slide CTA button references so the carousel can re-theme them
    /// once the cover's dominant accent has been extracted async. Index
    /// matches <see cref="_slides"/>.
    /// </summary>
    private readonly List<(Button? Primary, Button? Secondary)> _ctaButtons = new();
    /// <summary>
    /// References to each slide's title <see cref="TextBlock"/> so the
    /// carousel can drive responsive typography — <c>FontSize</c> scales
    /// with slide-host width on every <see cref="UpdateStepFromHostSize"/>
    /// pass. CSS reference: <c>clamp(28px, 5vw, 50px)</c>.
    /// </summary>
    private readonly List<TextBlock> _titleBlocks = new();
    private CancellationTokenSource? _bakeCts;
    private DispatcherTimer? _autoplayTimer;
    private Grid? _slideHost;
    private Grid? _overlayHost;
    private PipsPager? _pips;
    private Border? _pipIndicator;
    /// <summary>
    /// One-shot guard for the pip-indicator's ExpressionAnimation. We can
    /// only wire it once both the template (for <c>_pipIndicator</c>) and
    /// the interaction tracker (for <c>_interaction</c>) exist; the wire
    /// call is idempotent and bails out until both are ready.
    /// </summary>
    private bool _pipExpressionWired;
    /// <summary>
    /// Per-slide accent table the carousel lerps across to publish
    /// <see cref="CurrentAccent"/> — same source of truth the
    /// <see cref="BakedSurfaceCache"/> uses for the radial wash in the
    /// slide's backdrop, so the published accent matches the bg the user
    /// actually sees. We reuse <see cref="HeroCarouselItem.Accent"/>
    /// rather than extracting from the cover image, because the extracted
    /// dominant cover-image colour is often the *foreground* (e.g., the
    /// yellow on a navy podcast cover) and clashes with the navy bake.
    /// </summary>
    private Windows.UI.Color[]? _slideAccents;
    /// <summary>
    /// Re-entrancy guard for the PipsPager round-trip: when the carousel
    /// drives <c>PART_Pips.SelectedPageIndex</c> (after a tracker idle or an
    /// external <see cref="SelectedIndex"/> set), <c>SelectedIndexChanged</c>
    /// fires synchronously and would loop back into <c>GoToOffset</c> if not
    /// suppressed. The guard is also honoured in the handler so a programmatic
    /// write never triggers user-intent navigation.
    /// </summary>
    private bool _updatingPips;

    public HeroCarousel()
    {
        DefaultStyleKey = typeof(HeroCarousel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => InvalidateBackdrops();
    }

    // ─── Public DPs ──────────────────────────────────────────────────────

    /// <summary>
    /// The collection of slides. Setting a new list rebuilds the entire slide
    /// composition tree (releases previous Composition resources and bakes new
    /// backdrops). Reads from this property's backing DP are O(1).
    /// </summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList<HeroCarouselItem>), typeof(HeroCarousel),
        new PropertyMetadata(null, (d, _) => ((HeroCarousel)d).RebuildSlides()));

    /// <inheritdoc cref="ItemsSourceProperty"/>
    public IList<HeroCarouselItem>? ItemsSource
    {
        get => (IList<HeroCarouselItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Currently centered slide. Updated when the
    /// <see cref="Microsoft.UI.Composition.Interactions.InteractionTracker"/>
    /// settles on a snap point (idle state) — never per frame. Setting this
    /// property programmatically snaps to the requested index instantly.
    /// </summary>
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(HeroCarousel),
        new PropertyMetadata(0, (d, e) => ((HeroCarousel)d).OnSelectedIndexExternallyChanged((int)e.NewValue)));

    /// <inheritdoc cref="SelectedIndexProperty"/>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>
    /// The carousel's current ambient accent — a per-frame RGB lerp between
    /// the slides on either side of the continuous tracker position. Driven
    /// by <see cref="HeroInteraction.PositionChanged"/>; consumers (typically
    /// <see cref="HeroHalo"/> via its attached <c>Source</c> property) bind
    /// to this for ambient tint effects that need to track the scrub
    /// without waiting for <see cref="SelectedIndex"/> to settle.
    /// </summary>
    /// <remarks>
    /// Read-only from the consumer's perspective — only the carousel
    /// writes here. The setter is public so XAML markup compilers don't
    /// complain about binding to it, but external writes get overwritten
    /// on the next tracker tick.
    /// </remarks>
    public static readonly DependencyProperty CurrentAccentProperty = DependencyProperty.Register(
        nameof(CurrentAccent), typeof(Windows.UI.Color), typeof(HeroCarousel),
        new PropertyMetadata(Windows.UI.Color.FromArgb(0, 0, 0, 0)));

    /// <inheritdoc cref="CurrentAccentProperty"/>
    public Windows.UI.Color CurrentAccent
    {
        get => (Windows.UI.Color)GetValue(CurrentAccentProperty);
        set => SetValue(CurrentAccentProperty, value);
    }

    /// <summary>
    /// When <see langword="true"/>, the carousel advances one slide every
    /// <see cref="AutoplayInterval"/>. Pauses on pointer hover (planned).
    /// </summary>
    public static readonly DependencyProperty AutoplayProperty = DependencyProperty.Register(
        nameof(Autoplay), typeof(bool), typeof(HeroCarousel),
        new PropertyMetadata(true, (d, _) => ((HeroCarousel)d).UpdateAutoplay()));

    /// <inheritdoc cref="AutoplayProperty"/>
    public bool Autoplay
    {
        get => (bool)GetValue(AutoplayProperty);
        set => SetValue(AutoplayProperty, value);
    }

    /// <summary>Interval between autoplay-driven slide advances. Default 5.5s.</summary>
    public static readonly DependencyProperty AutoplayIntervalProperty = DependencyProperty.Register(
        nameof(AutoplayInterval), typeof(TimeSpan), typeof(HeroCarousel),
        new PropertyMetadata(TimeSpan.FromMilliseconds(5500), (d, _) => ((HeroCarousel)d).UpdateAutoplay()));

    /// <inheritdoc cref="AutoplayIntervalProperty"/>
    public TimeSpan AutoplayInterval
    {
        get => (TimeSpan)GetValue(AutoplayIntervalProperty);
        set => SetValue(AutoplayIntervalProperty, value);
    }

    /// <summary>
    /// Style applied to the per-slide *primary* CTA <see cref="Button"/>
    /// (the stronger / "Install"-style button). Setting this lets consumers
    /// override padding, corner radius, font, border thickness, and any
    /// other layout / typography concerns. The carousel still applies
    /// per-slide accent tinting on top of the style via the button's
    /// local <c>Resources</c> dictionary, so the consumer can change the
    /// button's shape without breaking per-slide theming.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="null"/>; the carousel falls back to
    /// hardcoded compact-glass defaults (Padding 22 × 7, CornerRadius 6,
    /// SemiBold 14 px). Setting <see cref="Background"/> /
    /// <see cref="Control.BorderBrush"/> on the supplied style is a no-op
    /// — those are owned by the per-button accent overrides; restrict
    /// the style to layout / typography setters.
    /// </remarks>
    public static readonly DependencyProperty PrimaryCtaButtonStyleProperty = DependencyProperty.Register(
        nameof(PrimaryCtaButtonStyle), typeof(Style), typeof(HeroCarousel),
        new PropertyMetadata(null, (d, _) => ((HeroCarousel)d).RebuildSlides()));

    /// <inheritdoc cref="PrimaryCtaButtonStyleProperty"/>
    public Style? PrimaryCtaButtonStyle
    {
        get => (Style?)GetValue(PrimaryCtaButtonStyleProperty);
        set => SetValue(PrimaryCtaButtonStyleProperty, value);
    }

    /// <summary>
    /// Style applied to the per-slide *secondary* CTA <see cref="Button"/>
    /// (the lighter / "Learn more"-style button). See
    /// <see cref="PrimaryCtaButtonStyleProperty"/> for the override
    /// semantics; same rules apply.
    /// </summary>
    public static readonly DependencyProperty SecondaryCtaButtonStyleProperty = DependencyProperty.Register(
        nameof(SecondaryCtaButtonStyle), typeof(Style), typeof(HeroCarousel),
        new PropertyMetadata(null, (d, _) => ((HeroCarousel)d).RebuildSlides()));

    /// <inheritdoc cref="SecondaryCtaButtonStyleProperty"/>
    public Style? SecondaryCtaButtonStyle
    {
        get => (Style?)GetValue(SecondaryCtaButtonStyleProperty);
        set => SetValue(SecondaryCtaButtonStyleProperty, value);
    }

    /// <summary>
    /// Raised when <see cref="SelectedIndex"/> changes — fires on tracker idle
    /// after a settle, not while a flick / scrub is in flight.
    /// </summary>
    public event TypedEventHandler<HeroCarousel, int>? SelectedIndexChanged;

    // ─── Lifecycle ───────────────────────────────────────────────────────

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_slideHost is not null)
        {
            _slideHost.SizeChanged -= OnSlideHostSizeChanged;
            _slideHost.PointerPressed -= OnHostPointerPressed;
            _slideHost.PointerWheelChanged -= OnHostPointerWheel;
            _slideHost.KeyDown -= OnHostKeyDown;
        }
        if (_pips is not null)
        {
            _pips.SelectedIndexChanged -= OnPipsSelectedIndexChanged;
        }

        _slideHost = GetTemplateChild(PartSlideHost) as Grid;
        _overlayHost = GetTemplateChild(PartOverlayHost) as Grid;
        _pips = GetTemplateChild(PartPips) as PipsPager;
        _pipIndicator = GetTemplateChild(PartPipIndicator) as Border;

        if (GetTemplateChild(PartPrev) is Button prev)
            prev.Click += (_, _) => GoToOffset(-1);
        if (GetTemplateChild(PartNext) is Button next)
            next.Click += (_, _) => GoToOffset(+1);

        if (_slideHost is { } host)
        {
            host.SizeChanged += OnSlideHostSizeChanged;
            host.PointerPressed += OnHostPointerPressed;
            host.PointerWheelChanged += OnHostPointerWheel;
            host.KeyDown += OnHostKeyDown;
        }

        if (_pips is { } pips)
        {
            pips.SelectedIndexChanged += OnPipsSelectedIndexChanged;
            // Initial seed under the guard — `RebuildSlides` (called from
            // OnLoaded) will overwrite NumberOfPages with the real count.
            SyncPagerToSelection(ItemsSource?.Count ?? 0, SelectedIndex);
        }
    }

    private void SyncPagerToSelection(int pageCount, int selectedIndex)
    {
        if (_pips is null) return;
        _updatingPips = true;
        try
        {
            _pips.NumberOfPages = Math.Max(0, pageCount);
            if (pageCount > 0)
                _pips.SelectedPageIndex = Math.Clamp(selectedIndex, 0, pageCount - 1);
        }
        finally
        {
            _updatingPips = false;
        }
        UpdatePipIndicatorVisibility(pageCount);
        EnsurePipExpressionWired();
    }

    /// <summary>
    /// Pure visibility toggle — the pill's <em>position</em> is driven by an
    /// <see cref="Microsoft.UI.Composition.ExpressionAnimation"/> on its
    /// <c>Translation</c> (see <see cref="EnsurePipExpressionWired"/>), so we
    /// never write <c>TranslateX</c> imperatively from a settle callback.
    /// That keeps the pill in lock-step with the slides during a drag /
    /// flick rather than waiting for the tracker to enter idle.
    /// </summary>
    private void UpdatePipIndicatorVisibility(int pageCount)
    {
        if (_pipIndicator is null) return;
        // Single-slide / empty carousel — the pill has nothing to mark.
        _pipIndicator.Opacity = pageCount > 1 ? 1 : 0;
    }

    /// <summary>
    /// Attaches Composition expression animations that drive the pill's
    /// <c>Translation.X</c> and <c>Scale.X</c> off the carousel's continuous
    /// tracker position, producing the staggered "extend then collapse"
    /// effect: as the user drags from pip <em>i</em> toward pip <em>i+1</em>
    /// the pill's right edge advances first (pill stretches to span both
    /// pips), then its left edge catches up (pill collapses onto the new
    /// pip). Because every slide visual reads from the same
    /// <c>tracker.Position.X</c>, the pill animates <em>in phase</em> with
    /// the slide motion — no settle delay.
    /// </summary>
    /// <remarks>
    /// <para>Math, with <c>p = tracker.Position.X / shared.StepX</c>,
    /// <c>floor = Floor(p)</c>, <c>frac = p - floor</c>, slot width <c>w</c>:</para>
    /// <list type="bullet">
    /// <item><c>leftEdge  = floor·w + Clamp(2·frac − 1, 0, 1)·w</c> — stays at
    ///   pip <em>i</em> until <c>frac = 0.5</c>, then sweeps to pip <em>i+1</em>.</item>
    /// <item><c>scaleX    = 1 + Clamp(2·frac, 0, 1) − Clamp(2·frac − 1, 0, 1)</c> —
    ///   triangle peaking at 2 when <c>frac = 0.5</c> (pill spans two slots),
    ///   returning to 1 at integer pip positions.</item>
    /// </list>
    /// <para>The math is direction-agnostic: at any value of <c>p</c> the
    /// formula yields the same coverage regardless of whether the user is
    /// flicking forward or backward through that point.</para>
    /// <para>XAML's layout pass clobbers the hand-out visual's <c>Offset</c>
    /// on every measure, so the Composition <c>Translation</c> channel is
    /// what we drive. <c>Scale</c> is app-managed by default and isn't
    /// reset by layout. Both require the element opted into Translation
    /// via <see cref="ElementCompositionPreview.SetIsTranslationEnabled"/>.</para>
    /// </remarks>
    private void EnsurePipExpressionWired()
    {
        if (_pipExpressionWired) return;
        if (_pipIndicator is null || _interaction is null || _compositor is null) return;

        ElementCompositionPreview.SetIsTranslationEnabled(_pipIndicator, true);
        var visual = ElementCompositionPreview.GetElementVisual(_pipIndicator);
        // Scale anchors at the pill's left edge so Scale.X expands rightward
        // — that's what makes the right edge "lead" during the stretch phase.
        visual.CenterPoint = new Vector3(0, 0, 0);

        // p     = tracker.Position.X / shared.StepX  (continuous slide index)
        // frac  = p - Floor(p)
        // left  = Floor(p) * w  +  Clamp(2*frac - 1, 0, 1) * w
        var translation = _compositor.CreateExpressionAnimation(
            "Vector3(" +
            "  Floor(tracker.Position.X / shared.StepX) * pipSlotWidth" +
            "  + Clamp(2 * (tracker.Position.X / shared.StepX - Floor(tracker.Position.X / shared.StepX)) - 1, 0, 1) * pipSlotWidth," +
            "  0, 0)");
        translation.SetReferenceParameter("tracker", _interaction.Tracker);
        translation.SetReferenceParameter("shared", _interaction.SharedPropertySet);
        translation.SetScalarParameter("pipSlotWidth", (float)PipSlotWidth);
        visual.StartAnimation("Translation", translation);

        // scaleX = 1 + Clamp(2*frac, 0, 1) - Clamp(2*frac - 1, 0, 1)
        //        = triangle: 1 at frac=0, 2 at frac=0.5, 1 at frac=1.
        var scale = _compositor.CreateExpressionAnimation(
            "Vector3(" +
            "  1" +
            "  + Clamp(2 * (tracker.Position.X / shared.StepX - Floor(tracker.Position.X / shared.StepX)), 0, 1)" +
            "  - Clamp(2 * (tracker.Position.X / shared.StepX - Floor(tracker.Position.X / shared.StepX)) - 1, 0, 1)," +
            "  1, 1)");
        scale.SetReferenceParameter("tracker", _interaction.Tracker);
        scale.SetReferenceParameter("shared", _interaction.SharedPropertySet);
        visual.StartAnimation("Scale", scale);

        _pipExpressionWired = true;
    }

    private void OnPipsSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        // Skip our own programmatic writes — those come through
        // SyncPagerToSelection under the guard.
        if (_updatingPips || _interaction is null) return;
        var pivot = _interaction.PendingSlide;
        if (sender.SelectedPageIndex == pivot) return;
        var delta = sender.SelectedPageIndex - pivot;
        GoToOffset(delta);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_slideHost is null) return;

        _compositor = ElementCompositionPreview.GetElementVisual(_slideHost).Compositor;

        _stageRoot = _compositor.CreateContainerVisual();
        _stageRoot.RelativeSizeAdjustment = Vector2.One;
        _stageRoot.Clip = _compositor.CreateInsetClip(0, 0, 0, 0);
        ElementCompositionPreview.SetElementChildVisual(_slideHost, _stageRoot);

        // Off-frame XAML overlays (text) need the same hard clipping the
        // composition stage gets, otherwise scaled-down + offset overlays
        // would render past the carousel's bounds onto adjacent UI.
        if (_overlayHost is not null)
        {
            var overlayHostVisual = ElementCompositionPreview.GetElementVisual(_overlayHost);
            overlayHostVisual.Clip = _compositor.CreateInsetClip(0, 0, 0, 0);
        }

        _interaction = new HeroInteraction(_compositor, _stageRoot);
        _interaction.IdleEntered += OnTrackerIdle;
        _interaction.PositionChanged += OnInteractionPositionChanged;

        _surfaceCache = new BakedSurfaceCache(_compositor);

        RebuildSlides();
        UpdateAutoplay();

        // Seed the step AFTER the next layout pass — at Loaded time the
        // templated child Grid often hasn't been arranged yet so its
        // ActualWidth is 0. Defer the SetStep so the per-slide expression
        // animations pick up the carousel's real width on the first frame.
        DispatcherQueue.TryEnqueue(SeedStepFromLayout);
    }

    private void SeedStepFromLayout()
    {
        if (_interaction is null || _slideHost is null) return;
        var w = (float)_slideHost.ActualWidth;
        if (w > 0)
        {
            _interaction.SetStep(w);
            InvalidateBackdrops();
        }
        else
        {
            // Layout still hasn't produced a width — try again next tick.
            DispatcherQueue.TryEnqueue(SeedStepFromLayout);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pips is not null)
        {
            _pips.SelectedIndexChanged -= OnPipsSelectedIndexChanged;
        }
        // Composition cleans up the animation when the visual is GC'd, but
        // we need to re-wire it on a future Loaded — `_interaction` is
        // about to be disposed below.
        _pipExpressionWired = false;
        _autoplayTimer?.Stop();
        _autoplayTimer = null;
        _bakeCts?.Cancel();
        _bakeCts?.Dispose();
        _bakeCts = null;

        foreach (var slide in _slides) slide.Dispose();
        _slides.Clear();

        if (_interaction is not null)
        {
            _interaction.IdleEntered -= OnTrackerIdle;
            _interaction.PositionChanged -= OnInteractionPositionChanged;
            _interaction.Dispose();
            _interaction = null;
        }
        _surfaceCache?.Dispose();
        _surfaceCache = null;

        if (_slideHost is not null)
            ElementCompositionPreview.SetElementChildVisual(_slideHost, null);

        _stageRoot?.Dispose();
        _stageRoot = null;
        _compositor = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStepFromHostSize();
    }

    private void OnSlideHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStepFromHostSize();
    }

    private void UpdateStepFromHostSize()
    {
        if (_interaction is null || _slideHost is null) return;
        // Use the actual slide-host width (carousel minus borders/insets) —
        // it's what the slide visuals actually have available.
        var step = (float)_slideHost.ActualWidth;
        if (step <= 0) return;
        _interaction.SetStep(step);
        InvalidateBackdrops();
        UpdateResponsiveTypography(step);
    }

    /// <summary>
    /// Scales the title <see cref="TextBlock.FontSize"/> across every
    /// overlay so titles track the carousel's actual width — CSS
    /// <c>clamp(28px, 5vw, 50px)</c>. Called from
    /// <see cref="UpdateStepFromHostSize"/> so it runs on every layout
    /// pass that changes the slide host's width.
    /// </summary>
    private void UpdateResponsiveTypography(float slideWidth)
    {
        if (_titleBlocks.Count == 0) return;
        var fontSize = Math.Clamp(slideWidth * 0.05, 28.0, 50.0);
        foreach (var t in _titleBlocks)
            t.FontSize = fontSize;
    }

    // ─── Slide construction ──────────────────────────────────────────────

    private void RebuildSlides()
    {
        if (_compositor is null || _stageRoot is null || _interaction is null || _surfaceCache is null) return;

        foreach (var slide in _slides) slide.Dispose();
        _slides.Clear();
        _stageRoot.Children.RemoveAll();
        _overlayHost?.Children.Clear();
        _overlays.Clear();
        _titleBlocks.Clear();
        _ctaButtons.Clear();

        var items = ItemsSource;
        if (items is null || items.Count == 0)
        {
            _interaction.SetItemCount(1);
            return;
        }

        _interaction.SetItemCount(items.Count);

        // Halo lerps across these — same accent the bake uses, so the
        // halo's hue tracks the bg the user actually sees on screen.
        _slideAccents = new Windows.UI.Color[items.Count];
        for (int j = 0; j < items.Count; j++)
            _slideAccents[j] = items[j].Accent;

        // The hand-out visual of the XAML slide host has its Size property
        // set by layout — that's the only visual in the chain whose Size is
        // expression-readable as a non-zero number, so the cover sizing
        // expressions reference it directly.
        var hostVisual = ElementCompositionPreview.GetElementVisual(_slideHost!);

        for (int i = 0; i < items.Count; i++)
        {
            var slide = new HeroSlideVisual(_compositor, i, _interaction, hostVisual);
            _stageRoot.Children.InsertAtTop(slide.Root);
            _slides.Add(slide);

            // Slides flagged UseImageAsBackground hide their right-anchored
            // cover thumbnail group — the image is already baked into the
            // slide backdrop, so a duplicate thumbnail would compete with
            // it. The bake-time choice is wired into BakeAllAsync below;
            // this is the layout-side counterpart.
            if (items[i].UseImageAsBackground)
            {
                slide.SetCoverVisible(false);
            }

            // Cover image — slide owns the LoadedImageSurface lifetime so
            // the async load isn't interrupted by GC (matches the SideCard
            // pattern that's already working).
            var item = items[i];
            if (item.ImageUri is not null)
            {
                slide.LoadCoverImage(item.ImageUri);

                // Soft accent halo behind the cover.
                var glow = _compositor.CreateRadialGradientBrush();
                glow.MappingMode = CompositionMappingMode.Relative;
                glow.EllipseCenter = new System.Numerics.Vector2(0.5f, 0.5f);
                glow.EllipseRadius = new System.Numerics.Vector2(0.5f, 0.5f);
                var s0 = _compositor.CreateColorGradientStop();
                s0.Offset = 0f;
                s0.Color = Windows.UI.Color.FromArgb(180, item.Accent.R, item.Accent.G, item.Accent.B);
                var s1 = _compositor.CreateColorGradientStop();
                s1.Offset = 1f;
                s1.Color = Windows.UI.Color.FromArgb(0, item.Accent.R, item.Accent.G, item.Accent.B);
                glow.ColorStops.Add(s0);
                glow.ColorStops.Add(s1);
                slide.SetCoverGlow(glow);
            }

            if (_overlayHost is not null)
            {
                var (overlayElement, coverShimmer, titleBlock, primaryCta, secondaryCta) = OverlayBuilder.Create(
                    items[i],
                    PrimaryCtaButtonStyle,
                    SecondaryCtaButtonStyle);
                overlayElement.Opacity = 1;
                _overlayHost.Children.Add(overlayElement);
                _overlays.Add(overlayElement);
                _titleBlocks.Add(titleBlock);
                _ctaButtons.Add((primaryCta, secondaryCta));

                // Cross-fade the Shimmer out once the Composition cover's
                // LoadedImageSurface arrives. The slide's LoadCompleted has
                // already done a Task.Yield to give the cover a paint pass
                // before we get here, so the fade reveals an already-painted
                // cover rather than an empty slot.
                slide.CoverLoadCompleted += (_, success) =>
                {
                    if (!success) return;
                    DispatcherQueue.TryEnqueue(() => FadeOutShimmer(coverShimmer));
                };
            }
        }

        InvalidateBackdrops();
        SyncPagerToSelection(items.Count, SelectedIndex);
        RefreshAccentFromCurrentPosition();

        // Defer expression-animation attachment until the XAML overlays have a
        // size — otherwise CenterPoint(0, h/2, 0) reads 0 and the pivot snaps
        // to (0, 0). One DispatcherQueue cycle is enough for measure/arrange.
        DispatcherQueue.TryEnqueue(AttachOverlayAnimations);
    }

    /// <summary>
    /// Per-frame accent refresh driven by
    /// <see cref="HeroInteraction.PositionChanged"/> — RGB-lerps between
    /// the slides on either side of the continuous tracker position and
    /// publishes the result to <see cref="CurrentAccent"/>. Consumers
    /// (typically <see cref="HeroHalo"/>'s attached property) bind to
    /// that DP for ambient effects that need to track the scrub rather
    /// than wait for <see cref="SelectedIndex"/> to settle.
    /// </summary>
    private void OnInteractionPositionChanged(float progress)
    {
        ApplyHaloColor(progress);
    }

    /// <summary>
    /// Reads the tracker's current position and publishes the lerped
    /// accent to <see cref="CurrentAccent"/>. Used for non-tick refreshes
    /// (after a successful accent
    /// extraction lands, or after <c>RebuildSlides</c>) where the tracker
    /// hasn't moved but the inputs to the lerp have.
    /// </summary>
    private void RefreshAccentFromCurrentPosition()
    {
        if (_interaction is null) return;
        var status = _interaction.SharedPropertySet.TryGetScalar("StepX", out var step);
        if (status != CompositionGetValueStatus.Succeeded || step <= 0) return;
        ApplyHaloColor(_interaction.Tracker.Position.X / step);
    }

    private void ApplyHaloColor(float progress)
    {
        if (_slideAccents is null || _slideAccents.Length == 0) return;

        var floor = (int)Math.Floor(progress);
        var frac = Math.Clamp(progress - floor, 0f, 1f);
        var iA = Math.Clamp(floor, 0, _slideAccents.Length - 1);
        var iB = Math.Clamp(floor + 1, 0, _slideAccents.Length - 1);
        var ca = _slideAccents[iA];
        var cb = _slideAccents[iB];
        var lerped = LerpRgb(ca, cb, frac);
        // Publish at full alpha — HeroHalo (or any other consumer) is
        // responsible for applying its own intensity multiplier to the
        // halo's render. We just publish the *colour*, not the appearance.
        CurrentAccent = Windows.UI.Color.FromArgb(255, lerped.R, lerped.G, lerped.B);
    }

    private static Windows.UI.Color LerpRgb(Windows.UI.Color a, Windows.UI.Color b, float t) =>
        Windows.UI.Color.FromArgb(
            255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Fade the Shimmer placeholder to opacity 0 over 280 ms with a cubic
    /// ease-out, then turn off its animation. We *don't* set
    /// <c>Visibility=Collapsed</c> — the Shimmer's <c>Width</c> is what
    /// reserves the layout slot for the cover column, and collapsing it
    /// would let the Auto-sized column shrink to 0, reflowing the text
    /// underneath the Composition cover image.
    /// </summary>
    private static void FadeOutShimmer(Shimmer shimmer)
    {
        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, shimmer);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) => shimmer.IsActive = false;
        sb.Begin();
    }

    private void AttachOverlayAnimations()
    {
        if (_compositor is null || _interaction is null) return;
        var shared = _interaction.SharedPropertySet;
        var tracker = _interaction.Tracker;

        for (int i = 0; i < _overlays.Count && i < _slides.Count; i++)
        {
            var element = _overlays[i];
            // CRITICAL: XAML *manages* the hand-out visual's Offset and Size
            // properties — every layout pass resets them, which is why a
            // direct `StartAnimation("Offset.X", expression)` silently does
            // nothing and every slide's text ends up stacked at x=0. The
            // app-managed alternative is the Translation property, which
            // must be opted into explicitly.
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var item = _slides[i].ItemPropertySet;

            // Translation rides on top of XAML layout, so the slide-offset
            // expression (power-curve x = sign·|off|^1.45·step) actually
            // reaches the visual.
            visual.StartAnimation("Translation.X",
                HeroAnimations.BuildSlideOffsetX(_compositor, tracker, shared, item));

            // Scale + CenterPoint *are* app-managed on hand-out visuals —
            // these animate without being clobbered by layout.
            visual.StartAnimation("Scale",
                HeroAnimations.BuildContentScale(_compositor, tracker, shared, item));
            visual.StartAnimation("CenterPoint",
                HeroAnimations.BuildLeftMidCenterPoint(_compositor));

            // Match the React design: the overlay never fades; it only
            // scales from the left-mid pivot. Off-frame slides are pushed
            // out of view by Translation.X and clipped by PART_OverlayHost.
            visual.Opacity = 1f;
        }
    }

    private void InvalidateBackdrops()
    {
        if (_compositor is null || _surfaceCache is null || _slideHost is null) return;
        _bakeCts?.Cancel();
        _bakeCts = new CancellationTokenSource();
        _ = BakeAllAsync(_bakeCts.Token);
    }

    private async Task BakeAllAsync(CancellationToken ct)
    {
        var items = ItemsSource;
        if (items is null || _surfaceCache is null) return;

        var width = (int)Math.Max(64, _slideHost!.ActualWidth);
        var height = (int)Math.Max(64, _slideHost.ActualHeight);
        var pixelSize = new SizeInt32 { Width = width, Height = height };
        var isDark = ActualTheme == ElementTheme.Dark;

        for (int i = 0; i < items.Count && i < _slides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            if (item.ImageUri is null) continue;

            try
            {
                var brush = await _surfaceCache.GetBrushAsync(
                    item.ImageUri,
                    item.Accent,
                    pixelSize,
                    isDark,
                    item.UseImageAsBackground,
                    ct);
                if (brush is not null && i < _slides.Count)
                {
                    _slides[i].SetBackdrop(brush);
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* keep fallback */ }

            // Extract dominant accent and re-theme this slide's CTA
            // buttons. The buttons were initially built with the seed
            // accent (synchronous, so they render immediately); once the
            // image-derived dominant lands, we swap in the more vibrant
            // colour. If extraction returns the neutral DefaultAccent
            // sentinel we leave the seed accent in place.
            try
            {
                var extracted = await _surfaceCache.GetExtractedAccentAsync(item.ImageUri, ct);
                if (extracted != BakedSurfaceCache.DefaultAccent)
                {
                    // Re-theme this slide's CTA buttons to the image-dominant
                    // accent (overrides the seed accent the buttons were
                    // initially built with).
                    if (i < _ctaButtons.Count)
                    {
                        var (primary, secondary) = _ctaButtons[i];
                        if (primary is not null)
                            OverlayBuilder.RethemeCtaButton(primary, extracted, primary: true);
                        if (secondary is not null)
                            OverlayBuilder.RethemeCtaButton(secondary, extracted, primary: false);
                    }

                    // For slides that use the cover as their bg, the halo
                    // should also follow the image-dominant accent — the
                    // seed accent is hand-curated and may not match the
                    // image's actual dominant colour (e.g., a slate-blue
                    // seed on a red cover gives a blue halo around a red
                    // slide). Update the per-slide entry the halo lerps
                    // across, then refresh the published CurrentAccent.
                    if (item.UseImageAsBackground &&
                        _slideAccents is { } accents && i < accents.Length)
                    {
                        accents[i] = extracted;
                        RefreshAccentFromCurrentPosition();
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* keep seed accent */ }
        }
    }


    // ─── Input ───────────────────────────────────────────────────────────

    private void OnHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_interaction is null) return;
        var pp = e.GetCurrentPoint(_slideHost);
        _interaction.RedirectForManipulation(pp);
    }

    private void OnHostPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_interaction is null) return;
        var delta = e.GetCurrentPoint(_slideHost).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        _interaction.SetIntentDirection(delta < 0 ? +1 : -1);
        var step = (float)(_slideHost?.ActualWidth ?? 0);
        if (step <= 0) return;
        // Use the tracker's TARGET position rather than SelectedIndex —
        // SelectedIndex only updates on IdleStateEntered, so during an
        // in-flight transition it's still the *previous* slide.
        var current = _interaction.PendingSlide;
        var target = delta < 0 ? current + 1 : current - 1;
        var items = ItemsSource;
        if (items is null) return;
        target = Math.Clamp(target, 0, items.Count - 1);
        var easing = _compositor!.CreateCubicBezierEasingFunction(
            new Vector2(0.165f, 0.84f), new Vector2(0.44f, 1f));
        _interaction.GoTo(target, easing);
        RestartAutoplayCountdown();
    }

    private void OnHostKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                e.Handled = true; GoToOffset(-1); break;
            case Windows.System.VirtualKey.Right:
                e.Handled = true; GoToOffset(+1); break;
        }
    }

    private void GoToOffset(int delta, bool restartAutoplay = true)
    {
        if (_interaction is null || _compositor is null) return;
        var items = ItemsSource;
        if (items is null || items.Count == 0) return;
        // Pivot off the tracker's pending target rather than SelectedIndex —
        // matters when autoplay or a user gesture fires during an in-flight
        // animation (SelectedIndex is still the previous slide until the
        // tracker enters Idle).
        var pivot = _interaction.PendingSlide;
        var target = Math.Clamp(pivot + delta, 0, items.Count - 1);
        _interaction.SetIntentDirection(Math.Sign(delta));
        var easing = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.165f, 0.84f), new Vector2(0.44f, 1f));
        _interaction.GoTo(target, easing);
        if (restartAutoplay) RestartAutoplayCountdown();
    }

    /// <summary>
    /// Bumps the autoplay countdown to start over from "now". Called from
    /// every user-initiated navigation path (wheel, keyboard, dot click,
    /// drag end) so the next autoplay tick can't fire immediately after a
    /// user action and override their intent.
    /// </summary>
    private void RestartAutoplayCountdown()
    {
        if (_autoplayTimer is null) return;
        _autoplayTimer.Stop();
        if (Autoplay) _autoplayTimer.Start();
    }

    private void OnTrackerIdle()
    {
        if (_interaction is null) return;
        // After a drag/flick settles, give the user the same autoplay grace
        // period as a click would — otherwise the tick can fire immediately
        // after a settle and feel like the carousel "kicks" forward.
        RestartAutoplayCountdown();
        var idx = _interaction.CurrentSlide;
        var items = ItemsSource;
        if (items is null) return;
        idx = Math.Clamp(idx, 0, items.Count - 1);
        if (idx != SelectedIndex)
        {
            SelectedIndex = idx;
            SelectedIndexChanged?.Invoke(this, idx);
        }
        SyncPagerToSelection(items.Count, idx);
        // Halo is already in sync — every PositionChanged tick during the
        // settle has been lerping it. No extra refresh needed here.
    }

    private void OnSelectedIndexExternallyChanged(int value)
    {
        _interaction?.SnapToIndex(value);
        var count = ItemsSource?.Count ?? 0;
        if (count > 0) SyncPagerToSelection(count, value);
    }

    // ─── Autoplay ────────────────────────────────────────────────────────

    private void UpdateAutoplay()
    {
        _autoplayTimer?.Stop();
        if (!Autoplay) return;
        _autoplayTimer = new DispatcherTimer { Interval = AutoplayInterval };
        // Pass restartAutoplay: false so an autoplay-triggered advance
        // doesn't reset its own countdown (which would just amount to
        // running the timer with the same interval).
        _autoplayTimer.Tick += (_, _) => GoToOffset(+1, restartAutoplay: false);
        _autoplayTimer.Start();
    }
}
