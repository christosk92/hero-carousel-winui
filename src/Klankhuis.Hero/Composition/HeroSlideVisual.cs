using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace Klankhuis.Hero.Composition;

/// <summary>
/// One slide's Composition tree:
/// <code>
/// root (ContainerVisual)            ← .Offset.X driven by slide expression
/// ├─ bg     (SpriteVisual)          ← baked backdrop brush
/// ├─ bgArt  (SpriteVisual)          ← parallax-shifted layer of same surface
/// ├─ content (ContainerVisual)      ← scaled from left-mid pivot
/// └─ cover  (ContainerVisual)
///    ├─ glow (SpriteVisual)
///    └─ image (SpriteVisual)
/// </code>
/// </summary>
internal sealed class HeroSlideVisual : IDisposable
{
    public ContainerVisual Root { get; }
    public SpriteVisual Background { get; }
    public SpriteVisual BackgroundArt { get; }
    public ContainerVisual Content { get; }
    public ContainerVisual Cover { get; }
    public SpriteVisual CoverGlow { get; }
    public SpriteVisual CoverImage { get; }
    public CompositionPropertySet ItemPropertySet { get; }

    private readonly Compositor _compositor;
    /// <summary>
    /// Mask brush wrapping the cover image so it renders rounded without
    /// needing a <see cref="CompositionGeometricClip"/> on the visual — a
    /// clip would also clip the drop shadow's render extent, eating the
    /// shadow halo. Cover image is assigned to <c>_coverImageMask.Source</c>
    /// in <see cref="LoadCoverImage"/>; the rounded shape lives in
    /// <c>_coverImageMask.Mask</c>.
    /// </summary>
    private readonly CompositionMaskBrush _coverImageMask;
    private LoadedImageSurface? _coverSurface;
    private bool _disposed;

    /// <summary>
    /// Raised on the UI thread when the cover image's <see cref="LoadedImageSurface"/>
    /// finishes loading. The bool is <see langword="true"/> on success and
    /// <see langword="false"/> on any failure (network error, missing
    /// internetClient capability, decode failure, …). Consumers use this to
    /// collapse the Shimmer placeholder.
    /// </summary>
    public event EventHandler<bool>? CoverLoadCompleted;

