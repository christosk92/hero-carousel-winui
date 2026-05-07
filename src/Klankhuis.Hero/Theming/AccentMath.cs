using System;
using System.Numerics;
using Windows.UI;

namespace Klankhuis.Hero.Theming;

/// <summary>
/// Colour helpers for the carousel material. Intentionally tiny — none of
/// these depend on Composition or Win2D, so they're easy to unit-test.
/// </summary>
internal static class AccentMath
{
    /// <summary>
    /// Mix two colours in OkLab space (perceptually uniform). <paramref name="t"/>
    /// is the fraction of <paramref name="b"/> in the result, clamped to 0–1.
    /// Matches the React `color-mix(in oklab, …)` we used in the prototype.
    /// </summary>
    public static Windows.UI.Color OklabMix(Windows.UI.Color a, Windows.UI.Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var la = SrgbToOklab(a);
        var lb = SrgbToOklab(b);
        var mix = new Vector3(
            (float)(la.X + (lb.X - la.X) * t),
            (float)(la.Y + (lb.Y - la.Y) * t),
            (float)(la.Z + (lb.Z - la.Z) * t));
        var alpha = (byte)(a.A + (b.A - a.A) * t);
        return OklabToSrgb(mix, alpha);
    }

    /// <summary>
    /// Mix between accent and pure black in OkLab. Used for our diagonal
    /// dark-end stops.
    /// </summary>
    public static Windows.UI.Color WithBlack(Windows.UI.Color accent, double blackFraction) =>
        OklabMix(accent, Windows.UI.Color.FromArgb(accent.A, 0, 0, 0), blackFraction);

    /// <summary>
    /// Mix between accent and pure white in OkLab. Used for the bright
    /// highlight stops.
    /// </summary>
    public static Windows.UI.Color WithWhite(Windows.UI.Color accent, double whiteFraction) =>
        OklabMix(accent, Windows.UI.Color.FromArgb(accent.A, 255, 255, 255), whiteFraction);

    /// <summary>
    /// Relative luminance (WCAG) — used to pick text contrast on accent
    /// backgrounds. > 0.55 ⇒ use dark text, else white.
    /// </summary>
    public static double RelativeLuminance(Windows.UI.Color c)
    {
        double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    // ─── sRGB ↔ OkLab (Björn Ottosson, 2020) ─────────────────────────────

    private static Vector3 SrgbToLinear(Windows.UI.Color c)
    {
        float L(byte v)
        {
            var x = v / 255f;
            return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
        }
        return new Vector3(L(c.R), L(c.G), L(c.B));
    }

    private static Windows.UI.Color LinearToSrgb(Vector3 lin, byte alpha)
    {
        byte S(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            var x = v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            return (byte)Math.Round(x * 255f);
        }
        return Windows.UI.Color.FromArgb(alpha, S(lin.X), S(lin.Y), S(lin.Z));
    }

    private static Vector3 SrgbToOklab(Windows.UI.Color c)
    {
        var lin = SrgbToLinear(c);
        var l = 0.4122214708f * lin.X + 0.5363325363f * lin.Y + 0.0514459929f * lin.Z;
        var m = 0.2119034982f * lin.X + 0.6806995451f * lin.Y + 0.1073969566f * lin.Z;
        var s = 0.0883024619f * lin.X + 0.2817188376f * lin.Y + 0.6299787005f * lin.Z;
        var l_ = MathF.Cbrt(l);
        var m_ = MathF.Cbrt(m);
        var s_ = MathF.Cbrt(s);
        return new Vector3(
            0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
            1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
            0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_);
    }

    private static Windows.UI.Color OklabToSrgb(Vector3 lab, byte alpha)
    {
        var l_ = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        var m_ = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        var s_ = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;
        var lin = new Vector3(
            +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
        return LinearToSrgb(lin, alpha);
    }
}
