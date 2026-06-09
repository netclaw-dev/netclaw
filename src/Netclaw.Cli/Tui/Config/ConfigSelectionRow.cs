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

    private ConfigSelectionRow(string text, bool selected, Color foreground, bool bold)
    {
        _text = text ?? string.Empty;
        _selected = selected;
        _foreground = foreground;
        _bold = bold;
        WidthConstraint = SizeConstraint.FillRemaining();
        HeightConstraint = SizeConstraint.AutoSize();
    }

    /// <summary>
    /// Build a selectable row. When <paramref name="selected"/> is true the row
    /// renders as a full-width teal bar; otherwise it renders as plain text in
    /// <paramref name="foreground"/> (defaults to white).
    /// </summary>
    internal static ConfigSelectionRow Create(string text, bool selected, Color? foreground = null, bool bold = false)
        => new(text, selected, foreground ?? Color.White, bold);

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
            ctx.SetForeground(_foreground);
            if (_bold)
                ctx.SetDecoration(TextDecoration.Bold);
            ctx.WriteAt(0, 0, Clip(_text, bounds.Width));
            if (_bold)
                ctx.SetDecoration(TextDecoration.None);
        }

        ctx.ResetColors();
    }

    private static string Clip(string text, int width)
        => text.Length > width ? text[..width] : text;
}
