// -----------------------------------------------------------------------
// <copyright file="WorkflowViewComponents.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard.Steps;
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Workflow;

/// <summary>
/// Narrow, reusable workflow-view building blocks for short setup-oriented flows.
/// These intentionally stay presentational and do not own navigation or validation.
/// </summary>
internal static class WorkflowViewComponents
{
    internal static ILayoutNode BuildSelectionScreen(
        string heading,
        ILayoutNode selector,
        string? legend = null,
        string? supportText = null,
        Color? supportColor = null)
    {
        var layout = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {heading}").WithForeground(Color.White))
            .WithChild(selector);

        if (!string.IsNullOrWhiteSpace(legend))
        {
            layout = layout.WithChild(new TextNode($"  {legend}")
                .WithForeground(Color.Gray));
        }

        if (!string.IsNullOrWhiteSpace(supportText))
        {
            foreach (var line in SplitLines(supportText))
            {
                layout = layout.WithChild(new TextNode($"  {line}")
                    .WithForeground(supportColor ?? Color.Gray));
            }
        }

        return layout;
    }

    internal static ILayoutNode BuildEntryScreen(
        string title,
        string fieldLabel,
        TextInputNode input,
        string hint,
        string? error = null)
    {
        var layout = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {title}").WithForeground(Color.White))
            .WithChild(new TextNode($"  {fieldLabel}").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, fieldLabel))
            .WithChild(new TextNode($"  {hint}").WithForeground(Color.Gray));

        if (!string.IsNullOrWhiteSpace(error))
        {
            layout = layout.WithChild(new TextNode($"  ✗ {error}").WithForeground(Color.Red));
        }

        return layout;
    }

    internal static ILayoutNode BuildValidatingScreen(
        string heading,
        string message,
        string? supportText = null)
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {heading}").WithForeground(Color.White))
            .WithChild(new TextNode($"  {message}").WithForeground(Color.Yellow))
            .WithChild(string.IsNullOrWhiteSpace(supportText)
                ? Layouts.Empty()
                : new TextNode($"  {supportText}").WithForeground(Color.Gray));

    internal static ILayoutNode BuildSavedScreen(string successText, string nextStepText)
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {successText}").WithForeground(Color.Green))
            .WithChild(new TextNode($"  {nextStepText}").WithForeground(Color.Gray));

    internal static ILayoutNode BuildNoticeScreen(
        string title,
        IEnumerable<string> bodyLines,
        ILayoutNode confirmation,
        Color? titleColor = null)
    {
        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  {title}").WithForeground(titleColor ?? Color.Cyan))
            .WithSpacing(1);

        foreach (var line in bodyLines)
        {
            layout = layout.WithChild(new TextNode($"  {line}").WithForeground(Color.BrightBlack));
        }

        return layout
            .WithSpacing(1)
            .WithChild(confirmation);
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd());
}
