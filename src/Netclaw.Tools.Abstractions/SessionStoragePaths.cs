// -----------------------------------------------------------------------
// <copyright file="SessionStoragePaths.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// A persisted session-storage layout version.
/// </summary>
public readonly record struct SessionStorageLayoutVersion
{
    /// <summary>The unified session-envelope layout.</summary>
    public static SessionStorageLayoutVersion Version2 { get; } = new(2);

    /// <summary>Creates a positive storage-layout version.</summary>
    /// <param name="value">The positive wire value.</param>
    public SessionStorageLayoutVersion(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    /// <summary>Gets the positive wire value.</summary>
    public int Value { get; }

    /// <summary>Returns the wire value.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The canonical absolute root for one versioned session storage envelope.
/// </summary>
public readonly record struct SessionStorageEnvelopeRoot
{
    /// <summary>Creates a canonical absolute envelope root.</summary>
    /// <param name="value">The canonical absolute directory path.</param>
    public SessionStorageEnvelopeRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("A session storage envelope root must be an absolute path.", nameof(value));

        string canonical;
        try
        {
            canonical = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("A session storage envelope root must be a valid path.", nameof(value), ex);
        }

        if (!string.Equals(
                value,
                canonical,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("A session storage envelope root must be canonical.", nameof(value));

        Value = canonical;
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// The immutable durable binding for a versioned session storage envelope.
/// </summary>
/// <param name="LayoutVersion">The layout that interprets the envelope.</param>
/// <param name="EnvelopeRoot">The immutable absolute envelope root.</param>
public sealed record SessionStorageBinding(
    SessionStorageLayoutVersion LayoutVersion,
    SessionStorageEnvelopeRoot EnvelopeRoot);

/// <summary>
/// The validated directory and storage root for one run's temporary files.
/// </summary>
public readonly record struct ManagedTemporaryLocation
{
    /// <summary>Creates the parent temporary location below a versioned session envelope.</summary>
    /// <param name="parent">The parent session envelope.</param>
    public ManagedTemporaryLocation(SessionStorageEnvelopeRoot parent)
        : this(new ManagedTemporaryDirectory(parent), new ManagedTemporaryStorageRoot(parent))
    {
    }

    internal ManagedTemporaryLocation(SubAgentRunStorageRoot parent)
        : this(new ManagedTemporaryDirectory(parent), new ManagedTemporaryStorageRoot(parent.EnvelopeRoot))
    {
    }

    private ManagedTemporaryLocation(
        ManagedTemporaryDirectory directory,
        ManagedTemporaryStorageRoot storageRoot)
    {
        Directory = directory;
        StorageRoot = storageRoot;

        var relative = Path.GetRelativePath(StorageRoot.Value, Directory.Value);
        if (relative == "."
            || Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed temporary directory must be inside its storage root.",
                nameof(directory));
        }
    }

    internal static ManagedTemporaryLocation FromPersistedPaths(string directory, string storageRoot) =>
        new(
            ManagedTemporaryDirectory.FromPersistedPath(directory),
            ManagedTemporaryStorageRoot.FromPersistedPath(storageRoot));

    /// <summary>Gets the process-specific temporary directory.</summary>
    public ManagedTemporaryDirectory Directory { get; }

    /// <summary>Gets the storage root that contains <see cref="Directory"/>.</summary>
    public ManagedTemporaryStorageRoot StorageRoot { get; }
}

/// <summary>
/// Immutable paths for one parent or child run. The resolved paths define storage,
/// but they do not bypass content admission or filesystem authorization.
/// </summary>
public sealed record SessionStoragePaths
{
    private SessionStoragePaths(
        SessionStorageBinding? binding,
        SessionWorkspaceDirectory sessionDirectory,
        AttachmentStagingDirectory attachmentStagingDirectory,
        ArtifactDirectory artifactDirectory,
        ManagedTemporaryLocation managedTemporary,
        WorktreeDirectory worktreeDirectory,
        SessionLogPath logPath,
        LegacySessionLogsDirectory? legacyLogsBasePath)
    {
        Binding = binding;
        SessionDirectory = sessionDirectory;
        AttachmentStagingDirectory = attachmentStagingDirectory;
        ArtifactDirectory = artifactDirectory;
        ManagedTemporary = managedTemporary;
        WorktreeDirectory = worktreeDirectory;
        LogPath = logPath;
        LegacyLogsBasePath = legacyLogsBasePath;
    }

    /// <summary>Gets the durable versioned binding. A null value identifies an unchanged legacy layout.</summary>
    public SessionStorageBinding? Binding { get; }
    /// <summary>Gets the session workspace and default relative-path base.</summary>
    public SessionWorkspaceDirectory SessionDirectory { get; }
    /// <summary>Gets the directory for untrusted attachments before content admission.</summary>
    public AttachmentStagingDirectory AttachmentStagingDirectory { get; }
    /// <summary>Gets the current run's retained artifact directory.</summary>
    public ArtifactDirectory ArtifactDirectory { get; }
    /// <summary>Gets the current run's managed temporary location.</summary>
    public ManagedTemporaryLocation ManagedTemporary { get; }
    /// <summary>Gets the session-owned directory for Git worktrees.</summary>
    public WorktreeDirectory WorktreeDirectory { get; }
    /// <summary>Gets the current run's raw session log path.</summary>
    public SessionLogPath LogPath { get; }
    private LegacySessionLogsDirectory? LegacyLogsBasePath { get; }

    /// <summary>Creates the version-2 parent layout below one persisted envelope.</summary>
    /// <param name="envelopeRoot">The persisted envelope root.</param>
    /// <returns>The resolved parent paths.</returns>
    public static SessionStoragePaths CreateVersion2(SessionStorageEnvelopeRoot envelopeRoot)
    {
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            new SessionWorkspaceDirectory(envelopeRoot),
            new AttachmentStagingDirectory(envelopeRoot),
            new ArtifactDirectory(envelopeRoot),
            new ManagedTemporaryLocation(envelopeRoot),
            new WorktreeDirectory(envelopeRoot),
            new SessionLogPath(envelopeRoot),
            null);
    }

    /// <summary>Creates paths for an existing session without a versioned binding.</summary>
    /// <param name="sessionDirectory">The established session directory.</param>
    /// <param name="sessionLogsBasePath">The established session-log base.</param>
    /// <param name="sanitizedSessionId">The legacy path segment for the session.</param>
    /// <returns>The unchanged legacy paths.</returns>
    public static SessionStoragePaths CreateLegacy(
        string sessionDirectory,
        string sessionLogsBasePath,
        string sanitizedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedSessionId);
        var normalizedSessionDirectory = SessionWorkspaceDirectory.FromLegacyPath(sessionDirectory);
        var normalizedLogsBase = new LegacySessionLogsDirectory(sessionLogsBasePath);
        return new SessionStoragePaths(
            null,
            normalizedSessionDirectory,
            AttachmentStagingDirectory.FromLegacyPath(Path.Combine(
                Directory.GetParent(normalizedSessionDirectory.Value)?.FullName
                ?? throw new ArgumentException("The legacy session directory needs a parent.", nameof(sessionDirectory)),
                ".attachment-staging",
                sanitizedSessionId)),
            ArtifactDirectory.FromLegacyPath(Path.Combine(normalizedSessionDirectory.Value, "artifacts")),
            ManagedTemporaryLocation.FromPersistedPaths(
                Path.Combine(normalizedSessionDirectory.Value, "tmp", "parent"),
                normalizedSessionDirectory.Value),
            WorktreeDirectory.FromLegacyPath(Path.Combine(normalizedSessionDirectory.Value, "worktrees")),
            SessionLogPath.FromLegacyPath(Path.Combine(normalizedLogsBase.Value, sanitizedSessionId, "session.log")),
            normalizedLogsBase);
    }

    /// <summary>Derives one child run from the parent layout.</summary>
    /// <param name="runId">The opaque child run identifier.</param>
    /// <param name="legacyScopeId">The legacy scope used only for an old log layout.</param>
    /// <returns>The child-specific artifact, temporary, and log paths.</returns>
    public SessionStoragePaths ForChild(SubAgentRunId runId, SubAgentScopeId legacyScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyScopeId.Value);

        if (Binding is { } binding)
        {
            var childRoot = new SubAgentRunStorageRoot(binding.EnvelopeRoot, runId);
            return new SessionStoragePaths(
                binding,
                SessionDirectory,
                AttachmentStagingDirectory,
                new ArtifactDirectory(childRoot),
                new ManagedTemporaryLocation(childRoot),
                WorktreeDirectory,
                new SessionLogPath(childRoot),
                null);
        }

        var childRootLegacy = Path.Combine(SessionDirectory.Value, "subagents", runId.Value);
        var sanitizedScopeId = SanitizePathSegment(legacyScopeId.Value);
        return new SessionStoragePaths(
            null,
            SessionDirectory,
            AttachmentStagingDirectory,
            ArtifactDirectory.FromLegacyPath(Path.Combine(childRootLegacy, "artifacts")),
            ManagedTemporaryLocation.FromPersistedPaths(
                Path.Combine(childRootLegacy, "tmp"),
                SessionDirectory.Value),
            WorktreeDirectory,
            SessionLogPath.FromLegacyPath(Path.Combine(
                LegacyLogsBasePath?.Value
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log")),
            LegacyLogsBasePath);
    }

    private static string SanitizePathSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            buffer[index] = char.IsLetterOrDigit(value[index]) || value[index] == '-'
                ? value[index]
                : '_';
        }

        return new string(buffer);
    }
}
