// -----------------------------------------------------------------------
// <copyright file="GrantScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security.Authorization.Consent;

/// <summary>
/// Where a grant applies. A grant is a stored operator consent.
/// </summary>
/// <remarks>
/// This is the one scope type of the Consent context. Button keys, labels, the
/// trace vocabulary, and the v3 store fields derive from it at the edges. A new
/// scope is one case here and one value in <see cref="GrantScopeKind"/>; the
/// compiler then shows each switch that needs it.
/// </remarks>
public abstract record GrantScope
{
    private GrantScope()
    {
    }

    /// <summary>
    /// True when the persistent approval store holds the grant. Only a
    /// <see cref="Session"/> grant stays in memory.
    /// </summary>
    public bool IsPersistent => this is not Session;

    /// <summary>
    /// Gets the scope of a persisted entry. The v3 store encodes the scope in
    /// two nullable fields: <c>repository</c> wins, then <c>directory</c>, and
    /// neither means everywhere.
    /// </summary>
    public static GrantScope OfStoredEntry(ApprovalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Repository is { } repository)
            return new Repository(repository);

        return entry.Directory is { } directory
            ? new Folder(directory)
            : Everywhere.Instance;
    }

    /// <summary>This chat. The grant lives in actor memory for the session.</summary>
    public sealed record Session : GrantScope
    {
        private Session()
        {
        }

        public static Session Instance { get; } = new();
    }

    /// <summary>One directory tree. The v3 store writes it as <c>directory</c>.</summary>
    public sealed record Folder : GrantScope
    {
        public Folder(string directory)
        {
            // The directory text is stored as written. A canonical form would
            // change the v3 bytes and would reject PowerShell roots on a
            // non-Windows host.
            ArgumentNullException.ThrowIfNull(directory);
            Directory = directory;
        }

        public string Directory { get; }
    }

    /// <summary>
    /// The registered worktrees of one Git repository. The v3 store writes the
    /// Git common directory as <c>repository</c>.
    /// </summary>
    public sealed record Repository : GrantScope
    {
        public Repository(string commonDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
            CommonDirectory = commonDirectory;
        }

        public string CommonDirectory { get; }
    }

    /// <summary>Any directory. The v3 store writes neither field.</summary>
    public sealed record Everywhere : GrantScope
    {
        private Everywhere()
        {
        }

        public static Everywhere Instance { get; } = new();
    }
}

/// <summary>
/// The kind of scope that an operator answer selects. The answer does not know
/// each candidate's directory or repository, so the grant builder resolves the
/// kind to one <see cref="GrantScope"/> for each candidate.
/// </summary>
public enum GrantScopeKind
{
    Session,
    Folder,
    Repository,
    Everywhere,
}
