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
/// The durable binding for a versioned session storage envelope. The stored root is absolute;
/// the resolver re-roots it under the current sessions directory when a home has moved.
/// </summary>
/// <param name="LayoutVersion">The layout that interprets the envelope.</param>
/// <param name="EnvelopeRoot">The absolute envelope root as stored.</param>
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
        : this(new ManagedTemporaryDirectory(parent), new ManagedTemporaryStorageRoot(parent), null)
    {
    }

    internal ManagedTemporaryLocation(SubAgentRunStorageRoot parent)
        : this(new ManagedTemporaryDirectory(parent), new ManagedTemporaryStorageRoot(parent.EnvelopeRoot), null)
    {
    }

    private ManagedTemporaryLocation(
        ManagedTemporaryDirectory directory,
        ManagedTemporaryStorageRoot storageRoot,
        ManagedTemporaryBase? privateBase)
    {
        Directory = directory;
        StorageRoot = storageRoot;
        PrivateBase = privateBase;

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
            ManagedTemporaryStorageRoot.FromPersistedPath(storageRoot),
            null);

    /// <summary>
    /// Creates the parent location <c>&lt;base&gt;/&lt;key&gt;/p</c>. The key is the first 16
    /// lowercase hex characters of the SHA-256 hash of the UTF-8 storage root text.
    /// </summary>
    /// <param name="temporaryBase">The per-user base.</param>
    /// <param name="storageRootText">The envelope root, or the legacy session directory.</param>
    internal static ManagedTemporaryLocation CreatePrivateParent(ManagedTemporaryBase temporaryBase, string storageRootText)
    {
        var keyFolder = Path.Combine(temporaryBase.Value, HashHex(storageRootText, 16));
        return new ManagedTemporaryLocation(
            ManagedTemporaryDirectory.FromPersistedPath(Path.Combine(keyFolder, "p")),
            ManagedTemporaryStorageRoot.FromPersistedPath(keyFolder),
            temporaryBase);
    }

    /// <summary>
    /// Creates the child location <c>&lt;base&gt;/&lt;key&gt;/c&lt;h&gt;</c> in the key folder of this
    /// private location. <c>h</c> is the first 8 lowercase hex characters of the SHA-256 hash of the run ID.
    /// </summary>
    internal ManagedTemporaryLocation CreatePrivateChild(SubAgentRunId runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId.Value);
        var temporaryBase = PrivateBase
            ?? throw new InvalidOperationException("Only a private temporary location has private child runs.");
        return new ManagedTemporaryLocation(
            ManagedTemporaryDirectory.FromPersistedPath(Path.Combine(StorageRoot.Value, "c" + HashHex(runId.Value, 8))),
            StorageRoot,
            temporaryBase);
    }

    private static string HashHex(string text, int length)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..length];

    /// <summary>Gets the process-specific temporary directory.</summary>
    public ManagedTemporaryDirectory Directory { get; }

    /// <summary>Gets the storage root that contains <see cref="Directory"/>.</summary>
    public ManagedTemporaryStorageRoot StorageRoot { get; }

    /// <summary>
    /// Gets the per-user base of a <see cref="ManagedTemporaryLayout.PrivateBase"/> location. It is
    /// <c>null</c> for a location inside session storage (Windows) and for a persisted record.
    /// </summary>
    /// <remarks>
    /// <c>ManagedTemporaryEnvironment.EnsureFolders</c> runs the private-folder checks (mode 0700, owner,
    /// no link) on this base and on <see cref="StorageRoot"/> when the value is present.
    /// </remarks>
    public ManagedTemporaryBase? PrivateBase { get; }
}

/// <summary>
/// The files that the runtime writes into one child run folder. The folder sits beside the artifact,
/// temporary, and log folders of the run. The spawner writes <see cref="TaskFile"/> before the child starts.
/// The session owner writes <see cref="ResultFile"/> after the child ends.
/// </summary>
public sealed record ChildRunFiles
{
    internal ChildRunFiles(string runDirectory)
    {
        RunDirectory = SessionStoragePathValue.NormalizeAbsolute(runDirectory, nameof(runDirectory));
        TaskFile = Path.Combine(RunDirectory, "task.md");
        ResultFile = Path.Combine(RunDirectory, "result.md");
    }

    /// <summary>Gets the canonical absolute folder of the run.</summary>
    public string RunDirectory { get; }

    /// <summary>Gets the path of the file that records the agent, the requester, and the brief.</summary>
    public string TaskFile { get; }

