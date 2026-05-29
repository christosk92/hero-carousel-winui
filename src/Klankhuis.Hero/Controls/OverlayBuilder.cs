using System;
using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Klankhuis.Hero.Controls;

/// <summary>
/// Result of <see cref="OverlayBuilder.Create"/> — the slide-overlay root,
/// a reference to the cover-shimmer placeholder so the carousel can fade
/// it when the Composition cover image loads, and a reference to the
/// title <see cref="TextBlock"/> so the carousel can adjust its
/// <c>FontSize</c> in response to slide-host resizes (responsive
/// typography — CSS <c>clamp(28px, 5vw, 50px)</c> equivalent).
/// </summary>
internal readonly record struct OverlayParts(
    FrameworkElement Root,
    Shimmer CoverShimmer,
    TextBlock Title,
    Button? PrimaryCta,
    Button? SecondaryCta);

/// <summary>
/// Builds the per-slide XAML overlay programmatically. Slot-based: every
/// optional element (icon badge, eyebrow chip, subtitle, CTAs, metadata
/// chips) renders only when its source field on <see cref="HeroCarouselItem"/>
/// is populated, so the same builder produces every Microsoft-Store-style
/// hero variant from "title + tagline only" up through "logo + price chip
/// + title + tagline + primary CTA + secondary CTA + rating chip".
/// </summary>
/// <remarks>
/// We build in code (not via <see cref="ItemsControl"/>) because the
/// carousel needs direct refs to each slide's container hand-out visual
/// for the per-slide expression animations.
/// </remarks>
internal static class OverlayBuilder
{
    public static OverlayParts Create(
        HeroCarouselItem item,
        Style? primaryCtaStyle = null,
        Style? secondaryCtaStyle = null)
    {
        // Two columns: text on the left (* with MinWidth=0 so it can shrink
        // below content size + MaxWidth=820 so TextBlock measure passes the
        // wrap constraint to children — paired with the title's responsive
        // FontSize clamp(28, 5.5vw, 76) in HeroCarousel.UpdateResponsiveTypography),
        // cover slot on the right (Auto, sized by the Shimmer's explicit Width).
        //
        // Padding was 56,40,56,40 + cover slot 280 wide. On hosts < ≈ 750 px wide
        // (e.g. HomePage with the right Queue panel open) that left the text
        // column under ~100 px — narrower than a single Black-weight title
        // character, so wrapping degenerated to one-char-per-line + ellipsis.
        // Trimmed horizontal padding to 32 to give the text column ~48 px back.
        var grid = new Grid
        {
            Padding = new Thickness(32, 40, 32, 40),
            ColumnSpacing = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            // Hit-testing: the *grid itself* has no Background and stays
            // hit-test visible so descendants (CTA buttons) can receive
            // pointer events. Empty space within the grid has no rendered
            // surface, so events fall through to the carousel's
            // PART_SlideHost beneath, which redirects them into the
            // InteractionTracker for scrub. Per-element overrides below
            // mark the non-interactive text/chip blocks as
            // IsHitTestVisible=false so a tap on a glyph also passes
            // through.
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 0,
            MaxWidth = 820,
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });

        var stack = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(stack, 0);

        // ── Optional icon badge (above the eyebrow chip) ────────────────
        if (item.IconUri is not null)
        {
            var iconImage = new Image
            {
                Source = new BitmapImage(item.IconUri),
                Width = 56,
                Height = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
            };
            // Wrap in a Border so we can round the corners (Image itself
            // has no CornerRadius; Border clips to its CornerRadius).
            var iconHost = new Border
            {
                CornerRadius = new CornerRadius(8),
                Child = iconImage,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
            };
            stack.Children.Add(iconHost);
        }

        // ── Eyebrow chip (free-form, hidden if empty) ───────────────────
        if (!string.IsNullOrEmpty(item.Eyebrow))
        {
            stack.Children.Add(BuildChip(item.Eyebrow));
        }

