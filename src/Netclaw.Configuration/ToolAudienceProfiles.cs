// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfiles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Media;

namespace Netclaw.Configuration;

public enum ToolProfileMode
{
    Allowlist,
    All
}

public enum ToolFilesystemMode
{
    None,
    Roots,
    All
}

public sealed class ToolFilesystemAccessProfile
{
    public ToolFilesystemMode Mode { get; set; } = ToolFilesystemMode.None;
    public List<string> Roots { get; set; } = [];
}

public sealed class ToolAudienceProfile
{
    public ToolProfileMode ToolsMode { get; set; } = ToolProfileMode.Allowlist;
    public List<string> AllowedTools { get; set; } = [];
    public ToolProfileMode McpServersMode { get; set; } = ToolProfileMode.Allowlist;
    public List<string> AllowedMcpServers { get; set; } = [];

    /// <summary>
    /// Per-server tool allowlists for this audience.
    /// When a server appears here, only listed tools are exposed to this audience.
    /// Servers not listed expose all their registered tools (subject to AllowedMcpServers gate).
    /// Null means no per-tool filtering.
    /// </summary>
    public Dictionary<string, List<string>>? McpServerToolGrants { get; set; }

    public ToolFilesystemAccessProfile ReadFiles { get; set; } = new();
    public ToolFilesystemAccessProfile WriteFiles { get; set; } = new();
    public ToolFilesystemAccessProfile AttachFiles { get; set; } = new();

    /// <summary>
    /// Per-audience approval gate configuration. When set, tools listed in
    /// <see cref="ToolApprovalConfig.ToolOverrides"/> require interactive user
    /// approval before execution. Null means no approval gates (all tools auto-approved).
    /// </summary>
    public ToolApprovalConfig? ApprovalPolicy { get; set; }

    /// <summary>
    /// Per-audience inbound channel attachment policy. Channel adapters read
    /// this from the resolved profile to decide which attachment classes are
    /// accepted, the per-file size cap, and the per-message file-count cap.
    /// Defaults to <see cref="ChannelAttachmentPolicy.Empty"/> (fail-closed:
    /// nothing allowed) so an unconfigured profile rejects every attachment
    /// until the operator opts in via the audience defaults.
    /// </summary>
    public ChannelAttachmentPolicy ChannelAttachments { get; set; } = ChannelAttachmentPolicy.Empty;
}

public sealed class ToolAudienceProfiles
{
    public ToolAudienceProfile Public { get; set; } = ToolAudienceProfileDefaults.CreatePublic();
    public ToolAudienceProfile Team { get; set; } = ToolAudienceProfileDefaults.CreateTeam();
    public ToolAudienceProfile Personal { get; set; } = ToolAudienceProfileDefaults.CreatePersonal();

    /// <summary>
    /// Returns all audience profiles (Public, Team, Personal) for enumeration.
    /// Use this instead of manually constructing arrays to avoid missing a tier.
    /// </summary>
    public IEnumerable<ToolAudienceProfile> GetAllProfiles()
    {
        yield return Public;
        yield return Team;
        yield return Personal;
    }

    /// <summary>
    /// Validates per-audience channel attachment policy. A policy that
    /// permits any category SHALL specify positive size and file-count caps,
    /// otherwise an allowed category cannot be delivered (a silent
    /// misconfiguration that this check converts into a loud startup error).
    /// A policy with no allowed categories is valid with any cap (it is
    /// already fail-closed).
    /// </summary>
    public IReadOnlyList<string> ValidateChannelAttachments()
    {
        var errors = new List<string>();
        ValidateProfile("Public", Public, errors);
        ValidateProfile("Team", Team, errors);
        ValidateProfile("Personal", Personal, errors);
        return errors;
    }

    private static void ValidateProfile(string name, ToolAudienceProfile profile, List<string> errors)
    {
        var policy = profile.ChannelAttachments;
        if (policy is null)
            return;

        if (policy.AllowedCategories.Count == 0)
            return;

        if (policy.MaxFileBytes <= 0)
            errors.Add($"Tools.AudienceProfiles.{name}.ChannelAttachments.MaxFileBytes must be > 0 when AllowedCategories is not empty.");

        if (policy.MaxFilesPerMessage <= 0)
            errors.Add($"Tools.AudienceProfiles.{name}.ChannelAttachments.MaxFilesPerMessage must be > 0 when AllowedCategories is not empty.");
    }

