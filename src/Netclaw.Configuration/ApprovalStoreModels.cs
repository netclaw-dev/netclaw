// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>
/// Context required to convert version-2 shell approvals.
/// </summary>
public sealed record ApprovalStoreMigrationContext(
    ApprovalShell NativeShell,
    string ShellToolName = "shell_execute");

/// <summary>
/// Stable store failure categories. The values contain no file or grant text.
/// </summary>
public enum ApprovalStoreFailure
{
    /// <summary>The cross-process file lock was not available.</summary>
    LockUnavailable = 0,

    /// <summary>The file had an invalid structure or value.</summary>
    InvalidData = 1,

    /// <summary>The file used a schema newer than this process supports.</summary>
    UnsupportedVersion = 2,

    /// <summary>A version conversion could not complete.</summary>
    MigrationFailed = 3,

    /// <summary>A file-system operation failed.</summary>
    IoFailure = 4,
}

/// <summary>
/// Typed result from an approval-store load.
/// </summary>
public abstract record ApprovalStoreLoadResult
{
    private ApprovalStoreLoadResult()
    {
    }

    /// <summary>The store was available and has one complete snapshot.</summary>
    public sealed record Ready(ApprovalStoreSnapshot Data) : ApprovalStoreLoadResult;

    /// <summary>The store could not provide authority for this check.</summary>
    public sealed record Unavailable(ApprovalStoreFailure Failure) : ApprovalStoreLoadResult;
}

/// <summary>
/// One detached immutable approval-store snapshot.
/// </summary>
public sealed record ApprovalStoreSnapshot(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>> Audiences);

/// <summary>
/// Typed result from one approval-store change.
/// </summary>
public abstract record ApprovalStoreChangeResult
{
    private ApprovalStoreChangeResult()
    {
    }

    /// <summary>The store was available. The count can be zero.</summary>
    public sealed record Completed(int ChangeCount) : ApprovalStoreChangeResult;

    /// <summary>The store could not complete the change.</summary>
    public sealed record Unavailable(ApprovalStoreFailure Failure) : ApprovalStoreChangeResult;
}

internal sealed class ApprovalStoreException(
    ApprovalStoreFailure failure,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal ApprovalStoreFailure Failure { get; } = failure;
}
