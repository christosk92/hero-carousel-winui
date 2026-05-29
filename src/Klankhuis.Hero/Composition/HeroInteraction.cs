using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Input;

namespace Klankhuis.Hero.Composition;

/// <summary>
/// Owns the <see cref="InteractionTracker"/> and the
/// <see cref="VisualInteractionSource"/> that feeds it — the off-thread
/// state machine that drives every slide's transform.
/// </summary>
/// <remarks>
/// Position is the source of truth: it's a continuous float in pixels along
/// X. Every slide's expression animations consume it directly.
/// "Intent direction" is a separate latched scalar in <see cref="SharedPropertySet"/>
/// that's set on user gestures (drag/wheel/key/dot) — never derived from
/// velocity, so the z-ordering doesn't flicker as the tween settles.
/// </remarks>
internal sealed partial class HeroInteraction : IInteractionTrackerOwner, IDisposable
{
    private readonly Compositor _compositor;
    private readonly InteractionTracker _tracker;
    private readonly VisualInteractionSource _source;
    private readonly CompositionPropertySet _shared;
    private float _pendingTargetX; // last position we asked the tracker to land on
    private bool _disposed;

    public HeroInteraction(Compositor compositor, Visual stageVisual)
    {
        _compositor = compositor;
        _tracker = InteractionTracker.CreateWithOwner(compositor, this);

        _source = VisualInteractionSource.Create(stageVisual);
        _source.PositionXSourceMode = InteractionSourceMode.EnabledWithInertia;
        _source.PositionYSourceMode = InteractionSourceMode.Disabled;
        _source.ScaleSourceMode = InteractionSourceMode.Disabled;
        _source.ManipulationRedirectionMode =
            VisualInteractionSourceRedirectionMode.CapableTouchpadAndPointerWheel;
        _source.IsPositionXRailsEnabled = true;
        _source.PositionXChainingMode = InteractionChainingMode.Never;
        // Mouse wheel is handled in XAML PointerWheelChanged → TryUpdatePositionBy.
        // Touchpad / touch / pen flow through ManipulationRedirectionMode above.

        _tracker.InteractionSources.Add(_source);
        _tracker.MinPosition = Vector3.Zero;
        _tracker.MaxPosition = Vector3.Zero;
        _tracker.PositionInertiaDecayRate = new Vector3(0.93f);

        _shared = compositor.CreatePropertySet();
        _shared.InsertScalar("StepX", 1f);
        _shared.InsertScalar("Direction", 0f);
        _shared.InsertScalar("ItemCount", 1f);

        // Snap-to-slide inertia modifier. Without this, the tracker just
        // decays wherever the flick's velocity dies out — visible as a
        // long slow drift followed by an instant teleport to the nearest
        // slide on `OnTrackerIdle`. With this modifier the tracker
        // recomputes its trajectory to *land on* a multiple of `StepX`,
        // and the built-in deceleration provides the smooth easing the
        // React version had via its quart-out keyframe tween.
        var snap = InteractionTrackerInertiaRestingValue.Create(compositor);
        snap.Condition = compositor.CreateExpressionAnimation("true");
        var resting = compositor.CreateExpressionAnimation(
            "Round(this.Target.NaturalRestingPosition.X / shared.StepX) * shared.StepX");
        resting.SetReferenceParameter("shared", _shared);
        snap.RestingValue = resting;
        _tracker.ConfigurePositionXInertiaModifiers(new[] { snap });
    }

    public InteractionTracker Tracker => _tracker;
    public VisualInteractionSource Source => _source;

    /// <summary>
    /// Shared parameters every slide expression animation references:
    /// <c>StepX</c>, <c>Direction</c>, <c>ItemCount</c>.
    /// </summary>
    public CompositionPropertySet SharedPropertySet => _shared;

    public event Action? IdleEntered;
    public event Action? InteractionStarted;
    /// <summary>
    /// Fires every time the tracker's position changes (per compositor
    /// frame during inertia / animation / drag) with the continuous
    /// fractional slide index (<c>Position.X / StepX</c>). Subscribers
    /// drive per-frame UI-thread updates that need to read tracker state
    /// — e.g., colour-lerping the outer halo between neighbouring slides'
    /// extracted accents without waiting for an idle settle.
    /// </summary>
    public event Action<float>? PositionChanged;