    /// <summary>
    /// Filesystem roots that are always readable regardless of audience profile.
    /// Supports tokens: <c>{skills_dir}</c>, <c>{identity_dir}</c>, <c>{workspaces_dir}</c>.
    /// Defaults to skills, identity, and workspaces directories so skill loading,
    /// identity file reads, and project discovery work even under Team/Public audiences.
    /// </summary>
    public List<string> GlobalReadRoots { get; set; } =
    [
        ToolAudienceProfileDefaults.SkillsDirectoryToken,
        ToolAudienceProfileDefaults.IdentityDirectoryToken,
        ToolAudienceProfileDefaults.WorkspacesDirectoryToken
    ];
}

public static class ToolAudienceProfileToolCatalog
{
    public const string ShellExecute = "shell_execute";
    public const string FileRead = "file_read";
    public const string FileList = "file_list";
    public const string FileSearch = "file_search";
    public const string ToolOutputRead = "tool_output_read";
    public const string AttachFile = "attach_file";
    public const string FileWrite = "file_write";
    public const string FileEdit = "file_edit";
    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string SkillManage = "skill_manage";
    public const string SetWebhook = "set_webhook";
    public const string ListWebhooks = "list_webhooks";
    public const string DeleteWebhook = "delete_webhook";
    public const string SetReminder = "set_reminder";
    public const string ListReminders = "list_reminders";
    public const string CancelReminder = "cancel_reminder";
    public const string GetReminderHistory = "get_reminder_history";
    public const string RunReminder = "run_reminder";
    public const string SetWorkingDirectory = "set_working_directory";

    public static IReadOnlyList<string> FileTools { get; } =
        [FileRead, FileList, FileSearch, ToolOutputRead, FileWrite, FileEdit, AttachFile];
    public static IReadOnlyList<string> WebTools { get; } = [WebSearch, WebFetch];
    public static IReadOnlyList<string> SkillTools { get; } = [SkillManage];
    public static IReadOnlyList<string> WebhookTools { get; } = [SetWebhook, ListWebhooks, DeleteWebhook];
    public static IReadOnlyList<string> SchedulingTools { get; } =
        [SetReminder, ListReminders, CancelReminder, GetReminderHistory, RunReminder];
    public static IReadOnlyList<string> WorkingDirectoryTools { get; } = [SetWorkingDirectory];

    public static IReadOnlyList<string> PublicDefaultAllowedTools { get; } =
        [FileRead, FileList, FileSearch, ToolOutputRead, AttachFile];

    public static IReadOnlyList<string> TeamDefaultAllowedTools { get; } =
    [
        .. FileTools,
        .. WebTools,
        .. SkillTools,
        .. SchedulingTools,
        .. WorkingDirectoryTools
    ];

    // Policy data: every Public and Team default AllowedTools list that a release shipped.
    // `netclaw init` wrote the complete default list to netclaw.json. The old configuration
    // binder added configured items to the current defaults, so those installs ran with the
    // current default tools. With list replacement, the daemon maps an exact older list to the
    // current default (ToolConfig.BindFromConfiguration). `netclaw doctor --fix` deletes a key
    // that exactly matches any row, so that the audience follows the default in later releases.
    // The last row of each table is the current default. ToolConfigBindingTests fails when the
    // current default is not equal to the last row, so a change to a default needs a new row.
    //   0.8.0 to 0.19.0: a800e56e2 (#249).
    //   0.20.0 to 0.25.4, and 0.26.0-beta.1 to 0.26.0-beta.5: 980eab0d6 (#1111).
    //   0.26.0 to 0.27.1-beta.1: cfd528d5b (#2037), ecf70fc5d (#2038), 8bfe958b5 (#2045).
    //   The intermediate file_read_many and json_read lists never shipped in a release tag.
    // The tables are closed. Directory.Build.props has VersionPrefix 0.27.1 for this change: from
    // the first 0.27.1 build after 0.27.1-beta.1, `netclaw init` stops writing lists. It writes
    // only the posture and no audience profiles, so init cannot store a copy of a later default.
    public static IReadOnlyList<IReadOnlyList<string>> LegacyPublicDefaultAllowedTools { get; } =
    [
        [FileRead, FileWrite, AttachFile],
        [FileRead, FileList, AttachFile],
        [FileRead, FileList, FileSearch, ToolOutputRead, AttachFile]
    ];

