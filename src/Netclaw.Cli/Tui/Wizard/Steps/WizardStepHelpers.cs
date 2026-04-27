using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

internal static class WizardStepHelpers
{
    internal static (SelectionListNode<string> List, ILayoutNode Layout) BuildUserAccessChoiceSubStep(
        Action<bool> setRestrict, StepViewCallbacks callbacks)
    {
        var restrictLabel = "Restrict to specific users (recommended)";
        var allowLabel = "Allow anyone in allowed channels";

        var list = Layouts.SelectionList(restrictLabel, allowLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        list.OnFocused();

        list.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                setRestrict(selected[0] == restrictLabel);
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Who can interact with the bot?").WithForeground(Color.White))
            .WithChild(list);

        return (list, layout);
    }

    internal static List<string> ParseUserIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
}
