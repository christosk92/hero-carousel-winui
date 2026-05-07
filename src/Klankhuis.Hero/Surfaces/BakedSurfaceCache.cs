using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.Graphics.DirectX;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using ComputeSharp.D2D1.WinUI;
using Klankhuis.Hero.Effects;

namespace Klankhuis.Hero.Surfaces;

/// <summary>
/// Loads cover artwork, runs <see cref="BackgroundBlurEffect"/> over it once,
/// and parks the result in a <see cref="CompositionDrawingSurface"/>. The
/// resulting <see cref="CompositionSurfaceBrush"/> is cached so multiple
/// slides showing the same (uri, accent, size) tuple share one bitmap.
/// </summary>
/// <remarks>
/// This is the Microsoft Store paper's "render once, never as a live brush"
/// strategy — the effect graph runs at bake time and is never reattached to
/// the visual tree. Resize/DPI/theme/device-lost events all flow through
/// here to invalidate cache entries.
/// </remarks>
public sealed class BakedSurfaceCache : IDisposable
{
    private readonly Compositor _compositor;
    private readonly Lazy<CanvasDevice> _device;
    private readonly Lazy<CompositionGraphicsDevice> _graphicsDevice;
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    /// <summary>
    /// Per-URI cache of dominant-accent colors extracted from cover art —
    /// keyed by image URI alone (independent of the seed accent or pixel
    /// size used for the bake). Populated lazily by
    /// <see cref="GetExtractedAccentAsync"/> and used by the carousel to
    /// drive the per-slide CTA button tint without re-running extraction
    /// on every layout pass.
    /// </summary>
    private readonly Dictionary<Uri, Windows.UI.Color> _accentCache = new();
    private readonly object _gate = new();
    private int _maxEntries;
    private bool _disposed;

