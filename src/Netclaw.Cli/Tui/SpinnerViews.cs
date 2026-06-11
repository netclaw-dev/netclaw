// -----------------------------------------------------------------------
// <copyright file="SpinnerViews.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Shared builders for the standardized TUI loading spinner.
/// </summary>
/// <remarks>
/// Wraps Termina's self-animating <see cref="SpinnerNode"/> so every
/// probe/validation/OAuth surface gets the same glyph set, cadence, color
/// treatment, and indentation without re-implementing a frame array or
/// hand-wiring a redraw tick. The node owns its own animation timer and
/// propagates invalidation up the layout tree, so callers just drop it into the
/// tree — there is no per-surface spinner tick field and no redraw subscription
/// to wire (which is what previously produced the frozen/lazy spinners and the
/// re-entrant-redraw footgun called out in #1312).
/// </remarks>
internal static class SpinnerViews
{
    // Termina's 10-frame braille style. Matches the look of the old hand-rolled
    // 6-frame braille set but animates more smoothly.
    private const SpinnerStyle Style = SpinnerStyle.Dots;

    // Every probe surface indents content two columns; bake it in so spinner
    // lines align with the surrounding text instead of sitting at column 0.
    private const string Indent = "  ";

    /// <summary>
    /// A self-animating spinner glyph followed by a static <paramref name="label"/>,
    /// both drawn in <paramref name="color"/>, indented to match surrounding text.
    /// </summary>
    public static ILayoutNode Labeled(string label, Color color) =>
        Layouts.Horizontal()
            .WithChild(new TextNode(Indent))
            .WithChild(Spinner(label, color));

    /// <summary>
    /// A spinner + static <paramref name="label"/> with a live "(Ns)" elapsed
    /// counter rendered as a reactive sibling bound to <paramref name="elapsedSeconds"/>.
    /// </summary>
    /// <remarks>
    /// The counter updates off its own binding and the spinner animates off its
    /// own timer — neither re-runs the enclosing layout factory, so there is no
    /// animation reset and no re-entrant redraw. The counter stays hidden until
    /// the first whole second elapses (matching prior behavior).
    /// </remarks>
    public static ILayoutNode WithElapsed(string label, Color color, Observable<int> elapsedSeconds) =>
        Layouts.Horizontal()
            .WithChild(new TextNode(Indent))
            .WithChild(Spinner(label, color))
            .WithChild(elapsedSeconds.AsLayout(seconds => (ILayoutNode)(seconds > 0
                ? new TextNode($" ({seconds}s)").WithForeground(color)
                : Layouts.Empty())));

    private static SpinnerNode Spinner(string label, Color color) =>
        new SpinnerNode(Style)
            .WithLabel(label)
            .WithSpinnerColor(color)
            .WithLabelColor(color);
}
