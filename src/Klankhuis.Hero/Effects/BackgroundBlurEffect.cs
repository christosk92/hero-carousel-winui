using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace Klankhuis.Hero.Effects;

/// <summary>
/// The "background blur" material used for hero slide backdrops, modelled
/// after the Microsoft Store's app-card effect graph (ComputeSharp paper §4.1):
///
/// <code>
/// Source → Transform2D (center) → GaussianBlur → SaturationEffect
///        → Blend(Overlay)  with AccentTint
///        → Blend(SoftLight) with NoiseShader(@~5% alpha)
///        → Border(clamp)  → Output
/// </code>
///
/// The graph is realized once per (source, accent, output-size) tuple by
/// <see cref="Surfaces.BakedSurfaceCache"/> into a CompositionDrawingSurface
/// and is never used as a live brush, mirroring the MS Store's strategy.
/// </summary>
public sealed partial class BackgroundBlurEffect : CanvasEffect
{
    private readonly CanvasEffectNode<Transform2DEffect> CenteredNode = new();
    private readonly CanvasEffectNode<GaussianBlurEffect> BlurredNode = new();
    private readonly CanvasEffectNode<ColorMatrixEffect> SaturatedNode = new();
    private readonly CanvasEffectNode<ColorSourceEffect> AccentNode = new();
    private readonly CanvasEffectNode<BlendEffect> AccentBlendNode = new();
    private readonly CanvasEffectNode<PixelShaderEffect<NoiseShader>> NoiseNode = new();
    private readonly CanvasEffectNode<BlendEffect> NoiseBlendNode = new();
    private readonly CanvasEffectNode<BorderEffect> ClampedNode = new();

    private ICanvasImage? _source;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private float _blurAmount = 40f;
    // 1.0 = identity. Higher values amplify small colour variations in the
    // blurred source and clamp to [0, 1] per channel — that's what produced
    // the rainbow-noise look. Keep at identity; rely on the accent overlay
    // for the colour wash instead.
    private float _saturation = 1.0f;
    private Windows.UI.Color _accent = Windows.UI.Color.FromArgb(255, 0x60, 0xCD, 0xFF);
    private float _accentAlpha = 0.30f;
    private float _noiseAlpha = 0.025f;
    private bool _isDarkTheme = true;

    /// <summary>The image to bake (typically a podcast cover).</summary>
    public ICanvasImage? Source
    {
        get => _source;
        set { if (_source != value) { _source = value; InvalidateEffectGraph(); } }
    }

    /// <summary>
    /// Affine transform applied to the source before the blur — used to centre
    /// the cover within the output rect, matching the MS Store paper's
    /// `Transform2DEffect` placement step.
    /// </summary>
    public Matrix3x2 SourceTransform
    {
        get => _transform;
        set { if (_transform != value) { _transform = value; InvalidateEffectGraph(); } }
    }

    /// <summary>Gaussian blur radius in DIPs. Default 60.</summary>
    public float BlurAmount
    {
        get => _blurAmount;
        set { if (_blurAmount != value) { _blurAmount = value; InvalidateEffectGraph(); } }
    }

    /// <summary>Saturation multiplier applied after the blur. Default 2.4.</summary>
    public float Saturation
    {
        get => _saturation;
        set { if (_saturation != value) { _saturation = value; InvalidateEffectGraph(); } }
    }

    /// <summary>Per-slide accent colour for the wash overlay.</summary>
    public Windows.UI.Color Accent
    {
        get => _accent;
        set { if (!_accent.Equals(value)) { _accent = value; InvalidateEffectGraph(); } }
    }

    /// <summary>Alpha of the accent overlay (overlay blend). 0–1, default 0.40.</summary>
    public float AccentAlpha
    {
        get => _accentAlpha;
        set { if (_accentAlpha != value) { _accentAlpha = value; InvalidateEffectGraph(); } }
    }

    /// <summary>Alpha of the procedural noise pass. 0–1, default 0.045.</summary>
    public float NoiseAlpha
    {
        get => _noiseAlpha;
        set { if (_noiseAlpha != value) { _noiseAlpha = value; InvalidateEffectGraph(); } }
    }

