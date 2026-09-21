// -----------------------------------------------------------------------
// <copyright file="TeamsDirectoryContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Channels.Teams;

/// <summary>
/// The supported Entra group classes exposed by the Teams directory boundary.
/// </summary>
public enum TeamsDirectoryGroupKind
{
    Security,
    Microsoft365
}

/// <summary>
/// A canonical Entra user record safe to display in the operator UI.
/// </summary>
public sealed record TeamsDirectoryUser(
    string Id,
    string? DisplayName,
    string? UserPrincipalName,
    string? Mail);

/// <summary>
/// A canonical Entra group record safe to display in the operator UI.
/// </summary>
public sealed record TeamsDirectoryGroup(
    string Id,
    string? DisplayName,
    string? Mail,
    TeamsDirectoryGroupKind Kind);

/// <summary>
/// A canonical Microsoft Teams team record safe to display in the operator UI.
/// </summary>
public sealed record TeamsDirectoryTeam(
    string Id,
    string? DisplayName,
    string? Description);

/// <summary>
/// A canonical Microsoft Teams channel record safe to display in the operator UI.
/// </summary>
public sealed record TeamsDirectoryChannel(
    string TeamId,
    string Id,
    string? DisplayName,
    string? Description);

/// <summary>
/// A display-only Group Chat record. The ID remains the only value that can
/// enter Teams configuration or authorization.
/// </summary>
public sealed record TeamsDirectoryGroupChat(
    string Id,
    string? Topic,
    IReadOnlyList<string> ParticipantPreview);

/// <summary>
/// One bounded Group Chat discovery page. Continuation is opaque to callers.
/// It cannot be persisted or reused for a different selected user.
/// </summary>
public sealed record TeamsDirectoryGroupChatPage(
    IReadOnlyList<TeamsDirectoryGroupChat> Chats,
    string? Continuation);

/// <summary>
/// One bounded name-search page. Counters include all previous pages.
/// A continuation means the tenant search is not complete.
/// </summary>
public sealed record TeamsDirectoryGroupChatSearchPage(
    IReadOnlyList<TeamsDirectoryGroupChat> Chats,
    string? Continuation,
    int UsersExamined,
    int UnavailableUsers,
    int RequestsMade,
    int ChatsExamined);

/// <summary>
/// A safe, stable outcome classification for directory operations. Reason codes
/// are deliberately non-diagnostic and never carry credentials or principal IDs.
/// </summary>
public enum TeamsDirectoryOperationStatus
{
    Available,
    Unavailable,
    InvalidRequest
}

/// <summary>
/// Result from one bounded directory operation.
/// </summary>
public sealed record TeamsDirectoryOperationResult<T>(
    TeamsDirectoryOperationStatus Status,
    T? Value,
    string? ReasonCode = null)
{
    public bool IsAvailable => Status == TeamsDirectoryOperationStatus.Available;

    public static TeamsDirectoryOperationResult<T> Available(T value) =>
        new(TeamsDirectoryOperationStatus.Available, value);

    public static TeamsDirectoryOperationResult<T> Unavailable(string reasonCode) =>
        new(TeamsDirectoryOperationStatus.Unavailable, default, reasonCode);

    public static TeamsDirectoryOperationResult<T> InvalidRequest(string reasonCode) =>
        new(TeamsDirectoryOperationStatus.InvalidRequest, default, reasonCode);
}

/// <summary>
/// SDK-neutral, bounded Microsoft 365 directory operations required by Teams
/// configuration and principal authorization. Implementations must never expose
/// Graph SDK models through this contract.
/// </summary>
public interface ITeamsDirectory
{
    ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
        string teamId,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
        string teamId,
        int maximumResults,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
        string teamId,
        string channelId,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
        string userId,
        CancellationToken cancellationToken = default);

    ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
        string userId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches Group Chat topics across tenant users. The query never filters
    /// user identities. Continuations are opaque and bound to this query.
    /// </summary>
    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>> SearchGroupChatsAsync(
        string query,
        int maximumResults,
        string? continuation = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Unavailable(
                "teams_directory_group_chat_discovery_unavailable"));

    /// <summary>
    /// Gets one selected user's Group Chat metadata page. Implementations must
    /// keep Graph continuation URLs private and validate opaque continuations.
    /// </summary>
    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatPage>> GetGroupChatsAsync(
        string userId,
        int maximumResults,
        string? continuation = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            TeamsDirectoryOperationResult<TeamsDirectoryGroupChatPage>.Unavailable(
                "teams_directory_group_chat_discovery_unavailable"));

    /// <summary>
    /// Gets one saved Group Chat's display metadata. A missing permission only
    /// affects presentation; it never changes the saved canonical authority.
    /// </summary>
    ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChat>> GetGroupChatAsync(
        string chatId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            TeamsDirectoryOperationResult<TeamsDirectoryGroupChat>.Unavailable(
                "teams_directory_group_chat_discovery_unavailable"));
}

/// <summary>
/// Reads an already cached user record for presentation only. Implementations
/// must never start directory I/O from this synchronous boundary.
/// </summary>
public interface ITeamsDirectoryUserCache
{
    bool TryGetCachedUser(string userId, out TeamsDirectoryUser user);
}