        // ── Title ───────────────────────────────────────────────────────
        // Length-based scaling. The hero band is height-budgeted (HomePage
        // caps it at ~420 px), so a fixed 44/3-line title can claim half the
        // hero on long audiobook / show names like
        // "Stoicism: How to Use Stoic Philosophy to Find Inner Peace and
        // Happiness". Three tiers keep short titles dramatic and long titles
        // legible without re-introducing the char-by-char wrap.
        var titleLength = item.Title?.Length ?? 0;
        double titleFontSize;
        double titleLineHeight;
        int titleMaxLines;
        if (titleLength <= 36)
        {
            titleFontSize = 44; titleLineHeight = 48; titleMaxLines = 2;
        }
        else if (titleLength <= 64)
        {
            titleFontSize = 36; titleLineHeight = 40; titleMaxLines = 2;
        }
        else
        {
            titleFontSize = 30; titleLineHeight = 34; titleMaxLines = 3;
        }
        var title = new TextBlock
        {
            Text = item.Title,
            FontFamily = new FontFamily("Inter, Segoe UI Variable Text, Segoe UI"),
            FontSize = titleFontSize,
            FontWeight = FontWeights.Black,
            LineHeight = titleLineHeight,
            CharacterSpacing = -20,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxLines = titleMaxLines,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            IsHitTestVisible = false,
        };
        stack.Children.Add(title);

        // ── Tagline ─────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(item.Tagline))
        {
            stack.Children.Add(BuildTagline(item));
        }

