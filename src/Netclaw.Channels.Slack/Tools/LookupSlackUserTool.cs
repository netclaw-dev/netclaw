using System.ComponentModel;
using System.Text;
using Netclaw.Tools;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack.Tools;

/// <summary>
/// LLM tool that looks up Slack users by name, display name, or email.
/// Returns user IDs suitable for use with <see cref="SendSlackMessageTool"/>.
/// </summary>
[NetclawTool("lookup_slack_user",
    "Look up a Slack user by name, display name, or email. " +
    "Returns their user ID for use with send_slack_message.",
    Grant = "builtin")]
public sealed partial class LookupSlackUserTool : NetclawTool<LookupSlackUserTool.Params>, IChannelTool
{
    private readonly IUsersApi _usersApi;
    private readonly SlackChannelOptions _options;
    private readonly TimeProvider _timeProvider;

    // Simple in-memory cache: cleared after a short TTL to avoid stale data
    private List<CachedUser>? _cachedUsers;
    private DateTimeOffset _cacheExpiry;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public record Params(
        [property: Description("Name, display name, or email to search for")]
        string Query);

    public LookupSlackUserTool(IUsersApi usersApi, SlackChannelOptions options, TimeProvider timeProvider)
    {
        _usersApi = usersApi;
        _options = options;
        _timeProvider = timeProvider;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return "Error: 'query' parameter is required.";

        var users = await GetUsersAsync(ct);
        var query = args.Query.Trim();

        var matches = users
            .Where(u => MatchesQuery(u, query))
            .Take(10)
            .ToList();

        if (matches.Count == 0)
            return "No matching users found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matches.Count} matching user(s):");
        foreach (var user in matches)
        {
            sb.Append($"  {user.Id} ({user.RealName})");
            if (!string.IsNullOrWhiteSpace(user.Email))
                sb.Append($" — {user.Email}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static bool MatchesQuery(CachedUser user, string query)
    {
        return user.RealName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.Email.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<CachedUser>> GetUsersAsync(CancellationToken ct)
    {
        // Fast path: cache hit without lock
        if (_cachedUsers is { } cached && _timeProvider.GetUtcNow() < _cacheExpiry)
            return cached;

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedUsers is { } rechecked && _timeProvider.GetUtcNow() < _cacheExpiry)
                return rechecked;

            var allUsers = new List<CachedUser>();
            string? cursor = null;

            do
            {
                var response = await _usersApi.List(cursor: cursor, cancellationToken: ct);
                foreach (var user in response.Members)
                {
                    if (user.IsBot || user.Deleted)
                        continue;

                    // Filter to allowed users if the allow-list is non-empty
                    if (_options.AllowedUserIds.Length > 0
                        && !_options.AllowedUserIds.Contains(user.Id, StringComparer.Ordinal))
                        continue;

                    allUsers.Add(new CachedUser(
                        Id: user.Id,
                        Name: user.Name ?? string.Empty,
                        RealName: user.RealName ?? string.Empty,
                        DisplayName: user.Profile?.DisplayName ?? string.Empty,
                        Email: user.Profile?.Email ?? string.Empty));
                }

                cursor = response.ResponseMetadata?.NextCursor;
            } while (!string.IsNullOrWhiteSpace(cursor));

            _cachedUsers = allUsers;
            _cacheExpiry = _timeProvider.GetUtcNow() + CacheTtl;

            return allUsers;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    internal sealed record CachedUser(
        string Id,
        string Name,
        string RealName,
        string DisplayName,
        string Email);
}
