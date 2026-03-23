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
    public ToolFilesystemAccessProfile ReadFiles { get; set; } = new();
    public ToolFilesystemAccessProfile WriteFiles { get; set; } = new();
    public ToolFilesystemAccessProfile AttachFiles { get; set; } = new();
}

public sealed class ToolAudienceProfiles
{
    public ToolAudienceProfile Public { get; set; } = ToolAudienceProfileDefaults.CreatePublic();
    public ToolAudienceProfile Team { get; set; } = ToolAudienceProfileDefaults.CreateTeam();
    public ToolAudienceProfile Personal { get; set; } = ToolAudienceProfileDefaults.CreatePersonal();

    /// <summary>
    /// Filesystem roots that are always readable regardless of audience profile.
    /// Supports tokens: <c>{skills_dir}</c>, <c>{identity_dir}</c>.
    /// Defaults to skills and identity directories so skill loading and identity
    /// file reads work even under Team/Public audiences.
    /// </summary>
    public List<string> GlobalReadRoots { get; set; } =
    [
        ToolAudienceProfileDefaults.SkillsDirectoryToken,
        ToolAudienceProfileDefaults.IdentityDirectoryToken
    ];
}

public static class ToolAudienceProfileDefaults
{
    public const string SessionDirectoryToken = "{session_dir}";
    public const string SkillsDirectoryToken = "{skills_dir}";
    public const string IdentityDirectoryToken = "{identity_dir}";

    public static ToolAudienceProfiles CreateProfiles() => new()
    {
        Public = CreatePublic(),
        Team = CreateTeam(),
        Personal = CreatePersonal(),
        GlobalReadRoots = [SkillsDirectoryToken, IdentityDirectoryToken]
    };

    public static ToolAudienceProfile CreatePublic() => new()
    {
        AllowedTools = ["file_read", "file_write", "attach_file"],
        ReadFiles = CreateSessionScopedFilesystemAccess(),
        WriteFiles = CreateSessionScopedFilesystemAccess(),
        AttachFiles = CreateSessionScopedFilesystemAccess()
    };

    public static ToolAudienceProfile CreateTeam() => new()
    {
        AllowedTools = ["file_read", "attach_file"],
        ReadFiles = CreateSessionScopedFilesystemAccess(),
        WriteFiles = CreateSessionScopedFilesystemAccess(),
        AttachFiles = CreateSessionScopedFilesystemAccess()
    };

    public static ToolAudienceProfile CreatePersonal() => new()
    {
        ToolsMode = ToolProfileMode.All,
        McpServersMode = ToolProfileMode.All,
        ReadFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
        WriteFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
        AttachFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All }
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