    /// <summary>Gets the path of the file that records the outcome and the output of the run.</summary>
    public string ResultFile { get; }
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
        LegacySessionLogsDirectory? legacyLogsBasePath,
        ChildRunFiles? childFiles)
    {
        Binding = binding;
        SessionDirectory = sessionDirectory;
        AttachmentStagingDirectory = attachmentStagingDirectory;
        ArtifactDirectory = artifactDirectory;
        ManagedTemporary = managedTemporary;
        WorktreeDirectory = worktreeDirectory;
        LogPath = logPath;
        LegacyLogsBasePath = legacyLogsBasePath;
        ChildFiles = childFiles;
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
    /// <summary>Gets the runtime-written files of a child run. A parent layout has none.</summary>
    public ChildRunFiles? ChildFiles { get; }

    /// <summary>Creates the version-2 parent layout below one persisted envelope.</summary>
    /// <param name="envelopeRoot">The persisted envelope root.</param>
    /// <param name="temporaryLayout">Where the managed temporary folder of each run lives.</param>
    /// <returns>The resolved parent paths.</returns>
    public static SessionStoragePaths CreateVersion2(
        SessionStorageEnvelopeRoot envelopeRoot,
        ManagedTemporaryLayout temporaryLayout)
    {
        ArgumentNullException.ThrowIfNull(temporaryLayout);
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            new SessionWorkspaceDirectory(envelopeRoot),
            new AttachmentStagingDirectory(envelopeRoot),
            new ArtifactDirectory(envelopeRoot),
            temporaryLayout switch
            {
                ManagedTemporaryLayout.PrivateBase privateLayout =>
                    ManagedTemporaryLocation.CreatePrivateParent(privateLayout.Base, envelopeRoot.Value),
                ManagedTemporaryLayout.InsideSessionStorage => new ManagedTemporaryLocation(envelopeRoot),
                _ => throw new ArgumentException("Unknown managed temporary layout.", nameof(temporaryLayout))
            },
            new WorktreeDirectory(envelopeRoot),
            new SessionLogPath(envelopeRoot),
            null,
            null);
    }

    /// <summary>Creates paths for an existing session without a versioned binding.</summary>
    /// <param name="sessionDirectory">The established session directory.</param>
    /// <param name="sessionLogsBasePath">The established session-log base.</param>
    /// <param name="sanitizedSessionId">The legacy path segment for the session.</param>
    /// <param name="temporaryLayout">Where the managed temporary folder of each run lives.</param>
    /// <returns>The unchanged legacy paths.</returns>
    public static SessionStoragePaths CreateLegacy(
        string sessionDirectory,
        string sessionLogsBasePath,
        string sanitizedSessionId,
        ManagedTemporaryLayout temporaryLayout)
    {
        ArgumentNullException.ThrowIfNull(temporaryLayout);
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
            temporaryLayout switch
            {
                ManagedTemporaryLayout.PrivateBase privateLayout =>
                    ManagedTemporaryLocation.CreatePrivateParent(privateLayout.Base, normalizedSessionDirectory.Value),
                ManagedTemporaryLayout.InsideSessionStorage => ManagedTemporaryLocation.FromPersistedPaths(
                    Path.Combine(normalizedSessionDirectory.Value, "tmp", "parent"),
                    normalizedSessionDirectory.Value),
                _ => throw new ArgumentException("Unknown managed temporary layout.", nameof(temporaryLayout))
            },
            WorktreeDirectory.FromLegacyPath(Path.Combine(normalizedSessionDirectory.Value, "worktrees")),
            SessionLogPath.FromLegacyPath(Path.Combine(normalizedLogsBase.Value, sanitizedSessionId, "session.log")),
            normalizedLogsBase,
            null);
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
                ManagedTemporary.PrivateBase is null
                    ? new ManagedTemporaryLocation(childRoot)
                    : ManagedTemporary.CreatePrivateChild(runId),
                WorktreeDirectory,
                new SessionLogPath(childRoot),
                null,
                new ChildRunFiles(childRoot.Value));
        }

        var childRootLegacy = Path.Combine(SessionDirectory.Value, "subagents", runId.Value);
        var sanitizedScopeId = SanitizePathSegment(legacyScopeId.Value);
        return new SessionStoragePaths(
            null,
            SessionDirectory,
            AttachmentStagingDirectory,
            ArtifactDirectory.FromLegacyPath(Path.Combine(childRootLegacy, "artifacts")),
            ManagedTemporary.PrivateBase is null
                ? ManagedTemporaryLocation.FromPersistedPaths(
                    Path.Combine(childRootLegacy, "tmp"),
                    SessionDirectory.Value)
                : ManagedTemporary.CreatePrivateChild(runId),
            WorktreeDirectory,
            SessionLogPath.FromLegacyPath(Path.Combine(
                LegacyLogsBasePath?.Value
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log")),
            LegacyLogsBasePath,
            new ChildRunFiles(childRootLegacy));
    }

    /// <summary>
    /// Tells whether a path names <c>task.md</c> or <c>result.md</c> in the run folder of a child of this session. The
    /// runtime writes these files, and they carry no work of the user.
    /// </summary>
    /// <param name="canonicalPath">The canonical absolute path.</param>
    /// <returns>True for a runtime-written child run file.</returns>
    public bool IsChildRunFile(string canonicalPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(canonicalPath);
        if (!string.Equals(name, "task.md", comparison) && !string.Equals(name, "result.md", comparison))
            return false;

        var runsRoot = Binding is { } binding
            ? Path.Combine(binding.EnvelopeRoot.Value, "subagents")
            : Path.Combine(SessionDirectory.Value, "subagents");
        var runDirectory = Path.GetDirectoryName(canonicalPath);
        return runDirectory is not null
            && string.Equals(Path.GetDirectoryName(runDirectory), runsRoot, comparison);
    }

    /// <summary>
    /// Keeps letters, digits, and hyphens, and replaces every other character with an
    /// underscore. Session folder names on disk come from this output, so a change to it
    /// stops an upgraded daemon from finding existing session folders.
    /// </summary>
    internal static string SanitizePathSegment(string value)
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
