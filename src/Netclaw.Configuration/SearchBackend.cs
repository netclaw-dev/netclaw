namespace Netclaw.Configuration;

/// <summary>
/// Supported web search backends.
/// </summary>
public enum SearchBackend
{
    DuckDuckGo,
    Brave,
    SearXng
}

public static class SearchBackendExtensions
{
    /// <summary>
    /// Returns the lowercase wire-format string used in JSON config files.
    /// </summary>
    public static string ToWireValue(this SearchBackend backend) => backend switch
    {
        SearchBackend.DuckDuckGo => "duckduckgo",
        SearchBackend.Brave => "brave",
        SearchBackend.SearXng => "searxng",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };
}
