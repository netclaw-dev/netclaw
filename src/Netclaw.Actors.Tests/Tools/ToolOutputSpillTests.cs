// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolOutputSpillTests : IDisposable
{
    private readonly string _sessionDir =
        Path.Combine(Path.GetTempPath(), "nc-spill-" + Guid.NewGuid().ToString("N"));

    public ToolOutputSpillTests() => Directory.CreateDirectory(_sessionDir);

    public void Dispose()
    {
        if (Directory.Exists(_sessionDir))
            Directory.Delete(_sessionDir, recursive: true);
    }

    private ToolInvocationContext Context() =>
        TestToolExecutionContext.CreateBound("session/thread", _sessionDir, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal }).Invocation;

    private string ToolCallsDir => Path.Combine(_sessionDir, "tool-calls");

    // BoundAndSpillAsync receives an ALREADY-redacted result (the dispatcher redacts
    // first), so these tests pass content verbatim and assert the bound/spill shape.

    [Fact]
    public async Task Under_budget_returned_unchanged()
    {
        var input = new string('a', 50);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_1", budget: 100, Context(), CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir)); // nothing spilled
    }

    [Fact]
    public async Task Over_budget_spills_full_output_and_steers()
    {
        var input = new string('H', 200) + new string('T', 200); // 400 > 100
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_2", budget: 100, Context(), CancellationToken.None);

        Assert.True(ToolOutputSpillLocation.TryResolve(
            _sessionDir, "call_2", out _, out var spillPath));
        Assert.True(File.Exists(spillPath));
        Assert.Equal(input, await File.ReadAllTextAsync(spillPath, CancellationToken.None)); // full output on disk
        Assert.StartsWith(new string('H', 50), result);                                      // inline head
        Assert.Contains("tool_output_read", result);
        Assert.Contains("CallId='call_2'", result);
        Assert.DoesNotContain(spillPath, result);
    }

    [Fact]
    public async Task Budget_zero_falls_back_to_content_default()
    {
        // budget 0 → DefaultContentBudget (12000); a 5000-char input fits, returned whole.
        var input = new string('x', 5000);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_3", budget: 0, Context(), CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir));
    }

    [Fact]
    public async Task No_session_directory_degrades_to_inline_only()
    {
        var ctx = TestToolExecutionContext.CreateBound("session/thread", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        }).Invocation;
        var input = new string('H', 200) + new string('T', 200);

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_5", budget: 100, ctx, CancellationToken.None);

        Assert.StartsWith(new string('H', 50), result);    // inline still produced
        Assert.DoesNotContain("tool_output_read", result);   // but no continuation
    }

    [Fact]
    public async Task Unsafe_call_id_does_not_create_a_spill()
    {
        var input = new string('H', 200) + new string('T', 200);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "../../evil", budget: 100, Context(), CancellationToken.None);

        Assert.False(Directory.Exists(ToolCallsDir));
        Assert.DoesNotContain("tool_output_read", result);
    }

    [Fact]
    public async Task Continuation_reads_only_the_requested_character_window()
    {
        var input = string.Concat(Enumerable.Range(0, 100).Select(static index => index.ToString("D2")));
        await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_window", budget: 20, Context(), CancellationToken.None);
        var tool = new ToolOutputReadTool();

        var result = await tool.ExecuteAsync(
            ToolInput.Create("CallId", "call_window", "Start", 20, "Limit", 128),
            Context(),
            CancellationToken.None);

        Assert.StartsWith(input.Substring(20, 40), result, StringComparison.Ordinal);
        Assert.Contains("next_start=", result, StringComparison.Ordinal);
        Assert.Contains("complete=false", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 128);
    }

    [Fact]
    public async Task Final_continuation_reports_completion_inside_the_limit()
    {
        const string content = "short retained result";
        await ToolOutputSpill.BoundAndSpillAsync(
            content + new string('x', 100), "call_complete", budget: 5, Context(), CancellationToken.None);

        var result = await new ToolOutputReadTool().ExecuteAsync(
            ToolInput.Create("CallId", "call_complete", "Start", content.Length + 100, "Limit", 128),
            Context(),
            CancellationToken.None);

        Assert.Contains("complete=true", result, StringComparison.Ordinal);
        Assert.Contains("next_start=none", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 128);
    }

    [Theory]
    [InlineData("../call")]
    [InlineData("call/other")]
    [InlineData("call\nother")]
    [InlineData("")]
    public async Task Continuation_rejects_path_like_or_invalid_call_ids(string callId)
    {
        var tool = new ToolOutputReadTool();

        var result = await tool.ExecuteAsync(
            ToolInput.Create("CallId", callId),
            Context(),
            CancellationToken.None);

        Assert.Contains("opaque identifier", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuation_cannot_read_another_session_spill()
    {
        const string callId = "call_private";
        await ToolOutputSpill.BoundAndSpillAsync(
            new string('s', 200), callId, budget: 20, Context(), CancellationToken.None);
        var otherSession = Path.Combine(Path.GetTempPath(), "nc-spill-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherSession);
        try
        {
            var otherContext = TestToolExecutionContext.CreateBound(
                "session/other",
                otherSession,
                new TestToolExecutionContextOptions { Audience = TrustAudience.Personal }).Invocation;

            var result = await new ToolOutputReadTool().ExecuteAsync(
                ToolInput.Create("CallId", callId),
                otherContext,
                CancellationToken.None);

            Assert.Contains("No retained output exists", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(otherSession, recursive: true);
        }
    }

    [Fact]
    public async Task Continuation_returns_not_found_for_a_missing_spill()
    {
        var result = await new ToolOutputReadTool().ExecuteAsync(
            ToolInput.Create("CallId", "call_missing"),
            Context(),
            CancellationToken.None);

        Assert.Contains("No retained output exists", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 128, "Start")]
    [InlineData(256001, 128, "Start")]
    [InlineData(0, 0, "Limit")]
    [InlineData(0, 127, "Limit")]
    [InlineData(0, 10001, "Limit")]
    public async Task Continuation_rejects_out_of_range_windows(int start, int limit, string parameter)
    {
        var result = await new ToolOutputReadTool().ExecuteAsync(
            ToolInput.Create("CallId", "call_range", "Start", start, "Limit", limit),
            Context(),
            CancellationToken.None);

        Assert.Contains(parameter, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opaque_provider_punctuation_round_trips_through_the_hash_name()
    {
        const string callId = "provider:call.123=value";
        const string content = "retained output";
        await ToolOutputSpill.BoundAndSpillAsync(
            content + new string('x', 100), callId, budget: 5, Context(), CancellationToken.None);

        var result = await new ToolOutputReadTool().ExecuteAsync(
            ToolInput.Create("CallId", callId, "Limit", 128),
            Context(),
            CancellationToken.None);

        Assert.StartsWith(content, result, StringComparison.Ordinal);
        Assert.Contains("complete=false", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuation_rejects_a_symlinked_tool_calls_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(Path.GetTempPath(), "nc-spill-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(ToolCallsDir, outside);
        try
        {
            var result = await ToolOutputSpill.BoundAndSpillAsync(
                new string('x', 100), "call_link", budget: 5, Context(), CancellationToken.None);

            Assert.DoesNotContain("tool_output_read", result);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(ToolCallsDir);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Spill_rejects_a_symlinked_session_root_before_directory_creation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = Path.Combine(Path.GetTempPath(), "nc-spill-session-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "nc-spill-session-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var context = TestToolExecutionContext.CreateBound(
                "session/link",
                link,
                new TestToolExecutionContextOptions { Audience = TrustAudience.Personal }).Invocation;

            var result = await ToolOutputSpill.BoundAndSpillAsync(
                new string('x', 100), "call_linked_session", budget: 5, context, CancellationToken.None);

            Assert.DoesNotContain("tool_output_read", result);
            Assert.False(Directory.Exists(Path.Combine(target, "tool-calls")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }
}
