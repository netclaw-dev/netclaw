namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Tracks per-turn transient counters: tool call budget, duplicate detection,
/// empty-response retries, and force-no-tools state. Reset at the start of
/// each user turn.
/// </summary>
internal sealed class TurnStateTracker
{
    private readonly Dictionary<string, int> _toolCallHashes = new(StringComparer.Ordinal);

    public int ToolCallCount { get; set; }
    public int ToolIterationCount { get; set; }
    public bool BudgetNudgeSent { get; set; }
    public bool PostToolNudgeSent { get; set; }
    public int PreToolEmptyResponseCount { get; set; }
    public bool ForceNoToolsActive { get; set; }
    public bool DuplicateNudgeSent { get; set; }

    /// <summary>
    /// Reset all per-turn state. Called at the start of each user turn.
    /// </summary>
    public void ResetForNewTurn()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        BudgetNudgeSent = false;
        PostToolNudgeSent = false;
        PreToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
        _toolCallHashes.Clear();
        DuplicateNudgeSent = false;
    }

    /// <summary>
    /// Record a tool call hash for duplicate detection.
    /// </summary>
    public void TrackToolCall(string key)
    {
        _toolCallHashes.TryGetValue(key, out var count);
        _toolCallHashes[key] = count + 1;
    }

    /// <summary>
    /// Check whether a tool call key has been seen at least <paramref name="threshold"/> times.
    /// </summary>
    public bool HasDuplicate(string key, int threshold = 2)
    {
        return _toolCallHashes.TryGetValue(key, out var count) && count >= threshold;
    }

    /// <summary>
    /// Find the first tool call hash that meets or exceeds the given threshold.
    /// Returns null if no duplicates meet the threshold.
    /// </summary>
    public (string Key, int Count)? FindWorstDuplicate(int threshold = 3)
    {
        foreach (var (key, count) in _toolCallHashes)
        {
            if (count >= threshold)
                return (key, count);
        }

        return null;
    }

    /// <summary>
    /// Clear tool call hashes (used when resetting for buffer drain / new logical turn).
    /// </summary>
    public void ClearHashes()
    {
        _toolCallHashes.Clear();
    }
}
