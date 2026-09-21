// -----------------------------------------------------------------------
// <copyright file="TeamsGraphDirectoryClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Users.Item.CheckMemberGroups;
using Microsoft.Kiota.Abstractions;
using Netclaw.Channels.Teams;

namespace Netclaw.Channels.Teams.Graph;

/// <summary>
/// Bounded Microsoft Graph implementation of the SDK-free Teams directory
/// boundary. It owns no configuration persistence and never exposes Graph
/// types outside this infrastructure project.
/// </summary>
public sealed partial class TeamsGraphDirectoryClient : ITeamsDirectory, ITeamsDirectoryUserCache, IDisposable
{
    public const string DefaultScope = "https://graph.microsoft.com/.default";
    public const int MinimumSearchLength = 2;
    public const int MaximumSearchLength = 128;
    public const int MaximumResults = 50;
    public const int MaximumGroupsPerMembershipRequest = 20;
    private const int CacheSizeLimit = 1_024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProfileAndMembershipTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DirectoryRecordTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GroupChatContinuationTtl = TimeSpan.FromMinutes(5);
    private readonly GraphServiceClient _graphClient;
    private readonly string _tenantId;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsCache;
    private readonly bool _ownsGraphClient;

    /// <summary>
    /// Creates a client using an existing Graph client. The supplied cache is
    /// useful for host composition and deterministic tests; production callers
    /// should use <see cref="Create"/> to receive a size-bounded cache.
    /// </summary>
    public TeamsGraphDirectoryClient(
        GraphServiceClient graphClient,
        string tenantId,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
        : this(graphClient, tenantId, cache, timeProvider ?? TimeProvider.System, ownsCache: false, ownsGraphClient: false)
    {
    }

    private TeamsGraphDirectoryClient(
        GraphServiceClient graphClient,
        string tenantId,
        IMemoryCache cache,
        TimeProvider timeProvider,
        bool ownsCache,
        bool ownsGraphClient)
    {
        _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        _tenantId = RequireValue(tenantId, nameof(tenantId));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsCache = ownsCache;
        _ownsGraphClient = ownsGraphClient;
    }

    /// <summary>
    /// Creates the one long-lived app-only Graph client for a complete Teams
    /// credential. The secret is used only to construct the token credential.
    /// </summary>
    public static TeamsGraphDirectoryClient Create(TeamsChannelOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tenantId = RequireValue(options.TenantId, nameof(options.TenantId));
        var clientId = RequireValue(options.ClientId, nameof(options.ClientId));
        var clientSecret = RequireValue(options.ClientSecret?.Value, nameof(options.ClientSecret));
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graphClient = new GraphServiceClient(credential, [DefaultScope]);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit });
        return new TeamsGraphDirectoryClient(graphClient, tenantId, cache, timeProvider ?? TimeProvider.System, ownsCache: true, ownsGraphClient: true);
    }

    public async ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeSearch(query, maximumResults, out var normalizedQuery, out var maximum, out var reason))
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.InvalidRequest(reason);

        var key = CacheKey("teams-search", CacheQuery(normalizedQuery, maximum));
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamsDirectoryTeam>? cached) && cached is not null)
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available(cached);

        var escaped = EscapeODataLiteral(normalizedQuery);
        var result = await ExecuteAsync(
            async token =>
            {
                var response = await _graphClient.Teams.GetAsync(request =>
                {
                    request.QueryParameters.Top = maximum;
                    request.QueryParameters.Select = ["id", "displayName", "description"];
                    request.QueryParameters.Filter = $"startswith(displayName,'{escaped}')";
                }, token).ConfigureAwait(false);
                var teams = new List<Microsoft.Graph.Models.Team>();
                while (response is not null)
                {
                    teams.AddRange(response.Value ?? []);
                    if (teams.Count >= maximum || string.IsNullOrWhiteSpace(response.OdataNextLink))
                        break;

                    response = await _graphClient.Teams.WithUrl(response.OdataNextLink)
                        .GetAsync(cancellationToken: token).ConfigureAwait(false);
                }

                return ToTeams(teams, maximum);
            },
            cancellationToken).ConfigureAwait(false);

        CacheRecords(result, "team", static team => team.Id, DirectoryRecordTtl);
        return CacheResult(key, result, SearchTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
        string teamId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(teamId, out var canonicalTeamId))
            return TeamsDirectoryOperationResult<TeamsDirectoryTeam>.InvalidRequest("teams_directory_invalid_request");

        var key = CacheKey("team", canonicalTeamId);
        if (_cache.TryGetValue(key, out TeamsDirectoryTeam? cached) && cached is not null)
            return TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Available(cached);

        var result = await ExecuteAsync(
            async token => ToTeam(await _graphClient.Teams[canonicalTeamId].GetAsync(request =>
            {
                request.QueryParameters.Select = ["id", "displayName", "description"];
            }, token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        return CacheResult(key, result, DirectoryRecordTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
        string teamId,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(teamId, out var canonicalTeamId) || !TryNormalizeMaximum(maximumResults, out var maximum))
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.InvalidRequest("teams_directory_invalid_request");

        var key = CacheKey("channels", CacheQuery(canonicalTeamId, maximum));
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamsDirectoryChannel>? cached) && cached is not null)
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Available(cached);

        var result = await ExecuteAsync(
            async token =>
            {
                var response = await _graphClient.Teams[canonicalTeamId].Channels.GetAsync(request =>
                {
                    // The List channels API supports only $filter and $select;
                    // sending $top causes Graph to reject the entire request.
                    // Keep the caller's bound locally while following paging links.
                    request.QueryParameters.Select = ["id", "displayName", "description"];
                }, token).ConfigureAwait(false);
                var channels = new List<Microsoft.Graph.Models.Channel>();
                while (response is not null)
                {
                    channels.AddRange(response.Value ?? []);
                    if (channels.Count >= maximum || string.IsNullOrWhiteSpace(response.OdataNextLink))
                        break;

                    response = await _graphClient.Teams[canonicalTeamId].Channels.WithUrl(response.OdataNextLink)
                        .GetAsync(cancellationToken: token).ConfigureAwait(false);
                }

                return ToChannels(canonicalTeamId, channels, maximum);
            },
            cancellationToken).ConfigureAwait(false);

        CacheRecords(
            result,
            "channel",
            channel => ChannelCacheValue(channel.TeamId, channel.Id),
            DirectoryRecordTtl);
        return CacheResult(key, result, SearchTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
        string teamId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(teamId, out var canonicalTeamId)
            || !TryNormalizeIdentifier(channelId, out var canonicalChannelId))
        {
            return TeamsDirectoryOperationResult<TeamsDirectoryChannel>.InvalidRequest("teams_directory_invalid_request");
        }

        var key = CacheKey("channel", ChannelCacheValue(canonicalTeamId, canonicalChannelId));
        if (_cache.TryGetValue(key, out TeamsDirectoryChannel? cached) && cached is not null)
            return TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Available(cached);

        var result = await ExecuteAsync(
            async token => ToChannel(
                canonicalTeamId,
                await _graphClient.Teams[canonicalTeamId].Channels[canonicalChannelId].GetAsync(request =>
                {
                    request.QueryParameters.Select = ["id", "displayName", "description"];
                }, token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        return CacheResult(key, result, DirectoryRecordTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatPage>> GetGroupChatsAsync(
        string userId,
        int maximumResults,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(userId, out var canonicalUserId)
            || !TryNormalizeMaximum(maximumResults, out var maximum))
        {
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatPage>.InvalidRequest("teams_directory_invalid_request");
        }

        string? nextLink = null;
        if (!string.IsNullOrWhiteSpace(continuation)
            && !TryGetGroupChatContinuation(canonicalUserId, continuation, out nextLink))
        {
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatPage>.InvalidRequest("teams_directory_invalid_continuation");
        }

        var result = await ExecuteAsync(
            async token =>
            {
                Microsoft.Graph.Models.ChatCollectionResponse? response;
                if (nextLink is null)
                {
                    response = await _graphClient.Users[canonicalUserId].Chats.GetAsync(request =>
                    {
                        request.QueryParameters.Top = maximum;
                        request.QueryParameters.Expand = ["members"];
                    }, token).ConfigureAwait(false);
                }
                else
                {
                    response = await _graphClient.Users[canonicalUserId].Chats.WithUrl(nextLink)
                        .GetAsync(cancellationToken: token).ConfigureAwait(false);
                }

                var chats = ToGroupChats(response?.Value, maximum);
                var next = string.IsNullOrWhiteSpace(response?.OdataNextLink)
                    ? null
                    : CreateGroupChatContinuation(canonicalUserId, response.OdataNextLink);
                return new TeamsDirectoryGroupChatPage(chats, next);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsAvailable && result.Value is not null)
            CacheRecords(
                TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroupChat>>.Available(result.Value.Chats),
                "group-chat",
                static chat => chat.Id,
                DirectoryRecordTtl);

        return result;
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChat>> GetGroupChatAsync(
        string chatId,
        CancellationToken cancellationToken = default)
    {
        if (!TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(chatId))
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChat>.InvalidRequest("teams_directory_invalid_request");

        var canonicalChatId = chatId.Trim();
        var key = CacheKey("group-chat", canonicalChatId);
        if (_cache.TryGetValue(key, out TeamsDirectoryGroupChat? cached) && cached is not null)
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChat>.Available(cached);

        var result = await ExecuteAsync(
            async token => ToGroupChat(
                await _graphClient.Chats[canonicalChatId].GetAsync(request =>
                {
                    request.QueryParameters.Expand = ["members"];
                }, token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        return CacheResult(key, result, DirectoryRecordTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeSearch(query, maximumResults, out var normalizedQuery, out var maximum, out var reason))
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.InvalidRequest(reason);

        var key = CacheKey("users-search", CacheQuery(normalizedQuery, maximum));
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamsDirectoryUser>? cached) && cached is not null)
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Available(cached);

        var escaped = EscapeODataLiteral(normalizedQuery);
        var result = await ExecuteAsync(
            async token =>
            {
                var response = await _graphClient.Users.GetAsync(request =>
                {
                    request.QueryParameters.Top = maximum;
                    request.QueryParameters.Select = ["id", "displayName", "userPrincipalName", "mail"];
                    request.QueryParameters.Filter =
                        $"startswith(displayName,'{escaped}') or startswith(userPrincipalName,'{escaped}') or startswith(mail,'{escaped}')";
                }, token).ConfigureAwait(false);
                var users = new List<Microsoft.Graph.Models.User>();
                while (response is not null)
                {
                    users.AddRange(response.Value ?? []);
                    if (users.Count >= maximum || string.IsNullOrWhiteSpace(response.OdataNextLink))
                        break;

                    response = await _graphClient.Users.WithUrl(response.OdataNextLink)
                        .GetAsync(cancellationToken: token).ConfigureAwait(false);
                }

                return ToUsers(users, maximum);
            },
            cancellationToken).ConfigureAwait(false);

        CacheRecords(result, "user", static user => user.Id, ProfileAndMembershipTtl);
        return CacheResult(key, result, SearchTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeSearch(query, maximumResults, out var normalizedQuery, out var maximum, out var reason))
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.InvalidRequest(reason);

        var key = CacheKey("groups-search", CacheQuery(normalizedQuery, maximum));
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamsDirectoryGroup>? cached) && cached is not null)
            return TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Available(cached);

        var escaped = EscapeODataLiteral(normalizedQuery);
        var result = await ExecuteAsync(
            async token =>
            {
                var response = await _graphClient.Groups.GetAsync(request =>
                {
                    request.QueryParameters.Top = maximum;
                    request.QueryParameters.Select = ["id", "displayName", "mail", "securityEnabled", "groupTypes"];
                    request.QueryParameters.Filter =
                        $"(securityEnabled eq true or groupTypes/any(c:c eq 'Unified')) and startswith(displayName,'{escaped}')";
                }, token).ConfigureAwait(false);
                var groups = new List<Microsoft.Graph.Models.Group>();
                while (response is not null)
                {
                    groups.AddRange(response.Value ?? []);
                    if (groups.Count >= maximum || string.IsNullOrWhiteSpace(response.OdataNextLink))
                        break;

                    response = await _graphClient.Groups.WithUrl(response.OdataNextLink)
                        .GetAsync(cancellationToken: token).ConfigureAwait(false);
                }

                return ToGroups(groups, maximum);
            },
            cancellationToken).ConfigureAwait(false);

        CacheRecords(result, "group", static group => group.Id, DirectoryRecordTtl);
        return CacheResult(key, result, SearchTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(groupId, out var canonicalGroupId))
            return TeamsDirectoryOperationResult<TeamsDirectoryGroup>.InvalidRequest("teams_directory_invalid_request");

        var key = CacheKey("group", canonicalGroupId);
        if (_cache.TryGetValue(key, out TeamsDirectoryGroup? cached) && cached is not null)
            return TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Available(cached);

        var result = await ExecuteAsync(
            async token => ToGroup(await _graphClient.Groups[canonicalGroupId].GetAsync(request =>
            {
                request.QueryParameters.Select = ["id", "displayName", "mail", "securityEnabled", "groupTypes"];
            }, token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        return CacheResult(key, result, DirectoryRecordTtl);
    }

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(userId, out var canonicalUserId))
            return TeamsDirectoryOperationResult<TeamsDirectoryUser>.InvalidRequest("teams_directory_invalid_request");

        var key = CacheKey("user", canonicalUserId);
        if (_cache.TryGetValue(key, out TeamsDirectoryUser? cached) && cached is not null)
            return TeamsDirectoryOperationResult<TeamsDirectoryUser>.Available(cached);

        var result = await ExecuteAsync(
            async token =>
            {
                var user = await _graphClient.Users[canonicalUserId].GetAsync(request =>
                {
                    request.QueryParameters.Select = ["id", "displayName", "userPrincipalName", "mail"];
                }, token).ConfigureAwait(false);
                return ToUser(user);
            },
            cancellationToken).ConfigureAwait(false);

        return CacheResult(key, result, ProfileAndMembershipTtl);
    }

    public bool TryGetCachedUser(string userId, out TeamsDirectoryUser user)
    {
        user = default!;
        if (!TryNormalizeIdentifier(userId, out var canonicalUserId))
            return false;

        if (_cache.TryGetValue(CacheKey("user", canonicalUserId), out TeamsDirectoryUser? cached)
            && cached is not null)
        {
            user = cached;
            return true;
        }

        return false;
    }

    public async ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
        string userId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeIdentifier(userId, out var canonicalUserId) || groupIds is null)
            return TeamsDirectoryOperationResult<IReadOnlySet<string>>.InvalidRequest("teams_directory_invalid_request");

        var canonicalGroupIds = groupIds
            .Where(static groupId => TryNormalizeIdentifier(groupId, out _))
            .Select(static groupId => groupId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (canonicalGroupIds.Length != groupIds.Count)
            return TeamsDirectoryOperationResult<IReadOnlySet<string>>.InvalidRequest("teams_directory_invalid_request");

        if (canonicalGroupIds.Length == 0)
            return TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(new HashSet<string>(StringComparer.Ordinal));

        var key = CacheKey("membership", canonicalUserId + "\n" + string.Join("\n", canonicalGroupIds.OrderBy(static id => id, StringComparer.Ordinal)));
        if (_cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null)
            return TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(cached);

        foreach (var chunk in canonicalGroupIds.Chunk(MaximumGroupsPerMembershipRequest))
        {
            var result = await ExecuteAsync(
                async token =>
                {
                    var response = await _graphClient.Users[canonicalUserId].CheckMemberGroups
                        .PostAsCheckMemberGroupsPostResponseAsync(
                            new CheckMemberGroupsPostRequestBody { GroupIds = [.. chunk] },
                            cancellationToken: token)
                        .ConfigureAwait(false);
                    if (response is null)
                        throw new InvalidDataException("The membership response was empty.");

                    return (IReadOnlySet<string>)new HashSet<string>(
                        (response.Value ?? []).Where(chunk.Contains),
                        StringComparer.Ordinal);
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.IsAvailable)
                return result;
            if (result.Value is { Count: > 0 } matches)
                return CacheResult(key, result, ProfileAndMembershipTtl);
        }

        return CacheResult(
            key,
            TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(new HashSet<string>(StringComparer.Ordinal)),
            ProfileAndMembershipTtl);
    }

    public void Dispose()
    {
        ClearGroupChatSearchStates();
        if (_ownsGraphClient)
            _graphClient.Dispose();
        if (_ownsCache && _cache is IDisposable disposableCache)
            disposableCache.Dispose();
    }

    private ValueTask<TeamsDirectoryOperationResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) => ExecuteAsync(operation, cancellationToken, retry: true);

    private async ValueTask<TeamsDirectoryOperationResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool retry)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);

        try
        {
            return TeamsDirectoryOperationResult<T>.Available(retry
                ? await ExecuteWithRetryAsync(operation, deadline.Token).ConfigureAwait(false)
                : await operation(deadline.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_timeout");
        }
        catch (AuthenticationFailedException)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_authentication_failed");
        }
        catch (ApiException exception)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable(ReasonFor(exception));
        }
        catch (HttpRequestException)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_network_unavailable");
        }
        catch (InvalidDataException)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_malformed_response");
        }
        catch (Exception)
        {
            return TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_request_failed");
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception) when (IsRetryable(exception))
        {
            var retryAfter = RetryDelay(exception);
            await Task.Delay(retryAfter, _timeProvider, cancellationToken).ConfigureAwait(false);
            return await operation(cancellationToken).ConfigureAwait(false);
        }
    }

    private TeamsDirectoryOperationResult<T> CacheResult<T>(
        string key,
        TeamsDirectoryOperationResult<T> result,
        TimeSpan ttl)
    {
        if (result.IsAvailable && result.Value is not null)
        {
            _cache.Set(key, result.Value, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1
            });
        }

        return result;
    }

    private void CacheRecords<T>(
        TeamsDirectoryOperationResult<IReadOnlyList<T>> result,
        string resourceType,
        Func<T, string> cacheValue,
        TimeSpan ttl)
    {
        if (!result.IsAvailable || result.Value is null)
            return;

        foreach (var value in result.Value)
        {
            var key = CacheKey(resourceType, cacheValue(value));
            _cache.Set(key, value, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1
            });
        }
    }

    private string CacheKey(string resourceType, string value)
        => $"teams-directory:{resourceType}:{Hash(_tenantId + "\n" + value)}";

    private static string CacheQuery(string value, int maximum) => value + "\n" + maximum.ToString(CultureInfo.InvariantCulture);

    private static string ChannelCacheValue(string teamId, string channelId) => teamId + "\n" + channelId;

    private static IReadOnlyList<TeamsDirectoryTeam> ToTeams(
        IEnumerable<Microsoft.Graph.Models.Team>? values,
        int maximum) =>
        values?
            .Where(static team => TryNormalizeIdentifier(team.Id, out _))
            .Take(maximum)
            .Select(static team => new TeamsDirectoryTeam(team.Id!.Trim(), team.DisplayName, team.Description))
            .ToArray()
        ?? [];

    private static TeamsDirectoryTeam ToTeam(Microsoft.Graph.Models.Team? team)
    {
        if (team is null || !TryNormalizeIdentifier(team.Id, out var id))
            throw new InvalidDataException("The team response has no canonical ID.");

        return new TeamsDirectoryTeam(id, team.DisplayName, team.Description);
    }

    private static IReadOnlyList<TeamsDirectoryChannel> ToChannels(
        string teamId,
        IEnumerable<Microsoft.Graph.Models.Channel>? values,
        int maximum) =>
        values?
            .Where(static channel => TryNormalizeIdentifier(channel.Id, out _))
            .Take(maximum)
            .Select(channel => new TeamsDirectoryChannel(teamId, channel.Id!.Trim(), channel.DisplayName, channel.Description))
            .ToArray()
        ?? [];

    private static TeamsDirectoryChannel ToChannel(string teamId, Microsoft.Graph.Models.Channel? channel)
    {
        if (channel is null || !TryNormalizeIdentifier(channel.Id, out var id))
            throw new InvalidDataException("The channel response has no canonical ID.");

        return new TeamsDirectoryChannel(teamId, id, channel.DisplayName, channel.Description);
    }

    private static IReadOnlyList<TeamsDirectoryGroupChat> ToGroupChats(
        IEnumerable<Microsoft.Graph.Models.Chat>? values,
        int maximum) =>
        values?
            .Where(static chat => chat.ChatType == Microsoft.Graph.Models.ChatType.Group)
            .Where(static chat => TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(chat.Id))
            .GroupBy(static chat => chat.Id!.Trim(), StringComparer.Ordinal)
            .Select(static group => ToGroupChat(group.First()))
            .Take(maximum)
            .ToArray()
        ?? [];

    private static TeamsDirectoryGroupChat ToGroupChat(Microsoft.Graph.Models.Chat? chat)
    {
        var chatId = chat?.Id;
        if (chat is null
            || chatId is null
            || chat.ChatType != Microsoft.Graph.Models.ChatType.Group
            || !TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(chatId))
        {
            throw new InvalidDataException("The chat response is not a canonical Group Chat.");
        }

        var participants = chat.Members?
            .Select(static member => NormalizeDisplayText(member.DisplayName))
            .Where(static displayName => displayName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToArray()
            ?? [];
        return new TeamsDirectoryGroupChat(chatId.Trim(), NormalizeDisplayText(chat.Topic), participants);
    }

    private static IReadOnlyList<TeamsDirectoryUser> ToUsers(
        IEnumerable<Microsoft.Graph.Models.User>? values,
        int maximum) =>
        values?
            .Where(static user => TryNormalizeIdentifier(user.Id, out _))
            .Take(maximum)
            .Select(ToUser)
            .ToArray()
        ?? [];

    private static TeamsDirectoryUser ToUser(Microsoft.Graph.Models.User? user)
    {
        if (user is null || !TryNormalizeIdentifier(user.Id, out var id))
            throw new InvalidDataException("The user response has no canonical ID.");

        return new TeamsDirectoryUser(id, user.DisplayName, user.UserPrincipalName, user.Mail);
    }

    private static IReadOnlyList<TeamsDirectoryGroup> ToGroups(
        IEnumerable<Microsoft.Graph.Models.Group>? values,
        int maximum) =>
        values?
            .Where(static group => TryNormalizeIdentifier(group.Id, out _))
            .Where(static group => group.SecurityEnabled == true || group.GroupTypes?.Contains("Unified", StringComparer.OrdinalIgnoreCase) == true)
            .Take(maximum)
            .Select(static group => new TeamsDirectoryGroup(
                group.Id!.Trim(),
                group.DisplayName,
                group.Mail,
                group.GroupTypes?.Contains("Unified", StringComparer.OrdinalIgnoreCase) == true
                    ? TeamsDirectoryGroupKind.Microsoft365
                    : TeamsDirectoryGroupKind.Security))
            .ToArray()
        ?? [];

    private static TeamsDirectoryGroup ToGroup(Microsoft.Graph.Models.Group? group)
    {
        if (group is null || !TryNormalizeIdentifier(group.Id, out var id))
            throw new InvalidDataException("The group response has no canonical ID.");
        if (group.SecurityEnabled != true
            && group.GroupTypes?.Contains("Unified", StringComparer.OrdinalIgnoreCase) != true)
        {
            throw new InvalidDataException("The group response is not supported for Teams authorization.");
        }

        return new TeamsDirectoryGroup(
            id,
            group.DisplayName,
            group.Mail,
            group.GroupTypes?.Contains("Unified", StringComparer.OrdinalIgnoreCase) == true
                ? TeamsDirectoryGroupKind.Microsoft365
                : TeamsDirectoryGroupKind.Security);
    }

    private static bool TryNormalizeSearch(
        string? query,
        int maximumResults,
        out string normalizedQuery,
        out int maximum,
        out string reason)
    {
        normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length < MinimumSearchLength)
        {
            maximum = 0;
            reason = "teams_directory_query_too_short";
            return false;
        }

        maximum = 0;
        if (normalizedQuery.Length > MaximumSearchLength || !TryNormalizeMaximum(maximumResults, out maximum))
        {
            reason = "teams_directory_invalid_request";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryNormalizeMaximum(int value, out int maximum)
    {
        maximum = value;
        return value is > 0 and <= MaximumResults;
    }

    private static bool TryNormalizeIdentifier(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 256;
    }

    private string? CreateGroupChatContinuation(string userId, string nextLink)
    {
        if (!IsCurrentGraphUri(nextLink))
            return null;

        var handle = Guid.NewGuid().ToString("N");
        _cache.Set(
            CacheKey("group-chat-continuation", handle),
            new GroupChatContinuation(userId, nextLink, _timeProvider.GetUtcNow() + GroupChatContinuationTtl),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = GroupChatContinuationTtl,
                Size = 1
            });
        return handle;
    }

    private bool TryGetGroupChatContinuation(string userId, string continuation, out string nextLink)
    {
        nextLink = string.Empty;
        if (!Guid.TryParseExact(continuation, "N", out _)
            || !_cache.TryGetValue(CacheKey("group-chat-continuation", continuation), out GroupChatContinuation? value)
            || value is null
            || value.ExpiresAt <= _timeProvider.GetUtcNow()
            || !string.Equals(value.UserId, userId, StringComparison.Ordinal)
            || !IsCurrentGraphUri(value.NextLink))
        {
            return false;
        }

        nextLink = value.NextLink;
        return true;
    }

    private bool IsCurrentGraphUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !Uri.TryCreate(_graphClient.RequestAdapter.BaseUrl, UriKind.Absolute, out var graphBase))
        {
            return false;
        }

        return candidate.Scheme == Uri.UriSchemeHttps
               && string.Equals(candidate.Host, graphBase.Host, StringComparison.OrdinalIgnoreCase)
               && candidate.Port == graphBase.Port;
    }

    private static string? NormalizeDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string RequireValue(string? value, string parameterName)
    {
        if (!TryNormalizeIdentifier(value, out var normalized))
            throw new ArgumentException("A non-empty bounded value is required.", parameterName);

        return normalized;
    }

    private sealed record GroupChatContinuation(string UserId, string NextLink, DateTimeOffset ExpiresAt);

    private static string EscapeODataLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsRetryable(ApiException exception) => exception.ResponseStatusCode == 429 || exception.ResponseStatusCode >= 500;

    private TimeSpan RetryDelay(ApiException exception)
    {
        if (exception.ResponseHeaders.TryGetValue("Retry-After", out var values))
        {
            var value = values.FirstOrDefault();
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                var delay = date - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                    return delay;
            }
        }

        return TimeSpan.FromMilliseconds(250);
    }

    private static string ReasonFor(ApiException exception) => exception.ResponseStatusCode switch
    {
        401 => "teams_directory_authentication_failed",
        403 => "teams_directory_permission_denied",
        429 => "teams_directory_network_unavailable",
        _ => "teams_directory_request_failed"
    };
}
