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

    public static NetclawUiCommit<bool> AddLocalSymlinks(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<bool>(
            Id: "skill-sources.add-local.symlinks",
            Label: "Local folder symlink policy",
            ReadDraft: viewModel.ReadAddLocalSymlinksDraft,
            WriteDraft: viewModel.ReplaceAddLocalSymlinksDraft,
            Validate: viewModel.ValidateAddLocalSymlinksDraft,
            DynamicCheck: NetclawUiDynamicCheck<bool>.NotApplicable(
                "Symlink policy selection only records pending local scan policy; local folder scanning validates the policy after source creation."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddLocalSymlinksDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }

    public static NetclawUiCommit<string> AddLocalName(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.add-local.name",
            Label: "Source name",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateAddLocalNameDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.NotApplicable(
                "Local source name validation is structural; runtime local skill scanning consumes the already validated folder path."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddLocalNameDraft(draft);
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

    public static NetclawUiCommit<string> AddRemoteToken(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.add-remote.token",
            Label: "Bearer token",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateAddRemoteTokenDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.Required(
                viewModel.ValidateAddRemoteTokenReachabilityAsync,
                NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitAddRemoteTokenDraft(draft);
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

    public static NetclawUiCommit<string> RenameSource(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.source.rename",
            Label: "Source name",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateRenameSourceDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.NotApplicable(
                "Source rename changes only the config display key; path/feed runtime validation is unchanged."),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitRenameSourceDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }

    public static NetclawUiCommit<string> ChangeLocation(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return new NetclawUiCommit<string>(
            Id: "skill-sources.source.location",
            Label: "Location",
            ReadDraft: () => viewModel.Draft.Value,
            WriteDraft: viewModel.ReplaceDraft,
            Validate: viewModel.ValidateChangeLocationDraft,
            DynamicCheck: NetclawUiDynamicCheck<string>.Required(
                viewModel.ValidateChangeLocationReachabilityAsync,
                NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: (draft, _) =>
            {
                viewModel.CommitChangeLocationDraft(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
    }

    public static NetclawUiCommit<SkillSourceActionTarget?> ToggleEnabled(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return SourceActionCommit(
            viewModel,
            "skill-sources.source.toggle-enabled",
            "Source enabled state",
            viewModel.ValidateSourceActionTarget,
            viewModel.CommitToggleEnabled,
            "Enabled-state toggles only flip an existing persisted source flag; runtime scanners/feed sync consume the saved source shape unchanged.");
    }

    public static NetclawUiCommit<SkillSourceActionTarget?> ToggleLocalSymlinks(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return SourceActionCommit(
            viewModel,
            "skill-sources.local.toggle-symlinks",
            "Local folder symlink policy",
            viewModel.ValidateLocalSourceActionTarget,
            viewModel.CommitToggleLocalSymlinks,
            "Symlink toggles only flip the existing local source scan policy; runtime scanner validates file traversal on scan.");
    }

    public static NetclawUiCommit<SkillSourceActionTarget?> CycleRemoteSyncInterval(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return SourceActionCommit(
            viewModel,
            "skill-sources.remote.cycle-timeout",
            "Skill server HTTP timeout",
            viewModel.ValidateRemoteSourceActionTarget,
            viewModel.CommitCycleRemoteSyncInterval,
            "Timeout cycling chooses from fixed valid runtime timeout values, so no external probe is required.");
    }

    public static NetclawUiCommit<SkillSourceActionTarget?> RemoveRemoteToken(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return SourceActionCommit(
            viewModel,
            "skill-sources.remote.remove-token",
            "Skill server token",
            viewModel.ValidateRemoteSourceActionTarget,
            viewModel.CommitRemoveRemoteToken,
            "Token removal deletes an existing secret reference and does not depend on remote server reachability.");
    }

    public static NetclawUiCommit<SkillSourceActionTarget?> RemoveSource(SkillSourcesConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return SourceActionCommit(
            viewModel,
            "skill-sources.source.remove",
            "Skill source",
            viewModel.ValidateSourceActionTarget,
            viewModel.CommitRemoveSource,
            "Source removal deletes the selected config entry after explicit user confirmation; no runtime probe is required.");
    }

    private static NetclawUiCommit<SkillSourceActionTarget?> SourceActionCommit(
        SkillSourcesConfigViewModel viewModel,
        string id,
        string label,
        Func<SkillSourceActionTarget?, NetclawUiValidationResult> validate,
        Action<SkillSourceActionTarget?> persist,
        string dynamicJustification)
        => new(
            Id: id,
            Label: label,
            ReadDraft: viewModel.ReadCurrentSourceActionTarget,
            WriteDraft: _ => { },
            Validate: validate,
            DynamicCheck: NetclawUiDynamicCheck<SkillSourceActionTarget?>.NotApplicable(dynamicJustification),
            PersistAsync: (draft, _) =>
            {
                persist(draft);
                return ValueTask.CompletedTask;
            },
            AfterCommit: viewModel.ApplyCommitResult);
}
