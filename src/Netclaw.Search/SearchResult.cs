namespace Netclaw.Search;

/// <summary>
/// A single web search result returned by any search backend.
/// </summary>
public sealed record SearchResult(string Title, string Url, string Snippet);
