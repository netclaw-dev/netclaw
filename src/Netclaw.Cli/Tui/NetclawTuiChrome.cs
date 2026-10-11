// -----------------------------------------------------------------------
// <copyright file="NetclawTuiChrome.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal static class NetclawTuiChrome
{
    internal static ILayoutNode BuildPageFrame(string title, ILayoutNode content, Color? borderColor = null)
        => Layouts.Vertical()
            .WithChild(BuildPanel(title, content, borderColor ?? Color.Cyan).Fill());

    internal static PanelNode BuildPanel(string title, ILayoutNode content, Color borderColor)
        => new PanelNode()
            .WithTitle(title)
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(borderColor)
            .WithContent(content);

    internal static LayoutNode BuildTextInputPanel(TextInputNode input, string title)
        => BuildPanel(title, input, Color.Gray)
            .Height(3);

    /// <summary>
    /// Pre-fills a text input with the cursor at the end. Termina's <c>Text</c> setter leaves
    /// the cursor at position 0 on a fresh node, so without <c>MoveCursorToEnd</c> the first
    /// keystroke would insert in front of the default instead of after it.
    /// Every pre-filled input goes through here so they all behave the same.
    /// </summary>
    internal static void SeedTextInput(TextInputNode input, string? text)
    {
        input.Text = text ?? string.Empty;
        input.MoveCursorToEnd();
    }

    /// <summary>Longest a Provider column grows before it truncates, so Endpoint and Model ID keep their room.</summary>
    internal const int MaxProviderColumnWidth = 40;

    /// <summary>
    /// Width for a table column sized to its longest value (never narrower than the
    /// header), shrunk only when the terminal leaves no room for it.
    /// </summary>
    internal static int FitColumnWidth(IEnumerable<string> values, string header, int maxWidth)
        => Math.Min(Math.Max(header.Length, values.Select(static v => v.Length).DefaultIfEmpty(0).Max()), Math.Max(header.Length, maxWidth));

    /// <summary>Pads to <paramref name="width"/>, or truncates with an ellipsis when it cannot fit.</summary>
    internal static string FitColumn(string value, int width)
        => value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "\u2026";

    internal static ILayoutNode BuildStatusLine(string? text, Color color)
        => string.IsNullOrWhiteSpace(text)
            ? Layouts.Empty()
            : new TextNode($"  {text}").WithForeground(color);

    internal static LayoutNode BuildKeyHintLine(string text)
        => new TextNode(text)
            .WithForeground(Color.BrightBlack)
            .Height(1);
}
