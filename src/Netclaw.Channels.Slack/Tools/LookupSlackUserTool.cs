// -----------------------------------------------------------------------
// <copyright file="LookupSlackUserTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Tools;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack.Tools;

/// <summary>
/// LLM tool that looks up Slack users by name, display name, or email.
/// Returns user IDs suitable for use with the generic send_channel_message tool.
/// </summary>
[NetclawTool("lookup_slack_user",
    "Look up a Slack user by name, display name, or email. " +
    "Returns their user ID for use with send_channel_message.",
    Grant = "builtin")]
public sealed partial class LookupSlackUserTool : NetclawTool<LookupSlackUserTool.Params>, IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> UserAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User
    };

    private static readonly IReadOnlySet<ChannelAddressKind> UserAndDirectMessageAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User,
        ChannelAddressKind.DirectMessage
    };

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

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => _options.AllowDirectMessages
        ? UserAndDirectMessageAddressKinds
        : UserAddressKinds;

    public LookupSlackUserTool(IUsersApi usersApi, SlackChannelOptions options, TimeProvider timeProvider)
    {
        _usersApi = usersApi;
        _options = options;
        _timeProvider = timeProvider;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
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

    public async ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ChannelAddressResolutionResult.Unsupported($"Slack user resolver cannot resolve channel key '{request.ChannelKey}'.");

        if (request.AddressKind != ChannelAddressKind.User && request.AddressKind != ChannelAddressKind.DirectMessage)
            return ChannelAddressResolutionResult.Unsupported($"Slack user resolver does not support address kind '{request.AddressKind}'.");

        if (request.AddressKind == ChannelAddressKind.DirectMessage && !_options.AllowDirectMessages)
            return ChannelAddressResolutionResult.Unsupported("Slack direct-message resolution is disabled in configuration.");

        var query = NormalizeUserQuery(request.Query);
        if (IsSlackUserId(query))
        {
            var userId = new SlackUserId(query);
            return SlackAclPolicy.IsAllowedUser(userId, _options)
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, query, query))
                : ChannelAddressResolutionResult.NotFound($"Slack user '{query}' is not in the allowed users list.");
        }

        var users = await GetUsersAsync(cancellationToken);
        var matches = users
            .Where(user => MatchesQuery(user, query))
            .Select(user => ToResolvedAddress(request.AddressKind, user))
            .Take(10)
            .ToArray();

        if (matches.Length == 0)
            return ChannelAddressResolutionResult.NotFound($"No Slack user matched '{query}'.");

        if (matches.Length == 1)
            return ChannelAddressResolutionResult.Resolved(matches[0]);

        return ChannelAddressResolutionResult.Ambiguous(
            matches,
            $"Slack user query '{query}' matched {matches.Length} users.");
    }

    private static bool MatchesQuery(CachedUser user, string query)
    {
        return user.RealName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || user.Email.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private ResolvedChannelAddress ToResolvedAddress(ChannelAddressKind addressKind, CachedUser user)
    {
        var displayName = GetDisplayName(user);
        return new ResolvedChannelAddress(Key, addressKind, user.Id, displayName);
    }

    private static string GetDisplayName(CachedUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.RealName))
            return user.RealName;

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            return user.DisplayName;

        if (!string.IsNullOrWhiteSpace(user.Name))
            return user.Name;

        return user.Id;
    }

    private static string NormalizeUserQuery(string query)
    {
        var normalized = query.Trim();
        return normalized.StartsWith('@') ? normalized[1..].Trim() : normalized;
    }

    private static bool IsSlackUserId(string value)
    {
        return value.StartsWith("U", StringComparison.Ordinal);
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
