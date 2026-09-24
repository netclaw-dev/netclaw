// -----------------------------------------------------------------------
// <copyright file="SkillFeedsConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for private skill server feeds. Organizations can host
/// skill-server instances and have Netclaw daemons automatically discover
/// and sync skills at startup and periodically thereafter.
/// </summary>
public sealed class SkillFeedsConfig
{
    /// <summary>
    /// Ordered list of skill server feeds. Precedence follows list order —
    /// earlier feeds win on name collisions. Native Netclaw skills and
    /// system skills always take highest precedence regardless of order.
    /// </summary>
    public List<SkillFeedSource> Feeds { get; set; } = [];

    /// <summary>Managed plugin packages from public GitHub repositories.</summary>
    public List<ManagedPluginSource> Plugins { get; set; } = [];

    /// <summary>
    /// How often (in minutes) to re-check feeds for updated skills.
    /// Default: 60 (once per hour). Set to 0 to disable periodic sync
    /// and only sync at daemon startup.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;
}

/// <summary>The type of Git reference that a managed plugin follows.</summary>
public enum ManagedPluginReferenceKind
{
    Branch,
    Commit,
}

/// <summary>A managed plugin source from a public GitHub repository.</summary>
public sealed class ManagedPluginSource
{
    public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Format { get; set; } = ManagedPluginSourceValidator.AutoFormat;
    public string? Subdirectory { get; set; }
    public ManagedPluginReferenceKind ReferenceKind { get; set; }
    public string Reference { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Validates and canonicalizes managed GitHub plugin source data.</summary>
public static class ManagedPluginSourceValidator
{
    public const string AgentPluginFormat = "agent-plugin";
    public const string AutoFormat = "auto";
    public const string CodexFormat = "codex";
    public const int MaximumSourceCount = 20;
    private static readonly char[] WindowsInvalidPathCharacters = ['<', '>', ':', '"', '|', '?', '*'];
    private static readonly HashSet<string> WindowsReservedPathNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    public static bool TryValidateSources(IReadOnlyList<ManagedPluginSource> sources, out string error)
    {
        error = "";
        if (sources.Count > MaximumSourceCount)
        {
            error = $"No more than {MaximumSourceCount} GitHub plugins can be configured.";
            return false;
        }
        var duplicate = sources.GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            error = $"Plugin source ID '{duplicate.Key}' occurs more than once.";
            return false;
        }
        foreach (var source in sources)
        {
            if (!TryValidateSource(source, out error))
                return false;
        }
        return true;
    }

    public static bool TryNormalizeRepository(string value, out string repository, out string error)
    {
        repository = "";
        error = "";
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
        {
            error = "The repository must be GitHub owner/repository shorthand or a canonical GitHub HTTPS URL.";
            return false;
        }

        var candidate = value.Trim();
        if (TryParseRepositoryParts(candidate, out var owner, out var name))
        {
            repository = $"{owner}/{name}";
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The repository URL must be canonical GitHub HTTPS without credentials, a port, a query, or a fragment.";
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (!TryParseRepositoryParts(path, out owner, out name))
        {
            error = "The GitHub URL must contain one owner and one repository.";
            return false;
        }

        repository = $"{owner}/{name}";
        return true;
    }

    public static bool TryValidateSource(ManagedPluginSource source, out string error)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!TryValidateId(source.Id, out error))
            return false;
        if (!TryNormalizeRepository(source.Repository, out var repository, out error)
            || !string.Equals(repository, source.Repository, StringComparison.Ordinal))
        {
            error = error.Length > 0 ? error : "The repository is not canonical owner/repository form.";
            return false;
        }
        if (source.Format is not AutoFormat and not AgentPluginFormat and not CodexFormat)
        {
            error = "The plugin format must be 'auto', 'agent-plugin', or 'codex'.";
            return false;
        }
        if (!TryNormalizeRelativePath(source.Subdirectory, allowEmpty: true, out var subdirectory, out error))
            return false;
        if (!string.Equals(source.Subdirectory, subdirectory, StringComparison.Ordinal))
        {
            error = "The repository subdirectory must use its canonical relative form.";
            return false;
        }
        if (!Enum.IsDefined(source.ReferenceKind))
        {
            error = "The plugin reference type is not supported.";
            return false;
        }
        if (!TryValidateReference(source.ReferenceKind, source.Reference, out error))
            return false;
        if (source.TimeoutSeconds is < 1 or > 300)
        {
            error = "The plugin timeout must be from 1 through 300 seconds.";
            return false;
        }
        return true;
    }

