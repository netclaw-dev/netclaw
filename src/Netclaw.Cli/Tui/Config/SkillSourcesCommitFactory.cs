// -----------------------------------------------------------------------
// <copyright file="SkillSourcesCommitFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Config;

internal static class SkillSourcesCommitFactory
{
    public static NetclawUiCommit<string> AddLocalPath(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.add-local.path",
            Label: "Folder path",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateAddLocalPathDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.NotApplicable(
                "Local folder existence and URL rejection are static filesystem validation; runtime skill scanning runs after source creation."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddLocalPathDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }
}
