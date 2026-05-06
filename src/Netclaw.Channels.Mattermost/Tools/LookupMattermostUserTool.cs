// -----------------------------------------------------------------------
// <copyright file="LookupMattermostUserTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Mattermost;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost.Tools;

/// <summary>
/// LLM tool that looks up Mattermost users by username or email.
/// Returns user IDs suitable for use with <see cref="SendMattermostMessageTool"/>.
/// </summary>
[NetclawTool("lookup_mattermost_user",
    "Look up a Mattermost user by username or email. " +
    "Returns their user ID for use with send_mattermost_message.",
    Grant = "builtin")]
public sealed partial class LookupMattermostUserTool : NetclawTool<LookupMattermostUserTool.Params>, IChannelTool
{
    private readonly MattermostClient _client;
    private readonly MattermostChannelOptions _options;

    public record Params(
        [property: Description("Username or email address to search for")]
        string Query);

    public LookupMattermostUserTool(MattermostClient client, MattermostChannelOptions options)
    {
        _client = client;
        _options = options;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return "Error: 'query' parameter is required.";

        var query = args.Query.Trim();

        // Strip leading @ if present (users often type @username)
        if (query.StartsWith('@'))
            query = query[1..];

        var sb = new StringBuilder();

        // Try username lookup first
        try
        {
            var user = await _client.GetUserByUsernameAsync(query);
            if (user is not null && !IsFilteredOut(user))
            {
                AppendUser(sb, user);
                return sb.ToString().TrimEnd();
            }
        }
        catch
        {
            // Username not found — fall through to email lookup
        }

        // Try email lookup
        if (query.Contains('@', StringComparison.Ordinal))
        {
            try
            {
                var user = await _client.GetUserByEmailAsync(query);
                if (user is not null && !IsFilteredOut(user))
                {
                    AppendUser(sb, user);
                    return sb.ToString().TrimEnd();
                }
            }
            catch
            {
                // Email not found either
            }
        }

        return "No matching user found. Try an exact username (without @) or email address.";
    }

    private bool IsFilteredOut(global::Mattermost.Models.Users.User user)
        => _options.AllowedUserIds.Length > 0
            && !_options.AllowedUserIds.Contains(user.Id, StringComparer.Ordinal);

    private static void AppendUser(StringBuilder sb, global::Mattermost.Models.Users.User user)
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
