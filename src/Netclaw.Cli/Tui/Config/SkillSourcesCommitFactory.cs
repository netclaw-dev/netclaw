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

    public static NetclawUiCommit<string> AddRemoteUrl(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.add-remote.url",
            Label: "Server URL",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateAddRemoteUrlDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.NotApplicable(
                "Skill server probing depends on the selected authentication mode, which is collected after URL entry."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddRemoteUrlDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }

    public static NetclawUiCommit<SkillSourceAuthMode> AddRemoteAuth(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<SkillSourceAuthMode>(
            Id: "skill-sources.add-remote.auth",
            Label: "Skill server authentication",
            ReadDraft: viewModel.ReadAddRemoteAuthDraft,
            WriteDraft: viewModel.ReplaceAddRemoteAuthDraft,
            Validate: viewModel.ValidateAddRemoteAuthDraft,
            DynamicCheck: NetclawUiDynamicCheck<SkillSourceAuthMode>.Required(
                viewModel.ValidateAddRemoteAuthReachabilityAsync,
                NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddRemoteAuthDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }

    public static NetclawUiCommit<string> AddRemoteName(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.add-remote.name",
            Label: "Source name",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateAddRemoteNameDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.NotApplicable(
                "Remote skill server reachability is validated before the source name confirmation step."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddRemoteNameDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }
}
