// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
            input, "call_1", budget: 100, Context(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir)); // nothing spilled
    }

    [Fact]
    public async Task Over_budget_spills_full_output_and_steers()
    {
        var input = new string('H', 200) + new string('T', 200); // 400 > 100
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_2", budget: 100, Context(), NullLogger.Instance, CancellationToken.None);

        Assert.True(ToolOutputSpillLocation.TryResolve(
            _sessionDir, "call_2", out _, out var spillPath));
        Assert.True(File.Exists(spillPath));
        Assert.Equal(input, await File.ReadAllTextAsync(spillPath, CancellationToken.None)); // full output on disk
        Assert.Contains(new string('T', 100), result);                                       // inline tail only
        Assert.DoesNotContain("H", result);                                                  // inline head dropped
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
            input, "call_3", budget: 0, Context(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir));
    }

    [Fact]
    public async Task No_session_directory_degrades_to_inline_only()
    {
        var ctx = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        }).Invocation;
        var input = new string('H', 200) + new string('T', 200);

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_5", budget: 100, ctx, NullLogger.Instance, CancellationToken.None);

        var inline = result[..result.IndexOf("[output truncated", StringComparison.Ordinal)];
        Assert.Contains(new string('T', 100), inline);     // inline tail still produced
        Assert.DoesNotContain("H", inline);                // inline head dropped
        // No continuation, and the text says so. It does not offer a call id.
        Assert.Contains("did not keep the full output", result);
        Assert.DoesNotContain("CallId=", result);
    }

    [Fact]
    public async Task Missing_session_folder_is_created_so_the_first_large_result_gets_a_continuation()
    {
        // Regression: only a shell launch created the session workspace folder. A
        // session whose first large result came from another tool got a truncated
        // result with no call id and no retained text.
        var parent = Path.Combine(_sessionDir, "envelope");
        Directory.CreateDirectory(parent);
        var workspace = Path.Combine(parent, "workspace");
        var context = TestToolExecutionContext.CreateBound("session/fresh", workspace, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal }).Invocation;
        var input = new string('H', 200) + new string('M', 200) + new string('T', 200);
        Assert.False(Directory.Exists(workspace));

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_fresh", budget: 100, context, NullLogger.Instance, CancellationToken.None);

        Assert.Contains("CallId='call_fresh'", result);
        Assert.DoesNotContain("M", result[..result.IndexOf("[output truncated", StringComparison.Ordinal)]);
        var continuation = await new ToolOutputReadTool().ExecuteAsync(
            ToolInput.Create("CallId", "call_fresh", "Start", 200, "Limit", 200),
            TestToolExecutionContext.CreateBound("session/fresh", workspace, new TestToolExecutionContextOptions
            { Audience = TrustAudience.Personal }),
            CancellationToken.None);
        Assert.StartsWith(new string('M', 100), continuation);
    }

    [Fact]
    public async Task A_spill_that_cannot_be_written_says_so_and_logs_a_warning()
    {
        // A regular file is at the session folder path, so the folder cannot exist.
        var blocked = Path.Combine(_sessionDir, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "file", CancellationToken.None);
        var context = TestToolExecutionContext.CreateBound("session/blocked", blocked, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal }).Invocation;
        var logger = new RecordingLogger();

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            new string('x', 400), "call_blocked", budget: 100, context, logger, CancellationToken.None);

        Assert.Contains("[output truncated to 100 chars of 400", result);
        Assert.Contains("did not keep the full output, so tool_output_read cannot continue this call", result);
        Assert.Contains("skill_read_resource", result);
        Assert.DoesNotContain("file_read", result);
        Assert.DoesNotContain("shell", result);
        Assert.DoesNotContain("CallId=", result);
        Assert.DoesNotContain(blocked, result, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("tool_output_spill_not_retained", warning.Message);
        Assert.Contains("call_blocked", warning.Message);
        Assert.Contains("reason=WriteFailed", warning.Message);
    }

    [Fact]
    public async Task A_retained_spill_logs_no_warning()
    {
        var logger = new RecordingLogger();

        await ToolOutputSpill.BoundAndSpillAsync(
            new string('x', 400), "call_ok", budget: 100, Context(), logger, CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Unsafe_call_id_does_not_create_a_spill()
    {
        var input = new string('H', 200) + new string('T', 200);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "../../evil", budget: 100, Context(), NullLogger.Instance, CancellationToken.None);

        Assert.False(Directory.Exists(ToolCallsDir));
        Assert.DoesNotContain("CallId=", result);
        Assert.Contains("did not keep the full output", result);
    }

    [Fact]
    public async Task Spill_rejects_a_dangling_link_at_the_session_folder_path()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = Path.Combine(Path.GetTempPath(), "nc-spill-dangling-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(_sessionDir, "dangling-link");
        Directory.CreateSymbolicLink(link, target);
        var context = TestToolExecutionContext.CreateBound(
            "session/dangling",
            link,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal }).Invocation;

        var logger = new RecordingLogger();

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            new string('x', 100), "call_dangling", budget: 5, context, logger, CancellationToken.None);

        // The link check refuses the path before the creation. Without the check,
        // CreateDirectory fails on the link and the reason is WriteFailed.
        Assert.DoesNotContain("CallId=", result);
        Assert.Contains("did not keep the full output", result);
        Assert.Contains("reason=UnsafeSessionFolder", Assert.Single(logger.Entries).Message);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Continuation_reads_only_the_requested_character_window()
    {
        var input = string.Concat(Enumerable.Range(0, 100).Select(static index => index.ToString("D2")));
        await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_window", budget: 20, Context(), NullLogger.Instance, CancellationToken.None);
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
            content + new string('x', 100), "call_complete", budget: 5, Context(), NullLogger.Instance, CancellationToken.None);

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
            new string('s', 200), callId, budget: 20, Context(), NullLogger.Instance, CancellationToken.None);
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
            content + new string('x', 100), callId, budget: 5, Context(), NullLogger.Instance, CancellationToken.None);

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
                new string('x', 100), "call_link", budget: 5, Context(), NullLogger.Instance, CancellationToken.None);

            Assert.DoesNotContain("CallId=", result);
            Assert.Contains("did not keep the full output", result);
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
                new string('x', 100), "call_linked_session", budget: 5, context, NullLogger.Instance, CancellationToken.None);

            Assert.DoesNotContain("CallId=", result);
            Assert.Contains("did not keep the full output", result);
            Assert.False(Directory.Exists(Path.Combine(target, "tool-calls")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
