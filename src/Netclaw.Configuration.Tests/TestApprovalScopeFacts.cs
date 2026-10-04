// -----------------------------------------------------------------------
// <copyright file="TestApprovalScopeFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Scope facts for store tests in this project, which cannot reference
/// Netclaw.Security. No folder or repository grant covers another one, so only
/// an "anywhere" grant can cover a grant here. The hygiene invariant test in
/// Netclaw.Security.Tests uses the real facts.
/// </summary>
internal sealed class TestApprovalScopeFacts : IApprovalScopeFacts
{
    public static readonly TestApprovalScopeFacts None = new();

    public bool FolderCoversFolder(string wider, string narrower) => false;

    public bool RepositoryCoversFolder(string repositoryCommonDirectory, string folder) => false;
}
