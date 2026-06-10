// -----------------------------------------------------------------------
// <copyright file="ConfigSelectionRow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// A single config-page list row that renders the selected entry as a
/// full-width teal highlight bar (teal background, dark foreground) — the same
/// look <see cref="SelectionListNode{T}"/> gives the dashboard — instead of a
/// <c>▶</c>/<c>&gt;</c> marker prefix. This unifies the selection style across
/// the bespoke config sub-pages, which render their rows as manual nodes rather
/// than through a <see cref="SelectionListNode{T}"/>.
/// </summary>
/// <remarks>
/// The bar is drawn by filling the row's full bounds width with the highlight
/// background before writing the label, mirroring
/// <c>SelectionListNode.RenderItemLine</c>. A <see cref="TextNode"/> alone would
/// only colour the glyph cells of the text, so a manual fill is required to get
/// an edge-to-edge bar at the runtime panel width.
/// </remarks>
internal sealed class ConfigSelectionRow : LayoutNode
{
    internal static readonly Color BarBackground = Color.Cyan;
    internal static readonly Color BarForeground = Color.Black;

    private readonly string _text;
    private readonly bool _selected;
    private readonly Color _foreground;
    private readonly bool _bold;
    private readonly int _valueStart;
    private readonly Color _valueForeground;

    private ConfigSelectionRow(string text, bool selected, Color foreground, bool bold, int valueStart, Color valueForeground)
    {
        _text = text ?? string.Empty;
        _selected = selected;
        _foreground = foreground;
        _bold = bold;
        _valueStart = valueStart;
        _valueForeground = valueForeground;
        WidthConstraint = SizeConstraint.FillRemaining();
        HeightConstraint = SizeConstraint.AutoSize();
    }

    /// <summary>
    /// Build a selectable row. When <paramref name="selected"/> is true the row
    /// renders as a full-width teal bar; otherwise it renders as plain text in
    /// <paramref name="foreground"/> (defaults to white).
    /// </summary>
    internal static ConfigSelectionRow Create(string text, bool selected, Color? foreground = null, bool bold = false)
        => new(text, selected, foreground ?? Color.White, bold, valueStart: -1, valueForeground: Color.White);

    /// <summary>
    /// Build a form-field row whose trailing <paramref name="value"/> segment renders
    /// in its own colour when the row is not selected — e.g. a dim placeholder/example
    /// that must read as a prompt rather than an entered value, while the bright
    /// <paramref name="label"/> stays legible. When selected the whole row uses the
    /// teal bar so the focus look stays consistent with menu rows.
    /// </summary>
    internal static ConfigSelectionRow CreateLabeled(string label, string value, bool selected, Color valueForeground, Color? labelForeground = null)
    {
        label ??= string.Empty;
        return new(label + (value ?? string.Empty), selected, labelForeground ?? Color.White, bold: false, valueStart: label.Length, valueForeground: valueForeground);
    }

    public override Size Measure(Size available)
    {
        var width = WidthConstraint.Compute(available.Width, _text.Length, available.Width);
        return new Size(width, 1);
    }

    public override void Render(IRenderContext context, Rect bounds)
    {
        if (!bounds.HasArea)
            return;

        var ctx = context.CreateSubContext(bounds);
        if (_selected)
        {
            ctx.SetBackground(BarBackground);
            ctx.Fill(0, 0, bounds.Width, 1);
            ctx.SetForeground(BarForeground);
            if (_bold)
                ctx.SetDecoration(TextDecoration.Bold);
            ctx.WriteAt(0, 0, Clip(_text, bounds.Width));
            if (_bold)
                ctx.SetDecoration(TextDecoration.None);
        }
        else
        {
            if (_bold)
                ctx.SetDecoration(TextDecoration.Bold);

            var clipped = Clip(_text, bounds.Width);
            if (_valueStart >= 0 && _valueStart <= clipped.Length)
            {
                // Two-tone: bright label, then the value segment in its own colour
                // (a dim placeholder reads as a prompt; a real value reads as bright).
                ctx.SetForeground(_foreground);
                ctx.WriteAt(0, 0, clipped[.._valueStart]);
                ctx.SetForeground(_valueForeground);
                ctx.WriteAt(_valueStart, 0, clipped[_valueStart..]);
            }
            else
            {
                ctx.SetForeground(_foreground);
                ctx.WriteAt(0, 0, clipped);
            }

            if (_bold)
                ctx.SetDecoration(TextDecoration.None);
        }

        ctx.ResetColors();
    }

    private static string Clip(string text, int width)
        => text.Length > width ? text[..width] : text;
}
