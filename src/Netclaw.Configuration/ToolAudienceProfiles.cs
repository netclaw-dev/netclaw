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

    public static ToolAudienceProfile CreatePublic() => new()
    {
        AllowedTools = ["file_read", "file_write", "attach_file"],
        ReadFiles = CreateSessionScopedFilesystemAccess(),
        WriteFiles = CreateSessionScopedFilesystemAccess(),
        AttachFiles = CreateSessionScopedFilesystemAccess(),
        ChannelAttachments = CreatePublicChannelAttachments()
    };

    public static ToolAudienceProfile CreateTeam() => new()
    {
        AllowedTools = ["file_read", "attach_file"],
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