    public static IReadOnlyList<IReadOnlyList<string>> LegacyTeamDefaultAllowedTools { get; } =
    [
        [FileRead, AttachFile],
        [
            FileRead, FileList, FileWrite, FileEdit, AttachFile,
            WebSearch, WebFetch, SkillManage,
            SetReminder, ListReminders, CancelReminder, GetReminderHistory,
            SetWorkingDirectory
        ],
        [
            FileRead, FileList, FileSearch, ToolOutputRead, FileWrite, FileEdit, AttachFile,
            WebSearch, WebFetch, SkillManage,
            SetReminder, ListReminders, CancelReminder, GetReminderHistory,
            SetWorkingDirectory
        ],
        // Current default only: init no longer writes lists. run_reminder joined the default.
        [
            FileRead, FileList, FileSearch, ToolOutputRead, FileWrite, FileEdit, AttachFile,
            WebSearch, WebFetch, SkillManage,
            SetReminder, ListReminders, CancelReminder, GetReminderHistory, RunReminder,
            SetWorkingDirectory
        ]
    ];

    public static IReadOnlyList<string> ProfileManagedTools { get; } =
    [
        .. TeamDefaultAllowedTools,
        .. WebhookTools,
        ShellExecute
    ];

    private static readonly HashSet<string> ProfileManagedToolSet = new(ProfileManagedTools, StringComparer.Ordinal);

    public static bool IsProfileManaged(string toolName) => ProfileManagedToolSet.Contains(toolName);
}

public static class ToolAudienceProfileDefaults
{
    public const string SessionDirectoryToken = "{session_dir}";
    public const string SkillsDirectoryToken = "{skills_dir}";
    public const string IdentityDirectoryToken = "{identity_dir}";
    public const string WorkspacesDirectoryToken = "{workspaces_dir}";

    public static ToolAudienceProfiles CreateProfiles() => new()
    {
        Public = CreatePublic(),
        Team = CreateTeam(),
        Personal = CreatePersonal(),
        GlobalReadRoots = [SkillsDirectoryToken, IdentityDirectoryToken, WorkspacesDirectoryToken]
    };

