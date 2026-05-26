// -----------------------------------------------------------------------
// <copyright file="WizardStepHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Netclaw.Cli.Tui;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

internal static class WizardStepHelpers
{
    internal static (SelectionListNode<SelectionOption<bool>> List, ILayoutNode Layout) BuildUserAccessChoiceSubStep(
        Action<bool> setRestrict, StepViewCallbacks callbacks)
    {
        var restrictOption = new SelectionOption<bool>(true, "Restrict to specific users (recommended)");
        var allowOption = new SelectionOption<bool>(false, "Allow anyone in allowed channels");

        var list = Layouts.SelectionList<SelectionOption<bool>>(
                [restrictOption, allowOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        list.OnFocused();

        list.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                setRestrict(selected[0].Value);
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Who can interact with the bot?").WithForeground(Color.White))
            .WithChild(list);

        return (list, layout);
    }

    internal static ILayoutNode BuildTextInputPanel(TextInputNode input, string title)
        => NetclawTuiChrome.BuildTextInputPanel(input, title);

    internal static List<string> ParseUserIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)];
}

internal sealed record SelectionOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
