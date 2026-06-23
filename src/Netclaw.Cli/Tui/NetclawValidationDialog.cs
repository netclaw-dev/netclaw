// -----------------------------------------------------------------------
// <copyright file="NetclawValidationDialog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal enum NetclawValidationDialogAction
{
    RetryValidation,
    BackToEdit,
    SaveAnyway,
}

internal sealed record NetclawValidationDialogModel(string Title, string Intro, string Message);

internal static class NetclawValidationDialogViews
{
    private const string RetryLabel = "Retry validation";
    private const string BackToEditLabel = "Back to edit";
    private const string SaveAnywayLabel = "Save anyway";

    public static SelectionListNode<string> BuildActionList()
    {
        var list = Layouts.SelectionList(new List<string>
            {
                RetryLabel,
                BackToEditLabel,
                SaveAnywayLabel,
            })
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        list.OnFocused();
        return list;
    }

    public static ILayoutNode BuildWarningPanel(NetclawValidationDialogModel model, SelectionListNode<string> actionList)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(actionList);

        return NetclawTuiChrome.BuildPanel(
            model.Title,
            Layouts.Vertical()
                .WithSpacing(1)
                .WithChild(new TextNode($"  {model.Intro}").WithForeground(Color.White))
                .WithChild(new TextNode($"  {model.Message}").WithForeground(Color.Yellow))
                .WithChild(actionList),
            Color.Yellow);
    }

    public static NetclawValidationDialogAction ParseAction(string label)
        => label switch
        {
            RetryLabel => NetclawValidationDialogAction.RetryValidation,
            BackToEditLabel => NetclawValidationDialogAction.BackToEdit,
            SaveAnywayLabel => NetclawValidationDialogAction.SaveAnyway,
            _ => throw new InvalidOperationException($"Unknown validation dialog action '{label}'."),
        };
}