        // ── Subtitle ────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(item.Subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Subtitle,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 11,
                CharacterSpacing = 100,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(158, 255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            });
        }

        // ── CTA row (primary + optional secondary) ──────────────────────
        var hasPrimary = !string.IsNullOrEmpty(item.PrimaryCtaText);
        var hasSecondary = !string.IsNullOrEmpty(item.SecondaryCtaText);
        Button? primaryButton = null;
        Button? secondaryButton = null;
        if (hasPrimary || hasSecondary)
        {
            var ctaRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
            };

            if (hasPrimary)
            {
                var (wrapper, btn) = BuildCtaButton(
                    item.PrimaryCtaText,
                    item.PrimaryCtaCommand,
                    item.PrimaryCtaCommandParameter,
                    primary: true,
                    accent: item.Accent,
                    style: primaryCtaStyle);
                primaryButton = btn;
                ctaRow.Children.Add(wrapper);
            }
            if (hasSecondary)
            {
                var (wrapper, btn) = BuildCtaButton(
                    item.SecondaryCtaText,
                    item.SecondaryCtaCommand,
                    item.SecondaryCtaCommandParameter,
                    primary: false,
                    accent: item.Accent,
                    style: secondaryCtaStyle);
                secondaryButton = btn;
                ctaRow.Children.Add(wrapper);
            }
            stack.Children.Add(ctaRow);
        }

        // ── Metadata chips row ──────────────────────────────────────────
        if (item.Metadata is { Count: > 0 } metadata)
        {
            stack.Children.Add(BuildMetadataRow(metadata));
        }

        grid.Children.Add(stack);

        // Cover image Shimmer placeholder — sits in column 1, sized to
        // approximately match the Composition CoverImage. The carousel
        // collapses this on cover-load via its Shimmer fade-out logic.
        //
        // Reserved width here is the *layout slot* the title text column
        // reflows around. Tracks HeroSlideVisual.CoverFraction (0.35) at a
        // ~480-px slide height — anything larger and the cover grows past
        // the slot, which is visually fine because the Composition cover is
        // a square in the right-mid area, not a column. Going much wider
        // than 160 here would re-introduce the char-by-char title wrap on
        // narrow hosts.
        var coverShimmer = new Shimmer
        {
            Width = 160,
            Height = 160,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            IsActive = true,
            IsHitTestVisible = false,
        };
        Grid.SetColumn(coverShimmer, 1);
        grid.Children.Add(coverShimmer);

        return new OverlayParts(grid, coverShimmer, title, primaryButton, secondaryButton);
    }

    /// <summary>
    /// Re-applies the glass-accent treatment to an existing CTA button
    /// without rebuilding it. Called by <see cref="HeroCarousel"/> once
    /// the cover image's dominant colour has been extracted, so the
    /// visible button "snaps in" to the image-derived hue.
    /// </summary>
    public static void RethemeCtaButton(Button button, Windows.UI.Color accent, bool primary)
        => ApplyGlassAccent(button, accent, primary);

    /// <summary>
    /// Subtle white-on-frosted chip used for both the eyebrow and the
    /// metadata row entries. Same visual style across slots so the page
    /// reads as "one chip family" rather than "eyebrow vs rating".
    /// </summary>
    private static Border BuildChip(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 12, 5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(31, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 200,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(218, 255, 255, 255)),
            },
        };
    }

    private static FrameworkElement BuildTagline(HeroCarouselItem item)
    {
        var text = new TextBlock
        {
            Text = item.Tagline,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(219, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        if (string.IsNullOrEmpty(item.TaglineIconGlyph))
            return text;

        var row = new Grid
        {
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = item.TaglineIconGlyph,
            FontSize = 14,
            Foreground = new SolidColorBrush(item.TaglineIconColor),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return row;
    }

    /// <summary>
    /// Renders one metadata chip: optional leading icon (image preferred
    /// over font glyph) + a label.
    /// </summary>
    private static Border BuildMetadataChip(HeroCarouselMetadataItem item)
    {
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (item.IconUri is not null)
        {
            inner.Children.Add(new Image
            {
                Source = new BitmapImage(item.IconUri),
                Width = 18,
                Height = 18,
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (!string.IsNullOrEmpty(item.IconGlyph))
        {
            inner.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = item.IconGlyph,
                FontSize = 14,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(218, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        if (!string.IsNullOrEmpty(item.Label))
        {
            inner.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(218, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 10, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(31, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Child = inner,
        };
    }

    private static StackPanel BuildMetadataRow(IList<HeroCarouselMetadataItem> metadata)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        foreach (var m in metadata)
            row.Children.Add(BuildMetadataChip(m));
        return row;
    }

    /// <summary>
    /// Hero-rail-style "glass" CTA button — translucent fill tinted with
    /// the slide's accent, hairline accent border, white text. Hover and
    /// pressed states deepen the accent saturation. Mirrors the Microsoft
    /// Store hero rail's <c>Install</c> / <c>Get</c> buttons, where each
    /// slide's CTA picks up the carousel's per-slide tint.
    /// </summary>
    /// <remarks>
    /// <para>Implementation note: rather than wiring imperative pointer
    /// handlers, we override the standard <see cref="Button"/>'s
    /// per-state <c>ThemeResource</c>s on the button's local
    /// <c>Resources</c> dictionary. The default Button template already
    /// looks up <c>ButtonBackground</c> / <c>ButtonBackgroundPointerOver</c>
    /// / <c>ButtonBackgroundPressed</c> / <c>ButtonBorderBrush*</c> /
    /// <c>ButtonForeground*</c> via <c>{ThemeResource …}</c>, and that
    /// lookup walks up the visual tree starting from the button itself —
    /// so per-button overrides win. We get the standard template's
    /// built-in state-transition animations for free.</para>
    /// <para>Primary buttons get a denser glass (~30 % accent fill);
    /// secondary buttons are barely tinted (~14 %) so the two are
    /// visually distinct without losing the family resemblance.</para>
    /// </remarks>
    private static (FrameworkElement Wrapper, Button Button) BuildCtaButton(string text, System.Windows.Input.ICommand? command, object? commandParameter, bool primary, Windows.UI.Color accent, Style? style)
    {
        var button = new Button
        {
            Content = text,
            Command = command,
            CommandParameter = commandParameter,
            // Default IsHitTestVisible = true; this is what makes the CTA
            // clickable. Drag *originating* on a button stays in the
            // button's pointer-capture (no scrub), which is the expected
            // hero-rail behaviour.
        };

        if (style is not null)
        {
            // Consumer-supplied style — leaves padding / corner radius /
            // typography to the consumer. We only own the per-state
            // accent tinting via local Resources below.
            button.Style = style;
        }
        else
        {
            // Built-in compact-glass defaults. Tight padding — matches
            // the Microsoft Store hero rail proportions (~34 px total
            // height with FontSize=14). BorderThickness is bottom-only:
            // the only edge that should read as a "rim" is the inner
            // shadow at the bottom, which gives the button depth without
            // the cartoony "fully outlined" feel.
            button.Padding = new Thickness(22, 7, 22, 7);
            button.CornerRadius = new CornerRadius(6);
            button.FontWeight = FontWeights.SemiBold;
            button.FontSize = 14;
            button.BorderThickness = new Thickness(0, 0, 0, 1.5);
        }

        ApplyGlassAccent(button, accent, primary);

        // Foreground stays white through every state — accent-tinted
        // white text on accent-tinted glass would be illegible.
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        button.Resources["ButtonForeground"]            = white;
        button.Resources["ButtonForegroundPointerOver"] = white;
        button.Resources["ButtonForegroundPressed"]     = white;
        button.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(140, 255, 255, 255));

        // Drop shadow is *only* attached to primary CTAs. The secondary
        // button is a translucent ghost (α≈90 on its fill); a shadow
        // behind it would bleed *through* the button's fill, making it
        // read as a dark patch on top of the page bg rather than a
        // tinted glass button. Primaries are opaque enough to occlude
        // the shadow body, so the visible spread is the soft halo we
        // want.
        if (primary)
        {
            var wrapper = new Grid();
            var shadowHost = new Grid { IsHitTestVisible = false };
            wrapper.Children.Add(shadowHost);
            wrapper.Children.Add(button);

            AttachDropShadowOnLoad(shadowHost, button.CornerRadius.TopLeft);
            return (wrapper, button);
        }

        return (button, button);
    }

    /// <summary>
    /// Defers Composition setup until the host element has bounds. We
    /// build a brushless <see cref="SpriteVisual"/> sized 1:1 with the
    /// host, give it a black <see cref="DropShadow"/> with a rounded
    /// alpha mask matching the button's corner radius, and attach via
    /// <see cref="ElementCompositionPreview.SetElementChildVisual"/>.
    /// </summary>
    private static void AttachDropShadowOnLoad(Grid shadowHost, double cornerRadius)
    {
        if (shadowHost.IsLoaded) Attach();
        else shadowHost.Loaded += OnLoaded;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            shadowHost.Loaded -= OnLoaded;
            Attach();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            shadowHost.Unloaded -= OnUnloaded;
            ElementCompositionPreview.SetElementChildVisual(shadowHost, null);
        }

        void Attach()
        {
            var hostVisual = ElementCompositionPreview.GetElementVisual(shadowHost);
            var compositor = hostVisual.Compositor;

            // Rounded mask — same VisualSurface trick HeroHalo uses, but
            // sized to the host instead of stretched.
            var maskShape = compositor.CreateShapeVisual();
            var maskShapeSize = compositor.CreateExpressionAnimation("host.Size");
            maskShapeSize.SetReferenceParameter("host", hostVisual);
            maskShape.StartAnimation("Size", maskShapeSize);

            var maskGeom = compositor.CreateRoundedRectangleGeometry();
            maskGeom.CornerRadius = new Vector2((float)cornerRadius, (float)cornerRadius);
            var maskGeomSize = compositor.CreateExpressionAnimation("host.Size");
            maskGeomSize.SetReferenceParameter("host", hostVisual);
            maskGeom.StartAnimation("Size", maskGeomSize);

            var fill = compositor.CreateSpriteShape(maskGeom);
            fill.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
            maskShape.Shapes.Add(fill);

            var visualSurface = compositor.CreateVisualSurface();
            visualSurface.SourceVisual = maskShape;
            var visSurfaceSize = compositor.CreateExpressionAnimation("host.Size");
            visSurfaceSize.SetReferenceParameter("host", hostVisual);
            visualSurface.StartAnimation("SourceSize", visSurfaceSize);

            var maskBrush = compositor.CreateSurfaceBrush(visualSurface);

            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 14f;
            shadow.Offset = new Vector3(0, 3, 0);
            shadow.Color = Windows.UI.Color.FromArgb(110, 0, 0, 0);
            shadow.Mask = maskBrush;

            var shadowVisual = compositor.CreateSpriteVisual();
            shadowVisual.RelativeSizeAdjustment = Vector2.One;
            shadowVisual.Shadow = shadow;

            ElementCompositionPreview.SetElementChildVisual(shadowHost, shadowVisual);
            
            // Subscribe to Unload to detach the shadow visual when the element leaves the tree
            shadowHost.Unloaded += OnUnloaded;
        }
    }

    /// <summary>
    /// Paints the accent fill + bright "glass rim" border on a CTA
    /// button. Fill is a solid accent at high alpha; border is the
    /// accent mixed 40 % with white at full alpha — that border colour
    /// is what reads as the Microsoft-Store hero CTA's glass edge,
    /// without resorting to a top-down gradient (which read as
    /// cartoony in earlier iterations).
    /// </summary>
    /// <remarks>
    /// Per-state resource overrides handle hover/pressed via the default
    /// Button template's VSM. Direct <see cref="Control.Background"/> /
    /// <see cref="Control.BorderBrush"/> setters are also applied so the
    /// rest-state visual updates immediately even when WinUI's
    /// <c>{ThemeResource}</c> binding doesn't re-evaluate after the
    /// resource dictionary is mutated post-template-apply (a known
    /// WinAppSDK 2.0 quirk).
    /// </remarks>
    private static void ApplyGlassAccent(Button button, Windows.UI.Color accent, bool primary)
    {
        // Per-state alpha stops on the solid fill.
        var (restA, hoverA, pressedA) = primary
            ? ((byte)200, (byte)230, (byte)250)
            : ((byte)90,  (byte)135, (byte)180);

        // Bottom-rim border: a *darker* accent (mix with black). Reads as
        // a subtle inner shadow along the button's lower edge, giving
        // depth without the cartoony "outlined sticker" effect a
        // brighter rim produces.
        var rimColor       = MixWithBlack(accent, primary ? 0.30f : 0.20f);
        var rimColorHover  = MixWithBlack(accent, primary ? 0.40f : 0.30f);
        var rimBrush       = new SolidColorBrush(rimColor);
        var rimBrushHover  = new SolidColorBrush(rimColorHover);

        var rest     = AccentBrush(accent, restA);
        var hover    = AccentBrush(accent, hoverA);
        var pressed  = AccentBrush(accent, pressedA);
        var disabled = AccentBrush(accent, (byte)(restA / 2));

        // Resource overrides for VSM-driven hover / pressed transitions.
        button.Resources["ButtonBackground"]            = rest;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"]     = pressed;
        button.Resources["ButtonBackgroundDisabled"]    = disabled;

        button.Resources["ButtonBorderBrush"]            = rimBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = rimBrushHover;
        button.Resources["ButtonBorderBrushPressed"]     = rimBrushHover;
        button.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(140, rimColor.R, rimColor.G, rimColor.B));

        // Direct setters — guarantee the rest-state visual updates even
        // when {ThemeResource} bindings cache the original brush.
        button.Background = rest;
        button.BorderBrush = rimBrush;
    }

    private static Windows.UI.Color MixWithBlack(Windows.UI.Color accent, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Windows.UI.Color.FromArgb(
            255,
            (byte)(accent.R * (1 - t)),
            (byte)(accent.G * (1 - t)),
            (byte)(accent.B * (1 - t)));
    }

    private static Windows.UI.Color MixWithWhite(Windows.UI.Color accent, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Windows.UI.Color.FromArgb(
            255,
            (byte)(accent.R + (255 - accent.R) * t),
            (byte)(accent.G + (255 - accent.G) * t),
            (byte)(accent.B + (255 - accent.B) * t));
    }

    private static SolidColorBrush AccentBrush(Windows.UI.Color accent, byte alpha)
        => new(Windows.UI.Color.FromArgb(alpha, accent.R, accent.G, accent.B));
}