    public BakedSurfaceCache(Compositor compositor, int maxEntries = 32)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _maxEntries = Math.Max(4, maxEntries);
        _device = new Lazy<CanvasDevice>(() =>
        {
            var d = CanvasDevice.GetSharedDevice();
            d.DeviceLost += OnDeviceLost;
            return d;
        });
        _graphicsDevice = new Lazy<CompositionGraphicsDevice>(() =>
            CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _device.Value));
    }

    public int MaxEntries
    {
        get => _maxEntries;
        set
        {
            _maxEntries = Math.Max(4, value);
            EvictIfNeeded();
        }
    }

    /// <summary>
    /// Bakes (or returns the cached) backdrop surface for the given parameters.
    /// The returned brush is owned by the cache — do not dispose it.
    /// </summary>
    public async Task<CompositionSurfaceBrush?> GetBrushAsync(
        Uri imageUri,
        Windows.UI.Color accent,
        SizeInt32 pixelSize,
        bool isDarkTheme,
        CancellationToken ct = default)
    {
        if (_disposed) return null;
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;

        var key = new CacheKey(imageUri, accent, pixelSize, isDarkTheme);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                hit.Touched = Environment.TickCount64;
                return hit.Brush;
            }
        }

        // Load the source bitmap off the UI thread
        CanvasBitmap bitmap;
        try
        {
            var streamRef = RandomAccessStreamReference.CreateFromUri(imageUri);
            using var stream = await streamRef.OpenReadAsync().AsTask(ct);
            bitmap = await CanvasBitmap.LoadAsync(_device.Value, stream).AsTask(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        // Render to a CompositionDrawingSurface (the bridge type).
        var size = new Size(pixelSize.Width, pixelSize.Height);
        var surface = _graphicsDevice.Value.CreateDrawingSurface(
            size,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        BakeImperative(surface, bitmap, accent, pixelSize, isDarkTheme);

        // Extract dominant color from the same bitmap before disposing —
        // the alternative is a separate Stream + CanvasBitmap.LoadAsync
        // round trip per slide just to read the dominant. Result is
        // cached per-URI so subsequent GetExtractedAccentAsync calls are
        // a dictionary hit, not a load.
        try
        {
            var dominant = SampleAndExtract(bitmap);
            lock (_gate)
            {
                _accentCache[imageUri] = dominant;
            }
        }
        catch { /* leave _accentCache miss; GetExtractedAccentAsync will retry */ }

        bitmap.Dispose();

        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.UniformToFill;

        lock (_gate)
        {
            // Re-check (another caller might have raced and won)
            if (_cache.TryGetValue(key, out var raceWinner))
            {
                surface.Dispose();
                raceWinner.Touched = Environment.TickCount64;
                return raceWinner.Brush;
            }

            _cache[key] = new CacheEntry(brush, surface, Environment.TickCount64);
            EvictIfNeeded();
        }

        return brush;
    }

    /// <summary>
    /// Loads the cover art and returns its most-vibrant accent — derived
    /// by downsampling to 48 × 48 then HSV-bucketing the result (12
    /// 30°-wide hue bins, scoring each bin by
    /// <c>count × avgSat × avgVal</c>). Pixels with brightness &lt; 0.30
    /// or saturation &lt; 0.50 are excluded so dark mats and washed-out
    /// backgrounds don't dominate. The winning bucket's average RGB is
    /// returned.
    /// </summary>
    /// <remarks>
    /// Tuned for **CTA tinting**: we deliberately bias toward bright /
    /// saturated colours so the resulting button reads as vibrant accent,
    /// not muddy. The seed accent on <see cref="Composition.HeroSlideVisual"/>
    /// is hand-tuned per slide and may not match the cover's actual
    /// dominant; this extractor is what the carousel uses when the
    /// consumer wants the button to pick up the *image's* colour rather
    /// than the bake's tint.
    /// </remarks>
    public async Task<Windows.UI.Color> GetExtractedAccentAsync(
        Uri imageUri,
        CancellationToken ct = default)
    {
        if (_disposed) return DefaultAccent;

        lock (_gate)
        {
            if (_accentCache.TryGetValue(imageUri, out var cached)) return cached;
        }

        // Cache miss — fall through to a load. In practice this only
        // happens if the consumer asks for an accent before any bake has
        // run for that URI (the bake step in `GetBrushAsync` populates
        // `_accentCache` inline, sharing the bitmap it already loaded).
        CanvasBitmap bitmap;
        try
        {
            var streamRef = RandomAccessStreamReference.CreateFromUri(imageUri);
            using var stream = await streamRef.OpenReadAsync().AsTask(ct);
            bitmap = await CanvasBitmap.LoadAsync(_device.Value, stream).AsTask(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return DefaultAccent; }

        Windows.UI.Color extracted;
        try
        {
            extracted = SampleAndExtract(bitmap);
        }
        finally
        {
            bitmap.Dispose();
        }

        lock (_gate)
        {
            _accentCache[imageUri] = extracted;
        }
        return extracted;
    }

    /// <summary>
    /// Downsamples the source <paramref name="bitmap"/> to 48×48 and runs
    /// <see cref="ExtractDominantColor"/>. Shared by the standalone
    /// <see cref="GetExtractedAccentAsync"/> and the bake path (which
    /// already has the bitmap loaded and would otherwise have to re-fetch
    /// it just to compute the dominant).
    /// </summary>
    private Windows.UI.Color SampleAndExtract(CanvasBitmap bitmap)
    {
        const int SampleSize = 48;
        using var sample = new CanvasRenderTarget(_device.Value, SampleSize, SampleSize, 96);
        using (var ds = sample.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(bitmap, new Rect(0, 0, SampleSize, SampleSize));
        }
        return ExtractDominantColor(sample.GetPixelColors());
    }

    /// <summary>
    /// Sentinel returned when extraction fails or no pixels survive the
    /// HSV thresholds. Consumers should treat this as "no extracted
    /// accent" and fall back to their own default (e.g. the slide's seed
    /// accent).
    /// </summary>
    public static readonly Windows.UI.Color DefaultAccent =
        Windows.UI.Color.FromArgb(255, 128, 128, 128);

    private static Windows.UI.Color ExtractDominantColor(Windows.UI.Color[] pixels)
    {
        const int BucketCount = 12; // 30° hue slices
        Span<int> counts = stackalloc int[BucketCount];
        Span<int> rSum = stackalloc int[BucketCount];
        Span<int> gSum = stackalloc int[BucketCount];
        Span<int> bSum = stackalloc int[BucketCount];
        Span<float> satSum = stackalloc float[BucketCount];
        Span<float> valSum = stackalloc float[BucketCount];

        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var (hue, sat, val) = RgbToHsv(p.R, p.G, p.B);
            // Tighter thresholds than for halo extraction — we want
            // *vibrant*, not just "not-grey".
            if (val < 0.30f || sat < 0.50f) continue;
            int bucket = (int)(hue / 30f);
            if (bucket < 0) bucket = 0;
            if (bucket >= BucketCount) bucket = BucketCount - 1;
            counts[bucket]++;
            rSum[bucket] += p.R;
            gSum[bucket] += p.G;
            bSum[bucket] += p.B;
            satSum[bucket] += sat;
            valSum[bucket] += val;
        }

        int best = -1;
        float bestScore = 0f;
        for (int i = 0; i < BucketCount; i++)
        {
            if (counts[i] == 0) continue;
            var avgSat = satSum[i] / counts[i];
            var avgVal = valSum[i] / counts[i];
            var score = counts[i] * avgSat * avgVal;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0) return DefaultAccent;
        return Windows.UI.Color.FromArgb(
            255,
            (byte)(rSum[best] / counts[best]),
            (byte)(gSum[best] / counts[best]),
            (byte)(bSum[best] / counts[best]));
    }

    private static (float hue, float sat, float val) RgbToHsv(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float delta = max - min;
        float val = max;
        float sat = max == 0 ? 0 : delta / max;
        float hue = 0;
        if (delta > 0)
        {
            if (max == rf) hue = ((gf - bf) / delta) % 6f;
            else if (max == gf) hue = ((bf - rf) / delta) + 2f;
            else hue = ((rf - gf) / delta) + 4f;
            hue *= 60f;
            if (hue < 0) hue += 360f;
        }
        return (hue, sat, val);
    }

    /// <summary>
    /// Creates a reusable mask brush whose alpha shape is a rounded rectangle.
    /// Used by <see cref="Composition.HeroSlideVisual"/> as the
    /// <see cref="DropShadow.Mask"/> so the shadow follows the cover's
    /// rounded clip rather than its rectangular brush alpha. The brush
    /// stretches uniformly to whatever size the consuming visual is, so the
    /// underlying surface only needs to be rendered once.
    /// </summary>
    /// <param name="sizePx">Pixel size of the cached mask surface (square).
    /// 256 px is plenty — the brush stretches to the cover.</param>
    /// <param name="cornerRadius">Corner radius in pixels.</param>
    public CompositionSurfaceBrush CreateRoundedRectMaskBrush(int sizePx, float cornerRadius)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BakedSurfaceCache));
        var size = new Size(sizePx, sizePx);
        var surface = _graphicsDevice.Value.CreateDrawingSurface(
            size,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.FillRoundedRectangle(
                0, 0, sizePx, sizePx,
                cornerRadius, cornerRadius,
                Microsoft.UI.Colors.White);
        }

        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        return brush;
    }

    /// <summary>
    /// Invalidates the cache. Use on theme switch, DPI change, or device-lost
    /// recovery — the next <see cref="GetBrushAsync"/> call rebakes from scratch.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var entry in _cache.Values)
            {
                entry.Brush.Dispose();
                entry.Surface.Dispose();
            }
            _cache.Clear();
        }
    }

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        Invalidate();
    }

    private void EvictIfNeeded()
    {
        if (_cache.Count <= _maxEntries) return;

        // Drop the least-recently-touched entry (LRU)
        CacheKey? oldestKey = null;
        var oldestTick = long.MaxValue;
        foreach (var (k, v) in _cache)
        {
            if (v.Touched < oldestTick)
            {
                oldestTick = v.Touched;
                oldestKey = k;
            }
        }
        if (oldestKey is { } key && _cache.Remove(key, out var entry))
        {
            entry.Brush.Dispose();
            entry.Surface.Dispose();
        }
    }

    /// <summary>
    /// Imperatively draws the slide backdrop layer-by-layer, mirroring the
    /// React <c>useBakedBackdrop</c> reference design:
    ///
    /// <list type="number">
    /// <item>Dark base fill</item>
    /// <item>Accent-coloured radial gradient that dominates the surface</item>
    /// <item>Diagonal accent linear gradient on top</item>
    /// <item>Soft radial vignette</item>
    /// </list>
    ///
    /// The image is still part of the cache key and load pipeline because the
    /// cover art is the data source for the card, but this material is
    /// deliberately accent-driven. Drawing the blurred source into the bake made
    /// the card read as a corrupted enlarged cover instead of a clean Microsoft
    /// Store-style colour wash.
    /// </summary>
    private void BakeImperative(
        CompositionDrawingSurface surface,
        CanvasBitmap source,
        Windows.UI.Color accent,
        SizeInt32 pixelSize,
        bool isDarkTheme)
    {
        var w = pixelSize.Width;
        var h = pixelSize.Height;

        using var session = CanvasComposition.CreateDrawingSession(surface);

        // 1. Dark base
        session.Clear(Windows.UI.Color.FromArgb(255, 0x1A, 0x14, 0x20));

        // 2. Heavily blurred + dimmed source. This is the layer that gives
        //    the bake its colour variety — without it the accent gradients
        //    alone read as a flat single-hue radial. The source is centred,
        //    overscanned (so the blur halo doesn't reveal hard edges),
        //    blurred 60 DIPs, and dimmed via Exposure so it provides
        //    structure underneath the accent overlay rather than competing
        //    with it. Keeping Saturation at identity (skipping the
        //    SaturationEffect, which is hard-clamped to [0,1] in Win2D, and
        //    skipping a custom matrix > 1 because it blows pixels into the
        //    rainbow-noise look we hit earlier).
        using (var transform = new Transform2DEffect
        {
            Source = source,
            TransformMatrix = ComputeCenterTransform(source.Size, pixelSize),
            InterpolationMode = CanvasImageInterpolation.Linear,
        })
        using (var blurred = new GaussianBlurEffect
        {
            Source = transform,
            BlurAmount = 60f,
        })
        using (var dimmed = new ExposureEffect
        {
            Source = blurred,
            Exposure = -0.4f, // ~75% brightness, keeps colour visible
        })
        {
            session.DrawImage(dimmed);
        }

        // 3. Heavy accent radial gradient over the blurred source — matches
        //    the React stops exactly: 55% accent at the cover's centre,
        //    decaying through 18% accent to 95% near-black at the edges.
        var nearBlack = Windows.UI.Color.FromArgb(255, 0x0F, 0x0C, 0x14);
        using (var radial = new CanvasRadialGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0f,    Color = WithAlpha(accent,    0.55f) },
            new CanvasGradientStop { Position = 0.45f, Color = WithAlpha(accent,    0.18f) },
            new CanvasGradientStop { Position = 1f,    Color = WithAlpha(nearBlack, 0.95f) },
        }))
        {
            radial.Center = new Vector2(w * 0.72f, h * 0.5f);
            radial.RadiusX = w * 0.85f;
            radial.RadiusY = h * 0.85f;
            session.FillRectangle(0, 0, w, h, radial);
        }

        // 4. Diagonal accent linear overlay — adds top-left warmth, dark
        //    bottom-right. Drawn source-over (Win2D's drawing session
        //    doesn't expose Overlay-mode compositing without a BlendEffect
        //    graph — alpha-blending is a close enough approximation).
        var bgCool = Windows.UI.Color.FromArgb(255, 0x14, 0x10, 0x1C);
        using (var linear = new CanvasLinearGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = WithAlpha(accent, 0.40f) },
            new CanvasGradientStop { Position = 1f, Color = WithAlpha(bgCool, 0.95f) },
        }))
        {
            linear.StartPoint = new Vector2(0, 0);
            linear.EndPoint = new Vector2(w, h);
            session.FillRectangle(0, 0, w, h, linear);
        }

        // 5. Procedural noise — kills the 8-bit colour banding visible across
        //    the heavy blur + smooth gradients. Theme-aware range matches
        //    the Microsoft Store paper §4.1.1.
        //
        //    CRITICAL: NoiseShader.Execute returns `new(c, c, c, alpha)` —
        //    UNPREMULTIPLIED straight alpha. Win2D effects assume their
        //    inputs are premultiplied; without an explicit premultiply step
        //    a 50% gray @ 2.4% alpha gets read as a 20× boost (clamped to
        //    white) at 2.4%, which renders as the "dominating gray static"
        //    the bake had previously. The fix is the same one Microsoft
        //    Learn's FrostedGlassEffect tutorial calls out:
        //
        //      "remember to manually insert premultiply/unpremultiply nodes
        //       before and after custom shaders, to ensure colors are
        //       correctly preserved."
        //
        //    https://learn.microsoft.com/en-us/windows/apps/develop/win2d/custom-effects
        var (noiseMin, noiseMax) = isDarkTheme ? ((byte)0, (byte)255) : ((byte)128, (byte)255);
        using (var noise = new PixelShaderEffect<NoiseShader>())
        using (var premulNoise = new PremultiplyEffect { Source = noise })
        {
            noise.ConstantBuffer = new NoiseShader((byte)6, noiseMin, noiseMax); // ~2.4% alpha
            session.DrawImage(premulNoise, Vector2.Zero, new Rect(0, 0, w, h));
        }

        // 6. Soft vignette
        using (var vignette = new CanvasRadialGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0.0f, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 0.7f, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 1.0f, Color = Windows.UI.Color.FromArgb(80, 0, 0, 0) },
        }))
        {
            vignette.Center = new Vector2(w * 0.5f, h * 0.5f);
            vignette.RadiusX = MathF.Max(w, h) * 0.75f;
            vignette.RadiusY = MathF.Max(w, h) * 0.75f;
            session.FillRectangle(0, 0, w, h, vignette);
        }
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, float a) =>
        Windows.UI.Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255f), c.R, c.G, c.B);

    /// <summary>
    /// Compute the affine transform that centres the source image inside the
    /// output rect (paper §4.1, "Transform2DEffect placement step"). The
    /// source is scaled UniformToFill — bigger axis is cropped — then
    /// translated to centre the result.
    /// </summary>
    private static Matrix3x2 ComputeCenterTransform(Size source, SizeInt32 outputPx)
    {
        var sw = (float)source.Width;
        var sh = (float)source.Height;
        if (sw <= 0 || sh <= 0) return Matrix3x2.Identity;
        var ow = outputPx.Width;
        var oh = outputPx.Height;
        var scale = MathF.Max(ow / sw, oh / sh) * 1.45f; // Overscan: hides blur halo at edges
        var dx = (ow - sw * scale) * 0.5f;
        var dy = (oh - sh * scale) * 0.5f;
        return Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(dx, dy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Invalidate();
        if (_device.IsValueCreated)
        {
            _device.Value.DeviceLost -= OnDeviceLost;
        }
        if (_graphicsDevice.IsValueCreated)
        {
            _graphicsDevice.Value.Dispose();
        }
    }

    private readonly record struct CacheKey(
        Uri ImageUri,
        Windows.UI.Color Accent,
        SizeInt32 PixelSize,
        bool IsDarkTheme);

    private sealed class CacheEntry
    {
        public CompositionSurfaceBrush Brush { get; }
        public CompositionDrawingSurface Surface { get; }
        public long Touched { get; set; }

        public CacheEntry(CompositionSurfaceBrush brush, CompositionDrawingSurface surface, long touched)
        {
            Brush = brush;
            Surface = surface;
            Touched = touched;
        }
    }
}