    /// <summary>
    /// Creates the default audience profiles for a posture. The daemon binds the <c>Tools</c>
    /// section on top of these profiles, so netclaw.json does not store them. Personal
    /// installations require approval for shell commands unless another authorization gate
    /// permits the command.
    /// </summary>
    public static ToolAudienceProfiles CreateProfilesForPosture(DeploymentPosture posture)
    {
        var profiles = CreateProfiles();
        if (posture == DeploymentPosture.Personal)
        {
            profiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    [ToolAudienceProfileToolCatalog.ShellExecute] = ToolApprovalMode.Approval
                }
            };
        }

        return profiles;
    }

    // Audience tool grants are monotonic: Public ⊆ Team ⊆ Personal. Public is
    // the least-trusted, fail-closed audience — read, enumerate, and attach
    // only: no file-mutation tools and no outbound web tools (web_search /
    // web_fetch). WriteFiles stays session-scoped so an operator who
    // deliberately re-grants Public a write tool still gets the safe
    // session-directory scope rather than an unusable profile.
    public static ToolAudienceProfile CreatePublic() => new()
    {
        AllowedTools = [.. ToolAudienceProfileToolCatalog.PublicDefaultAllowedTools],
        ReadFiles = CreateSessionScopedFilesystemAccess(),
        WriteFiles = CreateSessionScopedFilesystemAccess(),
        AttachFiles = CreateSessionScopedFilesystemAccess(),
        ChannelAttachments = CreatePublicChannelAttachments()
    };

    // Team is operator-vetted: every profile-managed tool except shell
    // (Personal-only via the shell_requires_personal_context hard gate) and
    // the webhook tools. MCP stays operator-opt-in (AllowedMcpServers empty).
    // Monotonic invariant: Public ⊆ Team ⊆ Personal.
    public static ToolAudienceProfile CreateTeam() => new()
    {
        AllowedTools = [.. ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools],
        ReadFiles = CreateSessionScopedFilesystemAccess(),
        WriteFiles = CreateSessionScopedFilesystemAccess(),
        AttachFiles = CreateSessionScopedFilesystemAccess(),
        ChannelAttachments = CreateTeamChannelAttachments()
    };

    public static ToolAudienceProfile CreatePersonal() => new()
    {
        ToolsMode = ToolProfileMode.All,
        McpServersMode = ToolProfileMode.All,
        ReadFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
        WriteFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
        AttachFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
        ChannelAttachments = CreatePersonalChannelAttachments()
    };

    public static ChannelAttachmentPolicy CreatePublicChannelAttachments() => new()
    {
        AllowedCategories = [AttachmentCategory.Image],
        MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
        MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
    };

    public static ChannelAttachmentPolicy CreateTeamChannelAttachments() => new()
    {
        AllowedCategories =
        [
            AttachmentCategory.Image,
            AttachmentCategory.Pdf,
            AttachmentCategory.Document,
            AttachmentCategory.Archive,
            AttachmentCategory.Media
        ],
        MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
        MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
    };

    public static ChannelAttachmentPolicy CreatePersonalChannelAttachments() => new()
    {
        AllowedCategories =
        [
            AttachmentCategory.Image,
            AttachmentCategory.Pdf,
            AttachmentCategory.Document,
            AttachmentCategory.Archive,
            AttachmentCategory.Media,
            AttachmentCategory.Other
        ],
        MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
        MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
    };

    public static ToolFilesystemAccessProfile CreateSessionScopedFilesystemAccess() => new()
    {
        Mode = ToolFilesystemMode.Roots,
        Roots = [SessionDirectoryToken]
    };

    /// <summary>
    /// Returns true when <paramref name="allowedTools"/> is exactly a default list that a release
    /// shipped for <paramref name="audience"/>, which includes the current default: the same
    /// tools in any order, with no extra, missing, or repeated tool. Tool names are
    /// case-sensitive. Only Public and Team have shipped lists.
    /// </summary>
    public static bool IsLegacyDefaultAllowedTools(TrustAudience audience, IReadOnlyCollection<string> allowedTools)
    {
        var legacyLists = audience switch
        {
            TrustAudience.Public => ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools,
            TrustAudience.Team => ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools,
            _ => []
        };

        return legacyLists.Any(legacy => SameTools(legacy, allowedTools));
    }

    /// <summary>
    /// Returns true when <paramref name="allowedTools"/> is exactly the current default list for
    /// Public or Team, with the same match rules as <see cref="IsLegacyDefaultAllowedTools"/>.
    /// </summary>
    public static bool IsCurrentDefaultAllowedTools(TrustAudience audience, IReadOnlyCollection<string> allowedTools)
        => SameTools(CurrentDefaultAllowedTools(audience), allowedTools);

    private static bool SameTools(IReadOnlyCollection<string> expected, IReadOnlyCollection<string> actual)
        => expected.Count == actual.Count
            && new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual);

    /// <summary>
    /// Describes a shipped default AllowedTools list for the daemon startup log and for
    /// <c>netclaw doctor</c>: the audience, the tool changes, and the fix command.
    /// </summary>
    public static string DescribeLegacyDefaultAllowedTools(TrustAudience audience, IReadOnlyCollection<string> allowedTools)
    {
        var fix = $"Run `netclaw doctor --fix` to remove the key, so that {audience} follows the default.";
        if (IsCurrentDefaultAllowedTools(audience, allowedTools))
        {
            return $"Tools.AudienceProfiles.{audience}.AllowedTools is a copy of the current Netclaw default list. "
                + "A copy does not get the tools that later releases add to the default. "
                + fix;
        }

        var current = CurrentDefaultAllowedTools(audience);
        var added = current.Except(allowedTools, StringComparer.Ordinal).ToArray();
        var removed = allowedTools.Except(current, StringComparer.Ordinal).ToArray();
        return $"Tools.AudienceProfiles.{audience}.AllowedTools is an older Netclaw default list. "
            + $"The daemon applies the current {audience} default, which adds {JoinOrNone(added)} "
            + $"and removes {JoinOrNone(removed)}. "
            + fix;
    }

    private static string JoinOrNone(IReadOnlyCollection<string> tools)
        => tools.Count == 0 ? "no tools" : string.Join(", ", tools);

    /// <summary>
    /// Returns the current default AllowedTools list for Public or Team.
    /// </summary>
    public static IReadOnlyList<string> CurrentDefaultAllowedTools(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Public => ToolAudienceProfileToolCatalog.PublicDefaultAllowedTools,
            TrustAudience.Team => ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools,
            _ => throw new ArgumentOutOfRangeException(
                nameof(audience), audience, "Only Public and Team have a default AllowedTools list.")
        };

    public static ToolAudienceProfile GetResolvedProfile(ToolAudienceProfiles? profiles, TrustAudience audience)
    {
        var resolvedProfiles = profiles ?? CreateProfiles();
        return audience switch
        {
            TrustAudience.Public => resolvedProfiles.Public ?? CreatePublic(),
            TrustAudience.Team => resolvedProfiles.Team ?? CreateTeam(),
            TrustAudience.Personal => resolvedProfiles.Personal ?? CreatePersonal(),
            // An undefined audience value is an invariant violation. Do not
            // hand it the Public profile.
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Undefined trust audience.")
        };
    }
}
