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

    internal static ILayoutNode BuildStatusLine(string? text, Color color)
        => string.IsNullOrWhiteSpace(text)
            ? Layouts.Empty()
            : new TextNode($"  {text}").WithForeground(color);

    internal static LayoutNode BuildKeyHintLine(string text)
        => new TextNode(text)
            .WithForeground(Color.BrightBlack)
            .Height(1);
}
