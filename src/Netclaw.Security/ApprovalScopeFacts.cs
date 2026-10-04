// -----------------------------------------------------------------------
// <copyright file="ApprovalScopeFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Filesystem;

namespace Netclaw.Security;

/// <summary>
/// Compares grant scopes with the same folder and repository rules that
/// approval matching uses (<see cref="ApprovalPatternMatching"/>).
/// </summary>
/// <remarks>
/// A folder grant covers a directory below its root when no link lies between
/// them (R3). So a wider folder covers a narrower folder only when the narrower
/// folder exists and no link lies between the two. A repository grant covers a
/// directory that resolves to its Git common directory.
/// </remarks>
public sealed class ApprovalScopeFacts : IApprovalScopeFacts
{
    public static readonly ApprovalScopeFacts Instance = new();

    private ApprovalScopeFacts()
    {
    }

    /// <inheritdoc />
    public bool FolderCoversFolder(string wider, string narrower)
        => Directory.Exists(narrower)
           && CanonicalPath.TryCreateHost(wider, relativeBase: null, out var root)
           && CanonicalPath.TryCreateHost(narrower, relativeBase: null, out var folder)
           && FileSystemAuthority.EvaluateMembership(
               folder,
               [new PathBoundary.Folder(root, LinkRule.BelowRoot)]) is PathDecision.Allowed;

    /// <inheritdoc />
    public bool RepositoryCoversFolder(string repositoryCommonDirectory, string folder)
        => Directory.Exists(folder)
           && RepositoryIdentity.TryResolve(folder, cwd: null, out var repository)
           && ToolApprovalEntryComparer.Equals(repository!.CommonDirectory, repositoryCommonDirectory);
}
