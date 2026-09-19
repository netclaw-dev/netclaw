// -----------------------------------------------------------------------
// <copyright file="TeamsDirectorySearchController.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Teams;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// Limits interactive directory work to one current request. A newer request
/// cancels the prior request before it can update a configuration screen.
/// </summary>
internal sealed class TeamsDirectorySearchController : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GroupChatContinuationDelay = TimeSpan.FromSeconds(1);
    private readonly ITeamsDirectory _directory;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _currentRequest;
    private long _generation;

    public TeamsDirectorySearchController(ITeamsDirectory directory, TimeProvider timeProvider)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.SearchTeamsAsync(query, TeamsGraphSearchLimits.MaximumResults, token),
            cancellationToken);

    public ValueTask<TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
        string teamId,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.GetChannelsAsync(teamId, TeamsGraphSearchLimits.MaximumResults, token),
            cancellationToken);

    public ValueTask<TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.SearchUsersAsync(query, TeamsGraphSearchLimits.MaximumResults, token),
            cancellationToken);

    public ValueTask<TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.SearchGroupsAsync(query, TeamsGraphSearchLimits.MaximumResults, token),
            cancellationToken);

    public ValueTask<TeamsDirectorySearchResponse<TeamsDirectoryGroupChatPage>> GetGroupChatsAsync(
        string userId,
        string? continuation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.GetGroupChatsAsync(
                userId,
                TeamsGraphSearchLimits.MaximumResults,
                continuation,
                token),
            cancellationToken);

    public ValueTask<TeamsDirectorySearchResponse<TeamsDirectoryGroupChatSearchPage>> SearchGroupChatsAsync(
        string query,
        string? continuation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            token => _directory.SearchGroupChatsAsync(
                query,
                TeamsGraphSearchLimits.MaximumResults,
                continuation,
                token),
            cancellationToken,
            continuation is null ? DebounceDelay : GroupChatContinuationDelay);

    /// <summary>
    /// Invalidates a result when its input or owning screen changes. A provider
    /// that ignores cancellation still fails the generation check on return.
    /// </summary>
    public void Invalidate()
    {
        _currentRequest?.Cancel();
        _currentRequest?.Dispose();
        _currentRequest = null;
        _generation++;
    }

    public bool IsCurrent(long generation) => generation == _generation;

    private ValueTask<TeamsDirectorySearchResponse<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<TeamsDirectoryOperationResult<T>>> operation,
        CancellationToken cancellationToken)
        => ExecuteAsync(operation, cancellationToken, DebounceDelay);

    private async ValueTask<TeamsDirectorySearchResponse<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<TeamsDirectoryOperationResult<T>>> operation,
        CancellationToken cancellationToken,
        TimeSpan debounceDelay)
    {
        _currentRequest?.Cancel();
        _currentRequest?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentRequest = request;
        var generation = ++_generation;

        try
        {
            await Task.Delay(debounceDelay, _timeProvider, request.Token).ConfigureAwait(false);
            var result = await operation(request.Token).ConfigureAwait(false);
            return new TeamsDirectorySearchResponse<T>(generation == _generation, generation, result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return new TeamsDirectorySearchResponse<T>(false, generation, TeamsDirectoryOperationResult<T>.Unavailable("teams_directory_search_cancelled"));
        }
    }

    public void Dispose()
    {
        Invalidate();
    }
}

internal static class TeamsGraphSearchLimits
{
    public const int MaximumResults = 25;
}

internal sealed record TeamsDirectorySearchResponse<T>(
    bool IsCurrent,
    long Generation,
    TeamsDirectoryOperationResult<T> Result);
