// -----------------------------------------------------------------------
// <copyright file="RecordingSessionMetrics.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Records every <see cref="ISessionMetrics.RecordTokenUsage"/> call so a test can
/// assert a sub-agent bills each LLM call to the daily-stats sink. Thread-safe: the
/// actor records on its mailbox thread while the test reads after the <c>Ask</c>
/// completes. Regression support for issue #1597.
/// </summary>
internal sealed class RecordingSessionMetrics : ISessionMetrics
{
    private readonly object _gate = new();
    private readonly List<(long Input, long Output)> _tokenUsageCalls = [];
    private long _totalInput;
    private long _totalOutput;

    public IReadOnlyList<(long Input, long Output)> TokenUsageCalls
    {
        get { lock (_gate) { return _tokenUsageCalls.ToArray(); } }
    }

    public long TotalInputTokens { get { lock (_gate) { return _totalInput; } } }

    public long TotalOutputTokens { get { lock (_gate) { return _totalOutput; } } }

    public void RecordTokenUsage(long inputTokens, long outputTokens)
    {
        lock (_gate)
        {
            _tokenUsageCalls.Add((inputTokens, outputTokens));
            _totalInput += inputTokens;
            _totalOutput += outputTokens;
        }
    }

    public void RecordTurnCompleted() { }
    public void RecordSessionCreated() { }
    public void RecordMemoriesFormed(int count) { }
    public void RecordMemoriesRecalled(int count) { }
    public void RecordSkillsLoaded(int count) { }
    public void RecordSkillLoaded(string skillName, SkillLoadMethod method) { }
}
