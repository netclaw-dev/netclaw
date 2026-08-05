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
    public const string SetWorkingDirectory = "set_working_directory";

    public static IReadOnlyList<string> FileTools { get; } = [FileRead, FileList, FileWrite, FileEdit, AttachFile];
    public static IReadOnlyList<string> WebTools { get; } = [WebSearch, WebFetch];
    public static IReadOnlyList<string> SkillTools { get; } = [SkillManage];
    public static IReadOnlyList<string> WebhookTools { get; } = [SetWebhook, ListWebhooks, DeleteWebhook];
    public static IReadOnlyList<string> SchedulingTools { get; } = [SetReminder, ListReminders, CancelReminder, GetReminderHistory];
    public static IReadOnlyList<string> WorkingDirectoryTools { get; } = [SetWorkingDirectory];

    public static IReadOnlyList<string> PublicDefaultAllowedTools { get; } = [FileRead, FileList, AttachFile];

    public static IReadOnlyList<string> TeamDefaultAllowedTools { get; } =
    [
        .. FileTools,
        .. WebTools,
        .. SkillTools,
        .. SchedulingTools,
        .. WorkingDirectoryTools
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
    /// Creates the audience profiles that a new installation stores for the selected posture.
    /// Personal installations require approval for shell commands unless another authorization gate permits the command.
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

    public static ToolAudienceProfile GetResolvedProfile(ToolAudienceProfiles? profiles, TrustAudience audience)
    {
        var resolvedProfiles = profiles ?? CreateProfiles();
        return audience switch
        {
            TrustAudience.Public => resolvedProfiles.Public ?? CreatePublic(),
            TrustAudience.Team => resolvedProfiles.Team ?? CreateTeam(),
            TrustAudience.Personal => resolvedProfiles.Personal ?? CreatePersonal(),
            _ => CreatePublic()
        };
    }
}