    public HeroSlideVisual(Compositor compositor, int index, HeroInteraction interaction, Visual hostVisual, float intensity = 1f)
    {
        _compositor = compositor;
        Root = compositor.CreateContainerVisual();
        Root.RelativeSizeAdjustment = Vector2.One;
        // Match the React `.slide { overflow: hidden }` — without this, the
        // background's 1.08× breathing scale leaks past the slide's own
        // bounds and an adjacent slide's bg appears as a peek on the active
        // slide's edge. Clip each slide root to its own bounds.
        Root.Clip = compositor.CreateInsetClip(0, 0, 0, 0);

        Background = compositor.CreateSpriteVisual();
        Background.RelativeSizeAdjustment = Vector2.One;

        BackgroundArt = compositor.CreateSpriteVisual();
        BackgroundArt.RelativeSizeAdjustment = Vector2.One;

        Content = compositor.CreateContainerVisual();
        Content.RelativeSizeAdjustment = Vector2.One;

        // The cover layer is anchored to the right side of the slide,
        // vertically centred. We size it as a square = 70% of slide height,
        // pinned to the right with a 56-px gutter. We reference the SLIDE
        // HOST's hand-out visual rather than Cover, because Composition
        // expressions read the literal `Size` property (defaults to (0,0))
        // — `RelativeSizeAdjustment` only affects rendering, not the
        // expression-visible Size. Only the XAML hand-out visual has its
        // Size property set by layout.
        Cover = compositor.CreateContainerVisual();
        Cover.RelativeSizeAdjustment = Vector2.One;

        // Sizing: 0.55 of slide height ≈ 253 px on a 460-tall slide, matching
        // the React `width: min(280px, 100%)` clamp. The glow halo extends
        // ~36% past the cover for a soft fall-off.
        const float CoverFraction = 0.55f;
        const float GlowFraction = CoverFraction * 1.45f;
        const float CoverGutterRight = 56f;
        const float CornerRadius = 12f;

        CoverGlow = compositor.CreateSpriteVisual();
        var glowSize = compositor.CreateExpressionAnimation(
            $"Vector2(host.Size.Y * {GlowFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"host.Size.Y * {GlowFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
        glowSize.SetReferenceParameter("host", hostVisual);
        CoverGlow.StartAnimation("Size", glowSize);
        var glowOffset = compositor.CreateExpressionAnimation(
            $"Vector3(host.Size.X - host.Size.Y * {GlowFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} - {(CoverGutterRight - 32):0}, " +
            $"(host.Size.Y - host.Size.Y * {GlowFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}) / 2, 0)");
        glowOffset.SetReferenceParameter("host", hostVisual);
        CoverGlow.StartAnimation("Offset", glowOffset);

        // ─── Rounded mask (shared by cover image + drop shadow) ──────────
        // A CompositionVisualSurface captures a ShapeVisual containing a
        // single white-filled rounded rectangle into a SurfaceBrush. Two
        // consumers reference the same brush:
        //   1. CoverImage's CompositionMaskBrush.Mask — gives the image
        //      rounded corners *without* needing a geometric clip on the
        //      visual (a clip would also clip the shadow render extent).
        //   2. The drop-shadow visual's Shadow.Mask — gives the shadow a
        //      rounded shape.
        // Mask source size is fixed at 256×256 with corner radius 12; the
        // surface stretches to consumer size, so a 253-px cover renders the
        // corner at 12 × (253/256) ≈ 11.86 px — visually identical to 12.
        const int MaskSize = 256;
        var maskShape = compositor.CreateShapeVisual();
        maskShape.Size = new Vector2(MaskSize, MaskSize);
        var maskGeometry = compositor.CreateRoundedRectangleGeometry();
        maskGeometry.Size = new Vector2(MaskSize, MaskSize);
        maskGeometry.CornerRadius = new Vector2(CornerRadius, CornerRadius);
        var maskShapeFill = compositor.CreateSpriteShape(maskGeometry);
        maskShapeFill.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
        maskShape.Shapes.Add(maskShapeFill);

        var maskVisualSurface = compositor.CreateVisualSurface();
        maskVisualSurface.SourceVisual = maskShape;
        maskVisualSurface.SourceSize = new Vector2(MaskSize, MaskSize);
        var roundedMaskBrush = compositor.CreateSurfaceBrush(maskVisualSurface);
        roundedMaskBrush.Stretch = CompositionStretch.Fill;

        // ─── Cover image visual ──────────────────────────────────────────
        CoverImage = compositor.CreateSpriteVisual();
        var coverSize = compositor.CreateExpressionAnimation(
            $"Vector2(host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
        coverSize.SetReferenceParameter("host", hostVisual);
        CoverImage.StartAnimation("Size", coverSize);
        var coverOffset = compositor.CreateExpressionAnimation(
            $"Vector3(host.Size.X - host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} - {CoverGutterRight:0}, " +
            $"(host.Size.Y - host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}) / 2, 0)");
        coverOffset.SetReferenceParameter("host", hostVisual);
        CoverImage.StartAnimation("Offset", coverOffset);

        // Rounded corners via mask brush — Source is set in LoadCoverImage
        // when the image surface arrives.
        _coverImageMask = compositor.CreateMaskBrush();
        _coverImageMask.Mask = roundedMaskBrush;
        CoverImage.Brush = _coverImageMask;

        // ─── Drop-shadow sibling visual ─────────────────────────────────
        // A separate SpriteVisual sized identically to CoverImage but
        // brushless — it exists only to host the DropShadow. Without its
        // own clip, the shadow renders unrestricted (extends past the
        // cover's rectangular bounds), and the rounded mask gives it the
        // pill-corner shape.
        //
        // CSS reference: `box-shadow: 0 18px 36px rgba(0, 0, 0, 0.65)` —
        // the offset pushes the shadow under the card; BlurRadius creates
        // the soft fall-off; alpha is bumped vs the previous geometric-
        // clipped attempt because that one was eating most of the shadow
        // alpha at the visual's bottom edge.
        var coverShadowHost = compositor.CreateSpriteVisual();
        var shadowSize = compositor.CreateExpressionAnimation(
            $"Vector2(host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
        shadowSize.SetReferenceParameter("host", hostVisual);
        coverShadowHost.StartAnimation("Size", shadowSize);
        var shadowOffset = compositor.CreateExpressionAnimation(
            $"Vector3(host.Size.X - host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} - {CoverGutterRight:0}, " +
            $"(host.Size.Y - host.Size.Y * {CoverFraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}) / 2, 0)");
        shadowOffset.SetReferenceParameter("host", hostVisual);
        coverShadowHost.StartAnimation("Offset", shadowOffset);

        var coverShadow = compositor.CreateDropShadow();
        coverShadow.BlurRadius = 28f;
        coverShadow.Offset = new Vector3(0, 18, 0);
        coverShadow.Color = Windows.UI.Color.FromArgb(180, 0, 0, 0);
        coverShadow.Mask = roundedMaskBrush;
        coverShadowHost.Shadow = coverShadow;

        // Tree — children are listed back-to-front via successive
        // InsertAtTop calls (each moves the previous topmost down one).
        Root.Children.InsertAtTop(Background);
        Root.Children.InsertAtTop(BackgroundArt);
        Cover.Children.InsertAtTop(CoverGlow);          // bottom of cover stack
        Cover.Children.InsertAtTop(coverShadowHost);    // shadow sits between glow and image
        Cover.Children.InsertAtTop(CoverImage);         // top of cover stack
        Root.Children.InsertAtTop(Content);
        Root.Children.InsertAtTop(Cover);

        // Per-slide property set holding the index — referenced by the
        // shared expressions in HeroAnimations.
        ItemPropertySet = compositor.CreatePropertySet();
        ItemPropertySet.InsertScalar("Index", index);

        // Wire the expression animations.
        var shared = interaction.SharedPropertySet;
        var tracker = interaction.Tracker;

        Root.StartAnimation("Offset.X",
            HeroAnimations.BuildSlideOffsetX(compositor, tracker, shared, ItemPropertySet));

        Root.StartAnimation("Offset.Z",
            HeroAnimations.BuildSlideZIndex(compositor, tracker, shared, ItemPropertySet));

        Background.StartAnimation("Scale",
            HeroAnimations.BuildBgScale(compositor, tracker, shared, ItemPropertySet));
        // Pure Composition visuals: Visual.Size stays at (0,0) regardless of
        // RelativeSizeAdjustment, so this.Target.Size returns (0,0) and the
        // CenterPoint snaps to the top-left. Reference hostVisual.Size
        // (managed by XAML layout) instead so scales pivot from the visual
        // centre, exactly matching the React `transform-origin: center`.
        Background.StartAnimation("CenterPoint",
            HeroAnimations.BuildCenterPoint(compositor, hostVisual));

        BackgroundArt.StartAnimation("Offset.X",
            HeroAnimations.BuildBgArtOffsetX(compositor, tracker, shared, ItemPropertySet));

        // Content scales from the left-edge / vertical-centre pivot —
        // exactly the React `transform-origin: 0 50%`.
        Content.StartAnimation("Scale",
            HeroAnimations.BuildContentScale(compositor, tracker, shared, ItemPropertySet, intensity));
        Content.StartAnimation("CenterPoint",
            HeroAnimations.BuildLeftMidCenterPoint(compositor, hostVisual));

        Cover.StartAnimation("Scale",
            HeroAnimations.BuildCoverScale(compositor, tracker, shared, ItemPropertySet, intensity));
        Cover.StartAnimation("CenterPoint",
            HeroAnimations.BuildCenterPoint(compositor, hostVisual));

        CoverGlow.StartAnimation("Opacity",
            HeroAnimations.BuildGlowOpacity(compositor, tracker, shared, ItemPropertySet));
    }

    /// <summary>Apply the baked backdrop brush as the bg + bgArt brush.</summary>
    public void SetBackdrop(CompositionSurfaceBrush brush)
    {
        Background.Brush = brush;
        BackgroundArt.Brush = brush;
    }

    /// <summary>Apply the cover image brush as the masked source.</summary>
    public void SetCoverImage(CompositionBrush? brush)
    {
        _coverImageMask.Source = brush;
    }

    /// <summary>
    /// Load the cover image from a URI directly. <see cref="LoadedImageSurface"/>
    /// has to be kept alive for the async load to complete — without a strong
    /// reference held by the slide, the GC can collect the surface before its
    /// pixels arrive (the <see cref="CompositionSurfaceBrush"/>'s Surface
    /// reference is not enough on its own). The <see cref="SideCard"/> control
    /// works around this by holding the surface in a field; we mirror that here.
    /// </summary>
    /// <remarks>
    /// We assign a solid neutral fallback brush *first* so the
    /// <see cref="CoverImage"/> SpriteVisual is visible before the async load
    /// completes — if the image load silently fails (CORS, missing
    /// internetClient capability, network error, …) the slot is still drawn,
    /// which makes the failure mode obvious instead of looking identical to a
    /// layout bug. The real surface brush replaces the fallback once
    /// <c>LoadCompleted</c> fires with <c>Success</c>.
    /// </remarks>
    public void LoadCoverImage(Uri imageUri)
    {
        _coverSurface?.Dispose();

        // No fallback source — the XAML Shimmer placeholder sitting on top
        // of the CoverImage handles the loading state. The cover-image
        // mask brush has its Source unset until the surface arrives; the
        // Shimmer collapses on success via the CoverLoadCompleted event.
        _coverImageMask.Source = null;

        _coverSurface = LoadedImageSurface.StartLoadFromUri(imageUri);
        _coverSurface.LoadCompleted += async (s, e) =>
        {
            if (_disposed) return;
            var success = e.Status == LoadedImageSourceLoadStatus.Success;
            if (success)
            {
                var brush = _compositor.CreateSurfaceBrush(s);
                brush.Stretch = CompositionStretch.UniformToFill;
                _coverImageMask.Source = brush;
            }

            // Yield so the compositor has a tick to paint the cover before
            // any consumer (e.g. the Shimmer fade in HeroCarousel) starts
            // its handoff transition. Without the yield, the Shimmer can
            // begin fading on the same frame the brush is assigned, which
            // produces a flash of the (still-empty) Composition slot.
            await Task.Yield();
            if (_disposed) return;

            CoverLoadCompleted?.Invoke(this, success);
        };
    }

    /// <summary>Apply a radial glow brush behind the cover.</summary>
    public void SetCoverGlow(CompositionBrush? brush)
    {
        CoverGlow.Brush = brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coverSurface?.Dispose();
        _coverSurface = null;
        ItemPropertySet.Dispose();
        Root.Dispose();
    }
}