    public static bool TryValidateId(string value, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsLowerKebab(value))
        {
            error = "The plugin source ID must use lowercase letters, numbers, and single hyphens.";
            return false;
        }
        return true;
    }

    public static bool TryValidateCodexPackageName(string value, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsLowerKebab(value))
        {
            error = "The Codex package name must use lowercase letters, numbers, and single hyphens.";
            return false;
        }
        return true;
    }

    public static bool TryValidateReference(
        ManagedPluginReferenceKind kind,
        string value,
        out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The reference is required.";
            return false;
        }
        var candidate = value.Trim();
        if (!string.Equals(candidate, value, StringComparison.Ordinal)
            || candidate.Length > 256 || candidate.StartsWith("-", StringComparison.Ordinal)
            || candidate.EndsWith(".", StringComparison.Ordinal) || candidate.EndsWith("/", StringComparison.Ordinal)
            || candidate.Contains("..", StringComparison.Ordinal) || candidate.Contains("//", StringComparison.Ordinal)
            || candidate.Contains("@{", StringComparison.Ordinal) || candidate.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || candidate.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)
                || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            error = "The reference is not a safe Git branch or commit.";
            return false;
        }
        if (kind == ManagedPluginReferenceKind.Commit
            && (candidate.Length is not (40 or 64) || !candidate.All(char.IsAsciiHexDigit)))
        {
            error = "A commit reference must be a full 40-character or 64-character hexadecimal identity.";
            return false;
        }
        return true;
    }

    public static bool TryNormalizeRelativePath(
        string? value,
        bool allowEmpty,
        out string? path,
        out string error)
    {
        path = null;
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowEmpty)
                return true;
            error = "The relative path is required.";
            return false;
        }
        if (value.Contains('\\', StringComparison.Ordinal))
        {
            error = "Repository paths cannot contain backslashes.";
            return false;
        }
        var candidate = value.Trim();
        if (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];
        candidate = candidate.Trim('/');
        var segments = candidate.Split('/');
        if (Path.IsPathRooted(value) || candidate.Length > 512 || candidate.Length == 0
            || segments.Any(static segment => !IsPortablePathSegment(segment)))
        {
            error = "The repository subdirectory must be a safe relative path within the path limits.";
            return false;
        }
        path = string.Join('/', segments);
        return true;
    }

    private static bool IsPortablePathSegment(string segment)
    {
        if (segment.Length is 0 or > 255 || segment is "." or ".."
            || segment.EndsWith(' ') || segment.EndsWith('.')
            || segment.Any(char.IsControl)
            || segment.IndexOfAny(WindowsInvalidPathCharacters) >= 0)
        {
            return false;
        }

        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        return !WindowsReservedPathNames.Contains(stem);
    }

    public static string Fingerprint(ManagedPluginSource source)
    {
        var content = string.Join('\n', source.Repository, source.Subdirectory ?? "", source.Format,
            source.ReferenceKind.ToString(), source.Reference);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)));
    }

    private static bool TryParseRepositoryParts(string value, out string owner, out string repository)
    {
        owner = "";
        repository = "";
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(static part => !IsGitHubName(part)))
            return false;
        owner = parts[0];
        repository = parts[1];
        return true;
    }

    private static bool IsGitHubName(string value)
        => value.Length > 0 && value.Length <= 100
            && value is not "." and not ".."
            && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsLowerKebab(string value)
    {
        if (value[0] is '-' || value[^1] is '-')
            return false;
        var previousHyphen = false;
        foreach (var c in value)
        {
            if (c is '-')
            {
                if (previousHyphen)
                    return false;
                previousHyphen = true;
                continue;
            }
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))
                return false;
            previousHyphen = false;
        }
        return true;
    }
}

/// <summary>
/// A single skill server feed source.
/// </summary>
public sealed class SkillFeedSource
{
    /// <summary>
    /// Unique identifier for this feed (used as directory name and display label).
    /// Must be filesystem-safe: lowercase alphanumeric and hyphens.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Base URL of the skill server (e.g., "https://skills.corp.com").
    /// The daemon appends <c>/.well-known/agent-skills/index.json</c> for RFC discovery.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Optional API key for authenticated access to the skill server.
    /// Supports <c>ENC:</c> prefix for encrypted storage in secrets.json.
    /// </summary>
    public SensitiveString? ApiKey { get; set; }

    /// <summary>
    /// Whether this feed is active. Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// HTTP timeout in seconds for requests to this feed. Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
