using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Shared helper for downloading files from Slack's private file API with bot token auth.
/// </summary>
internal static class SlackFileDownloader
{
    public static async Task<ReadOnlyMemory<byte>> DownloadAsync(
        HttpClient httpClient,
        string url,
        SensitiveString? botToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (botToken is { Value: { Length: > 0 } token })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes;
    }
}
