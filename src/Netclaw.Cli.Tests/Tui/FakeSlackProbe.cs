using Netclaw.Channels.Slack;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Test double for <see cref="ISlackProbe"/> that returns canned results
/// without making real HTTP calls.
/// </summary>
public sealed class FakeSlackProbe : ISlackProbe
{
    /// <summary>
    /// The result to return from the next <see cref="ProbeAsync"/> call.
    /// Defaults to a successful probe.
    /// </summary>
    public SlackProbeResult NextResult { get; set; } = new(
        true, null, "Test Team", "U12345");

    /// <summary>
    /// Number of times <see cref="ProbeAsync"/> has been called.
    /// </summary>
    public int ProbeCallCount { get; private set; }

    /// <summary>
    /// The bot token from the last call.
    /// </summary>
    public string? LastBotToken { get; private set; }

    public Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastBotToken = botToken;
        return Task.FromResult(NextResult);
    }
}