    /// <summary>
    /// True for dark-theme noise range (paper §4.1.1 — noise modulates RGB
    /// rather than alpha so it can be theme-aware). False inverts the range
    /// to a lighter band for light theme.
    /// </summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set { if (_isDarkTheme != value) { _isDarkTheme = value; InvalidateEffectGraph(); } }
    }

    protected override void BuildEffectGraph(CanvasEffectGraph graph)
    {
        graph.RegisterNode(CenteredNode, new Transform2DEffect());
        graph.RegisterNode(BlurredNode, new GaussianBlurEffect());
        graph.RegisterNode(SaturatedNode, new ColorMatrixEffect());
        graph.RegisterNode(AccentNode, new ColorSourceEffect());
        graph.RegisterNode(AccentBlendNode, new BlendEffect());
        graph.RegisterNode(NoiseNode, new PixelShaderEffect<NoiseShader>());
        graph.RegisterNode(NoiseBlendNode, new BlendEffect());
        graph.RegisterNode(ClampedNode, new BorderEffect());

        // Wire the static graph topology
        var blurred = graph.GetNode(BlurredNode);
        blurred.Source = graph.GetNode(CenteredNode);

        var saturated = graph.GetNode(SaturatedNode);
        saturated.Source = blurred;

        var accentBlend = graph.GetNode(AccentBlendNode);
        accentBlend.Background = saturated;
        accentBlend.Foreground = graph.GetNode(AccentNode);
        // SoftLight is a gentle tint — gives the slide an accent-coloured
        // haze without crushing the blurred colour structure into pure
        // saturation. Overlay was *much* more aggressive and amplified
        // small colour blobs into the rainbow look.
        accentBlend.Mode = BlendEffectMode.SoftLight;

        var noiseBlend = graph.GetNode(NoiseBlendNode);
        noiseBlend.Background = accentBlend;
        noiseBlend.Foreground = graph.GetNode(NoiseNode);
        noiseBlend.Mode = BlendEffectMode.SoftLight;

        var clamp = graph.GetNode(ClampedNode);
        clamp.Source = noiseBlend;
        clamp.ExtendX = CanvasEdgeBehavior.Clamp;
        clamp.ExtendY = CanvasEdgeBehavior.Clamp;

        graph.SetOutputNode(ClampedNode);
    }

    /// <summary>
    /// Builds a 5×4 colour matrix that scales saturation around the BT.709
    /// luminance axis. <paramref name="s"/> is in [0, 4]: 0 = grayscale,
    /// 1 = identity, &gt;1 = boosted. Win2D's <c>SaturationEffect</c> is
    /// hard-clamped to [0, 1], so we substitute this matrix to support the
    /// 2.4× boost the React reference design uses.
    /// </summary>
    private static Matrix5x4 SaturationMatrix(float s)
    {
        const float lr = 0.213f, lg = 0.715f, lb = 0.072f;
        var inv = 1f - s;
        return new Matrix5x4
        {
            M11 = lr * inv + s, M12 = lr * inv,     M13 = lr * inv,     M14 = 0,
            M21 = lg * inv,     M22 = lg * inv + s, M23 = lg * inv,     M24 = 0,
            M31 = lb * inv,     M32 = lb * inv,     M33 = lb * inv + s, M34 = 0,
            M41 = 0,            M42 = 0,            M43 = 0,            M44 = 1,
            M51 = 0,            M52 = 0,            M53 = 0,            M54 = 0,
        };
    }

    protected override void ConfigureEffectGraph(CanvasEffectGraph graph)
    {
        var centered = graph.GetNode(CenteredNode);
        centered.Source = _source;
        centered.TransformMatrix = _transform;

        graph.GetNode(BlurredNode).BlurAmount = _blurAmount;
        graph.GetNode(SaturatedNode).ColorMatrix = SaturationMatrix(_saturation);

        var tint = graph.GetNode(AccentNode);
        tint.Color = Windows.UI.Color.FromArgb(
            (byte)(_accentAlpha * 255f), _accent.R, _accent.G, _accent.B);

        var noise = graph.GetNode(NoiseNode);
        // Theme-aware noise range — paper §4.1.1
        var (min, max) = _isDarkTheme ? ((byte)0, (byte)255) : ((byte)128, (byte)255);
        noise.ConstantBuffer = new NoiseShader(
            (byte)Math.Round(Math.Clamp(_noiseAlpha, 0f, 1f) * 255f),
            min,
            max);
    }
}