    public void SetItemCount(int count)
    {
        _shared.InsertScalar("ItemCount", count);
        UpdateBounds();
    }

    public void SetStep(float stepX)
    {
        if (stepX <= 0) stepX = 1f;
        _shared.InsertScalar("StepX", stepX);
        UpdateBounds();
    }

    /// <summary>
    /// User-intent direction latch (+1 forward, -1 back, 0 = unset). Updated
    /// on each gesture, never on velocity.
    /// </summary>
    public void SetIntentDirection(int direction)
    {
        if (direction == 0) return;
        _shared.InsertScalar("Direction", Math.Sign(direction));
    }

    public void RedirectForManipulation(PointerPoint pointer)
    {
        try { _source.TryRedirectForManipulation(pointer); }
        catch { /* not all pointer types are redirectable */ }
    }

    /// <summary>Animate to a target slide index.</summary>
    public void GoTo(int index, CompositionEasingFunction? easing = null)
    {
        var step = ReadStep();
        var target = new Vector3(index * step, 0, 0);
        _pendingTargetX = target.X;
        SetIntentDirection(target.X > _tracker.Position.X ? 1 : -1);

        if (easing is null)
        {
            _tracker.TryUpdatePositionWithAdditionalVelocity(Vector3.Zero);
            _tracker.TryUpdatePosition(target);
        }
        else
        {
            // Position keyframe animation
            var ka = _compositor.CreateVector3KeyFrameAnimation();
            ka.InsertKeyFrame(1f, target, easing);
            ka.Duration = TimeSpan.FromMilliseconds(540);
            _tracker.TryUpdatePositionWithAnimation(ka);
        }
    }

    /// <summary>Snap to nearest slide instantly (used for resize / data reset).</summary>
    public void SnapToIndex(int index)
    {
        var step = ReadStep();
        var target = new Vector3(index * step, 0, 0);
        _pendingTargetX = target.X;
        _tracker.TryUpdatePosition(target);
    }

    /// <summary>
    /// Slide index the tracker has *settled* on (i.e. after the most recent
    /// idle transition). Reads the live <c>Position</c>, rounded.
    /// </summary>
    public int CurrentSlide => Math.Max(0, (int)Math.Round(_tracker.Position.X / Math.Max(0.001f, ReadStep())));

    /// <summary>
    /// Slide index the tracker is *heading* to. During an in-flight
    /// animation this is the keyframe target rather than the live Position;
    /// when idle, it equals <see cref="CurrentSlide"/>. Used as the pivot
    /// for delta-based navigation (wheel / keyboard / autoplay) so a tick
    /// during an animation doesn't pivot off a stale settled value.
    /// </summary>
    public int PendingSlide
    {
        get
        {
            var step = Math.Max(0.001f, ReadStep());
            // _tracker.Position is the live value; PositionVelocityInPixelsPerSecond
            // can extrapolate if needed. The cleanest "pending" value is the
            // most recent target we asked the tracker to head toward. We
            // store that ourselves on every GoTo / TryUpdatePosition call.
            return Math.Max(0, (int)Math.Round(_pendingTargetX / step));
        }
    }

    private float ReadStep()
    {
        _shared.TryGetScalar("StepX", out var s);
        return s;
    }

    private void UpdateBounds()
    {
        _shared.TryGetScalar("StepX", out var step);
        _shared.TryGetScalar("ItemCount", out var count);
        var max = Math.Max(0f, (count - 1) * step);
        _tracker.MinPosition = Vector3.Zero;
        _tracker.MaxPosition = new Vector3(max, 0, 0);
    }

    // ─── IInteractionTrackerOwner ────────────────────────────────────────

    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {
        // Tracker is now resting on Position; mirror that into the pending
        // target so PendingSlide stays consistent with what the user sees.
        _pendingTargetX = sender.Position.X;
        IdleEntered?.Invoke();
    }

    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
        => InteractionStarted?.Invoke();

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }
    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        var step = ReadStep();
        if (step <= 0) return;
        PositionChanged?.Invoke(args.Position.X / step);
    }
    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shared.Dispose();
        _source.Dispose();
        _tracker.Dispose();
    }
}
