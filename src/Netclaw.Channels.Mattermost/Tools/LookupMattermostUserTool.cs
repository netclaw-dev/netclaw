// -----------------------------------------------------------------------
// <copyright file="LookupMattermostUserTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Mattermost;
using MattermostUser = Mattermost.Models.Users.User;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost.Tools;

/// <summary>
/// LLM tool that looks up Mattermost users by username or email.
/// Returns user IDs suitable for use with the generic <c>send_channel_message</c> tool.
/// </summary>
[NetclawTool("lookup_mattermost_user",
    "Look up a Mattermost user by username or email. " +
    "Returns their user ID for use with send_channel_message.",
    Grant = "builtin")]
public sealed partial class LookupMattermostUserTool : NetclawTool<LookupMattermostUserTool.Params>, IChannelAddressResolver
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

    private readonly Func<MattermostClient> _clientAccessor;
    private readonly MattermostChannelOptions _options;

    public record Params(
        [property: Description("Username or email address to search for")]
        string Query);

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => _options.AllowDirectMessages
        ? UserAndDirectMessageAddressKinds
        : UserAddressKinds;

    public LookupMattermostUserTool(MattermostClient client, MattermostChannelOptions options)
        : this(() => client, options)
    {
    }

    public LookupMattermostUserTool(Func<MattermostClient> clientAccessor, MattermostChannelOptions options)
    {
        _clientAccessor = clientAccessor;
        _options = options;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return "Error: 'query' parameter is required.";

        var query = args.Query.Trim();

        // Strip leading @ if present (users often type @username)
        if (query.StartsWith('@'))
            query = query[1..];

        var sb = new StringBuilder();
        Exception? lookupError = null;

        // Try username lookup first.
        try
        {
            var user = await _clientAccessor().GetUserByUsernameAsync(query);
            if (user is not null && !IsFilteredOut(user))
            {
                AppendUser(sb, user);
                return sb.ToString().TrimEnd();
            }
        }
        catch (Exception ex)
        {
            // A username miss falls through to the email lookup. A real
            // transport/auth failure is captured and surfaced below — it must
            // not masquerade as a clean "no matching user found".
            lookupError = ex;
        }

        // Try email lookup.
        if (query.Contains('@', StringComparison.Ordinal))
        {
            try
            {
                var user = await _clientAccessor().GetUserByEmailAsync(query);
                if (user is not null && !IsFilteredOut(user))
                {
                    AppendUser(sb, user);
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                lookupError = ex;
            }
        }

        return lookupError is not null
            ? $"No matching user found. The Mattermost lookup reported an error: {lookupError.Message}"
            : "No matching user found. Try an exact username (without @) or email address.";
    }

    public async ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ChannelAddressResolutionResult.Unsupported($"Mattermost user resolver cannot resolve channel key '{request.ChannelKey}'.");

        if (request.AddressKind != ChannelAddressKind.User && request.AddressKind != ChannelAddressKind.DirectMessage)
            return ChannelAddressResolutionResult.Unsupported($"Mattermost user resolver does not support address kind '{request.AddressKind}'.");

        if (request.AddressKind == ChannelAddressKind.DirectMessage && !_options.AllowDirectMessages)
            return ChannelAddressResolutionResult.Unsupported("Mattermost direct-message resolution is disabled in configuration.");

        var query = NormalizeUserQuery(request.Query);
        if (MattermostIdentifierFormat.IsMattermostId(query))
        {
            var userId = new MattermostUserId(query);
            return MattermostAclPolicy.IsAllowedUser(userId, _options)
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, query, query))
                : ChannelAddressResolutionResult.NotFound($"Mattermost user '{query}' is not in the allowed users list.");
        }

        var (user, lookupError) = await FindUserAsync(query);
        if (user is not null && !IsFilteredOut(user))
            return ChannelAddressResolutionResult.Resolved(ToResolvedAddress(request.AddressKind, user));

        return lookupError is not null
            ? ChannelAddressResolutionResult.NotFound($"No Mattermost user matched '{query}'. The Mattermost lookup reported an error: {lookupError.Message}")
            : ChannelAddressResolutionResult.NotFound($"No Mattermost user matched '{query}'.");
    }

    private bool IsFilteredOut(MattermostUser user)
        => _options.AllowedUserIds.Length > 0
            && !_options.AllowedUserIds.Contains(user.Id, StringComparer.Ordinal);

    private async Task<(MattermostUser? User, Exception? LookupError)> FindUserAsync(string query)
    {
        Exception? lookupError = null;

        try
        {
            var user = await _clientAccessor().GetUserByUsernameAsync(query);
            if (user is not null)
                return (user, null);
        }
        catch (Exception ex)
        {
            lookupError = ex;
        }

        if (query.Contains('@', StringComparison.Ordinal))
        {
            try
            {
                var user = await _clientAccessor().GetUserByEmailAsync(query);
                if (user is not null)
                    return (user, null);
            }
            catch (Exception ex)
            {
                lookupError = ex;
            }
        }

        return (null, lookupError);
    }

    private ResolvedChannelAddress ToResolvedAddress(ChannelAddressKind addressKind, MattermostUser user)
    {
        return new ResolvedChannelAddress(Key, addressKind, user.Id, GetDisplayName(user));
    }

    private static string NormalizeUserQuery(string query)
    {
        var normalized = query.Trim();
        return normalized.StartsWith('@') ? normalized[1..].Trim() : normalized;
    }

    private static string GetDisplayName(MattermostUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.Username))
            return $"@{user.Username}";

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;

        if (!string.IsNullOrWhiteSpace(user.Email))
            return user.Email;

        return user.Id;
    }

    private static void AppendUser(StringBuilder sb, MattermostUser user)
    {
        sb.AppendLine("Found user:");
        sb.Append($"  {user.Id} (@{user.Username})");
        if (!string.IsNullOrWhiteSpace(user.FirstName) || !string.IsNullOrWhiteSpace(user.LastName))
            sb.Append($" — {user.FirstName} {user.LastName}".TrimEnd());
        if (!string.IsNullOrWhiteSpace(user.Email))
            sb.Append($" — {user.Email}");
        sb.AppendLine();
    }
}
