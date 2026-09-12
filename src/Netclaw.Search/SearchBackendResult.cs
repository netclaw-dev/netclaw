// -----------------------------------------------------------------------
// <copyright file="SearchBackendResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Search;

/// <summary>
/// Discriminated result from a search backend operation.
/// Either a successful list of results or an error with a human-readable message.
/// </summary>
public abstract record SearchBackendResult
{
    private SearchBackendResult() { }

    public sealed record Success(IReadOnlyList<SearchResult> Results) : SearchBackendResult;
    public sealed record Error(string Message) : SearchBackendResult;
}
