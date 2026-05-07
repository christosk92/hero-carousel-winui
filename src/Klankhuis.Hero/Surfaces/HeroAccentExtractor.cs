using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Klankhuis.Hero.Surfaces;

/// <summary>
/// Standalone dominant-colour extractor — same HSV-bucketing algorithm
/// the carousel's <see cref="BakedSurfaceCache"/> runs internally
/// during the bake, exposed as a public static helper so consumers can
/// match the carousel's per-slide dominant for off-carousel UI (e.g.
/// the <see cref="Controls.SideCard"/>'s diagonal wash colour).
/// </summary>
/// <remarks>
/// Each call loads the image fresh — no per-URI cache, since this is
/// meant for one-shot use at consumer startup. If you need cached
/// access, wire it into a <see cref="BakedSurfaceCache"/> instance
/// instead and use <see cref="BakedSurfaceCache.GetExtractedAccentAsync"/>.
/// </remarks>
public static class HeroAccentExtractor
{
    /// <summary>
    /// Default (neutral grey) returned on any failure path. Mirrors
    /// <see cref="BakedSurfaceCache.DefaultAccent"/> so consumers can
    /// compare against either sentinel interchangeably.
    /// </summary>
    public static readonly Windows.UI.Color DefaultAccent =
        Windows.UI.Color.FromArgb(255, 128, 128, 128);

    /// <summary>
    /// Loads the image at <paramref name="imageUri"/>, downsamples it
    /// to 48×48, HSV-buckets the result (12 hue bins, dropping pixels
    /// with brightness &lt; 0.30 or saturation &lt; 0.50), and returns
    /// the average RGB of the highest-scoring bin. Returns
    /// <see cref="DefaultAccent"/> on any load / decode failure.
    /// </summary>
    public static async Task<Windows.UI.Color> ExtractAsync(
        Uri imageUri,
        CancellationToken ct = default)
    {
        var device = CanvasDevice.GetSharedDevice();
        try
        {
            var streamRef = RandomAccessStreamReference.CreateFromUri(imageUri);
            using var stream = await streamRef.OpenReadAsync().AsTask(ct);
            using var bitmap = await CanvasBitmap.LoadAsync(device, stream).AsTask(ct);

            const int SampleSize = 48;
            using var sample = new CanvasRenderTarget(device, SampleSize, SampleSize, 96);
            using (var ds = sample.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.DrawImage(bitmap, new Rect(0, 0, SampleSize, SampleSize));
            }
            return ExtractDominant(sample.GetPixelColors());
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return DefaultAccent;
        }
    }

    private static Windows.UI.Color ExtractDominant(Windows.UI.Color[] pixels)
    {
        const int BucketCount = 12;
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
}
