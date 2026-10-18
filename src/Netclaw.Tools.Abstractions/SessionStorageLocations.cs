// -----------------------------------------------------------------------
// <copyright file="SessionStorageLocations.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>Normalizes absolute paths shared by session storage value objects.</summary>
internal static class SessionStoragePathValue
{
    public static string NormalizeAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", parameterName);

        return Path.GetFullPath(path);
    }
}

/// <summary>A canonical absolute session workspace directory.</summary>
public readonly record struct SessionWorkspaceDirectory
{
    /// <summary>Creates the workspace below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public SessionWorkspaceDirectory(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "workspace"))
    {
    }

    private SessionWorkspaceDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static SessionWorkspaceDirectory FromLegacyPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for untrusted attachment staging.</summary>
public readonly record struct AttachmentStagingDirectory
{
    /// <summary>Creates the attachment staging directory below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public AttachmentStagingDirectory(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "attachment-staging"))
    {
    }

    private AttachmentStagingDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static AttachmentStagingDirectory FromLegacyPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for retained run artifacts.</summary>
public readonly record struct ArtifactDirectory
{
    /// <summary>Creates the parent artifact directory below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public ArtifactDirectory(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "artifacts"))
    {
    }

    internal ArtifactDirectory(SubAgentRunStorageRoot parent)
        : this(Path.Combine(parent.Value, "artifacts"))
    {
    }

    private ArtifactDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static ArtifactDirectory FromLegacyPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for disposable run files.</summary>
public readonly record struct ManagedTemporaryDirectory
{
    /// <summary>Creates the parent temporary directory below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public ManagedTemporaryDirectory(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "tmp", "parent"))
    {
    }

    internal ManagedTemporaryDirectory(SubAgentRunStorageRoot parent)
        : this(Path.Combine(parent.Value, "tmp"))
    {
    }

    private ManagedTemporaryDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static ManagedTemporaryDirectory FromPersistedPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute root which contains one managed temporary directory.</summary>
public readonly record struct ManagedTemporaryStorageRoot
{
    /// <summary>Creates the temporary storage root from a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public ManagedTemporaryStorageRoot(SessionStorageEnvelopeRoot parent)
        : this(parent.Value)
    {
    }

    private ManagedTemporaryStorageRoot(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static ManagedTemporaryStorageRoot FromPersistedPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for session Git worktrees.</summary>
public readonly record struct WorktreeDirectory
{
    /// <summary>Creates the worktree directory below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public WorktreeDirectory(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "worktrees"))
    {
    }

    private WorktreeDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static WorktreeDirectory FromLegacyPath(string value) => new(value);

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute path for one raw session log.</summary>
public readonly record struct SessionLogPath
{
    /// <summary>Creates the parent log path below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public SessionLogPath(SessionStorageEnvelopeRoot parent)
        : this(Path.Combine(parent.Value, "logs", "session.log"))
    {
    }

    internal SessionLogPath(SubAgentRunStorageRoot parent)
        : this(Path.Combine(parent.Value, "logs", "session.log"))
    {
    }

    private SessionLogPath(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    internal static SessionLogPath FromLegacyPath(string value) => new(value);

    /// <summary>Gets the canonical absolute file path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>Identifies the configured log directory used by sessions without a storage binding.</summary>
internal readonly record struct LegacySessionLogsDirectory
{
    /// <summary>Creates a legacy session-log directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public LegacySessionLogsDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }
}

/// <summary>Identifies one child-run directory below a versioned session envelope.</summary>
internal readonly record struct SubAgentRunStorageRoot
{
    /// <summary>Creates the child-run root from its session envelope and run identifier.</summary>
    /// <param name="envelopeRoot">The parent session envelope.</param>
    /// <param name="runId">The child-run identifier.</param>
    public SubAgentRunStorageRoot(SessionStorageEnvelopeRoot envelopeRoot, SubAgentRunId runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId.Value);
        EnvelopeRoot = envelopeRoot;
        Value = Path.Combine(envelopeRoot.Value, "subagents", runId.Value);
    }

    /// <summary>Gets the parent session envelope.</summary>
    public SessionStorageEnvelopeRoot EnvelopeRoot { get; }

    /// <summary>Gets the canonical absolute child-run directory.</summary>
    public string Value { get; }
}

/// <summary>
/// The per-user base of the short managed temporary folders on a POSIX host:
/// <c>/tmp/netclaw-&lt;uid&gt;</c> (issue #2554).
/// </summary>
/// <remarks>
/// SECURITY: the base is shared by every session of one daemon user. It is never
/// a session root. Only one key folder below it is a root of one session.
/// </remarks>
public readonly record struct ManagedTemporaryBase
{
    /// <summary>Creates a base from a canonical absolute folder path.</summary>
    /// <param name="value">The canonical absolute folder path.</param>
    public ManagedTemporaryBase(string value)
    {
        var normalized = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            throw new ArgumentException("A managed temporary base must be canonical.", nameof(value));

        Value = normalized;
    }

    /// <summary>Gets the canonical absolute folder path.</summary>
    public string Value { get; }

    /// <summary>
    /// Gets the base of the current effective user: <c>/tmp/netclaw-&lt;euid&gt;</c>, or
    /// <c>/private/tmp/netclaw-&lt;euid&gt;</c> on macOS.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The host is Windows.</exception>
    public static ManagedTemporaryBase ForCurrentUser()
        // Issue #2554 test-author stub. The implementer reads the effective user ID (geteuid).
        => throw new NotImplementedException("Issue #2554: ManagedTemporaryBase.ForCurrentUser is not implemented yet.");

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>Selects where the managed temporary folder of a run lives.</summary>
public abstract record ManagedTemporaryLayout
{
    private ManagedTemporaryLayout()
    {
    }

    /// <summary>
    /// The POSIX layout: <c>&lt;base&gt;/&lt;key&gt;/p</c> for a parent run and
    /// <c>&lt;base&gt;/&lt;key&gt;/c&lt;8 hex&gt;</c> for a child run.
    /// </summary>
    /// <param name="Base">The per-user base folder.</param>
    public sealed record PrivateBase(ManagedTemporaryBase Base) : ManagedTemporaryLayout;

    /// <summary>
    /// The Windows layout: <c>&lt;session-envelope&gt;/tmp/parent</c> for a parent run and
    /// <c>&lt;child-root&gt;/tmp</c> for a child run.
    /// </summary>
    public sealed record InsideSessionStorage : ManagedTemporaryLayout
    {
        private InsideSessionStorage()
        {
        }

        /// <summary>Gets the one instance.</summary>
        public static InsideSessionStorage Instance { get; } = new();
    }

    /// <summary>Gets the layout of this host: the private base on POSIX, else storage inside the session.</summary>
    public static ManagedTemporaryLayout ForHost()
        => OperatingSystem.IsWindows()
            ? InsideSessionStorage.Instance
            : new PrivateBase(ManagedTemporaryBase.ForCurrentUser());
}
