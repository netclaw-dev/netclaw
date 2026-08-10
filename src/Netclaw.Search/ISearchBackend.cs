// -----------------------------------------------------------------------
// <copyright file="ISearchBackend.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Search;

/// <summary>
/// Abstraction for web search providers. Implementations handle the
/// transport-specific details (HTML scraping, JSON APIs) and return
/// a uniform result type.
/// </summary>
public interface ISearchBackend
{
    /// <summary>
    /// Execute a web search query and return structured results.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with results, or Error with a human-readable message.</returns>
    Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct);
}
