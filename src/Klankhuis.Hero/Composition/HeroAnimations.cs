using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;

namespace Klankhuis.Hero.Composition;

/// <summary>
/// Factory for the per-slide <see cref="ExpressionAnimation"/> set. All
/// expressions reference one source of truth — <see cref="InteractionTracker.Position"/>
/// X — plus the shared property set on <see cref="HeroInteraction"/>. Animations
/// run on the compositor thread, so the carousel never spends a frame in
/// managed code during user interaction.
/// </summary>
/// <remarks>
/// Naming convention for parameters in the expression strings:
/// <list type="bullet">
/// <item><c>tracker</c>: the <see cref="InteractionTracker"/></item>
/// <item><c>shared</c>: the <see cref="HeroInteraction.SharedPropertySet"/>
/// (StepX, Direction, ItemCount)</item>
/// <item><c>item</c>: the per-slide property set (Index)</item>
/// </list>
/// </remarks>
internal static class HeroAnimations
{
    private const string Norm = "(item.Index - tracker.Position.X / shared.StepX)";
    private const string AbsNorm = "Abs" + "(" + Norm + ")";
    private const string Clamped = "Min(" + AbsNorm + ", 1.0)";

    public static ExpressionAnimation BuildSlideOffsetX(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        // Power-curve x position: slides hang back near center, rush out
        // near edges (matches the React `Sign(off) * pow(|off|, 1.45) * step`).
        // Composition expressions don't include a `Sign(...)` function, so we
        // emulate it with a ternary on the raw normalized offset.
        var anim = c.CreateExpressionAnimation(
            $"({Norm} >= 0 ? 1.0 : -1.0) * Pow({AbsNorm}, 1.45) * shared.StepX");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildSlideZIndex(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        // Direction-aware: heading-toward slide rides on top.
        // 0 direction (idle) -> centered slide highest by absOff falloff.
        var expr = $"shared.Direction == 0 " +
                   $"? (100 - {Clamped} * 10) " +
                   $": (100 + shared.Direction * {Norm} * 12)";
        var anim = c.CreateExpressionAnimation(expr);
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildBgScale(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        var anim = c.CreateExpressionAnimation(
            $"Vector3(1.08 - 0.04 * {Clamped}, 1.08 - 0.04 * {Clamped}, 1)");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildContentScale(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item, float intensity = 1f)
    {
        var anim = c.CreateExpressionAnimation(
            $"Vector3(1 - 0.55 * {intensity:0.###} * {Clamped}, 1 - 0.55 * {intensity:0.###} * {Clamped}, 1)");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildContentOpacity(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        // Text should belong to the slide currently under the user's eye, not
        // to SelectedIndex. SelectedIndex only changes after the tracker settles,
        // so driving opacity from it makes the old slide's text linger while
        // fast scrubbing. This expression fades each slide's own overlay based
        // on its continuous offset from center.
        var anim = c.CreateExpressionAnimation($"1 - Min({AbsNorm} * 1.15, 1.0)");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildCoverScale(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item, float intensity = 1f)
    {
        var anim = c.CreateExpressionAnimation(
            $"Vector3(1 - 0.45 * {intensity:0.###} * {Clamped}, 1 - 0.45 * {intensity:0.###} * {Clamped}, 1)");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildBgArtOffsetX(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        // Parallax — bg art drifts opposite to scroll at ~0.6× ratio
        var anim = c.CreateExpressionAnimation($"-40.0 * {Norm}");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    public static ExpressionAnimation BuildGlowOpacity(
        Compositor c, InteractionTracker tracker, CompositionPropertySet shared, CompositionPropertySet item)
    {
        var anim = c.CreateExpressionAnimation($"1 - {Clamped}");
        Bind(anim, tracker, shared, item);
        return anim;
    }

    /// <summary>
    /// Centre-point at left edge, mid-height — matches CSS
    /// <c>transform-origin: 0 50%</c>. Reads <c>this.Target.Size</c>, which
    /// only works for XAML hand-out visuals where layout sets the Size
    /// property. For pure Composition visuals (Size always 0,0 with
    /// RelativeSizeAdjustment) use the overload that takes a size source.
    /// </summary>
    public static ExpressionAnimation BuildLeftMidCenterPoint(Compositor c)
        => c.CreateExpressionAnimation("Vector3(0, this.Target.Size.Y / 2, 0)");

    /// <summary>
    /// Centre-point at left edge, mid-height of <paramref name="sizeSource"/>.
    /// Use for Composition visuals (Cover, Content, Background) whose own
    /// Size property is (0,0); pass the slide host's hand-out visual as the
    /// size source.
    /// </summary>
    public static ExpressionAnimation BuildLeftMidCenterPoint(Compositor c, Visual sizeSource)
    {
        var anim = c.CreateExpressionAnimation("Vector3(0, host.Size.Y / 2, 0)");
        anim.SetReferenceParameter("host", sizeSource);
        return anim;
    }

    /// <summary>Centre-point centred — for XAML hand-out visuals.</summary>
    public static ExpressionAnimation BuildCenterPoint(Compositor c)
        => c.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");

    /// <summary>
    /// Centre-point centred on <paramref name="sizeSource"/>. Use for
    /// Composition visuals whose own Size property is (0,0).
    /// </summary>
    public static ExpressionAnimation BuildCenterPoint(Compositor c, Visual sizeSource)
    {
        var anim = c.CreateExpressionAnimation("Vector3(host.Size.X / 2, host.Size.Y / 2, 0)");
        anim.SetReferenceParameter("host", sizeSource);
        return anim;
    }

    private static void Bind(
        ExpressionAnimation a,
        InteractionTracker tracker,
        CompositionPropertySet shared,
        CompositionPropertySet item)
    {
        a.SetReferenceParameter("tracker", tracker);
        a.SetReferenceParameter("shared", shared);
        a.SetReferenceParameter("item", item);
    }
}
