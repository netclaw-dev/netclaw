// -----------------------------------------------------------------------
// <copyright file="HeadlessChannelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests;

/// <summary>
/// Covers C1: the discard-and-resume mechanism (<c>LlmSessionActor.TryResumeAfterTimeout</c>)
/// emits <see cref="TextStreamDiscarded"/> before a resumed call streams its own
/// deltas. <see cref="HeadlessChannel"/> accumulates <see cref="TextDeltaOutput"/>
/// text into its JSON envelope response buffer — without honoring the discard
/// signal, a dead call's partial text glues onto the resumed call's answer. These
/// tests drive <see cref="HeadlessChannel.HandleOutput"/> directly (an internal
/// test seam) with a real multi-delta stall, then assert on the DELTA-accumulated
/// buffer, not <see cref="TextOutput"/>.
/// </summary>
public sealed class HeadlessChannelTests
{
    private static HeadlessChannel CreateChannel(bool jsonOutput) => new(
        new DaemonClient("http://127.0.0.1:1"), // never dialed in this test
        new NetclawPaths(),
        new FakeApplicationLifetime(),
        TimeProvider.System,
        new HeadlessOptions("test prompt") { JsonOutput = jsonOutput },
        NullLogger<HeadlessChannel>.Instance);

    [Fact]
    public void TextStreamDiscarded_clears_json_envelope_buffer_between_dead_and_resumed_deltas()
    {
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/test");

        // Real multi-delta stall — two substantive deltas before discard, matching
        // a genuine half-open provider stream (a single delta would not exercise
        // the buffered-first-delta path the way a real stall does).
        channel.HandleOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = sessionId }, null);

        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        channel.HandleOutput(new TextDeltaOutput("Resumed answer ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("after timeout") { SessionId = sessionId }, null);

        // The JSON envelope's Response field is built from this buffer — it must
        // contain ONLY the resumed call's text.
        Assert.Equal("Resumed answer after timeout", channel.ResponseBufferForTesting);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", channel.ResponseBufferForTesting, StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_is_a_no_op_when_no_deltas_streamed_yet()
    {
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/test-empty");

        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("first answer") { SessionId = sessionId }, null);

        Assert.Equal("first answer", channel.ResponseBufferForTesting);
    }

    [Fact]
    public void TextStreamDiscarded_preserves_an_earlier_completed_calls_text_but_discards_only_the_dead_calls_partial()
    {
        // D1: the JSON envelope buffer was TURN-scoped, so TextStreamDiscarded's
        // Clear() wiped an earlier COMPLETED call's already-delivered text along
        // with the dead call's partial. This mirrors ChatPage's call-scoped
        // segment semantics: TextOutput marks a call boundary and commits
        // everything before it, so a later discard can only ever remove text
        // from the call that is actually dying.
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/preamble-preserved");

        // Call 1: streams a preamble, then completes (a tool round).
        channel.HandleOutput(new TextDeltaOutput("Checking the files now. ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextOutput("Checking the files now. ") { SessionId = sessionId }, null);

        // Call 2: streams two real deltas, then dies mid-stream and is discarded.
        channel.HandleOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        // Resumed call streams the real final answer.
        channel.HandleOutput(new TextDeltaOutput("Done: the answer is X.") { SessionId = sessionId }, null);

        Assert.Equal("Checking the files now. Done: the answer is X.", channel.ResponseBufferForTesting);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", channel.ResponseBufferForTesting, StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_lets_TextOutput_repopulate_after_discard()
    {
        // D2: the discard arm cleared _responseBuffer but left
        // _receivedTextDeltaInCurrentTurn true, so a resumed call that never
        // streams a delta (SessionLlmInvoker withholds the first delta until a
        // second arrives — a single-chunk response never gets a second) hit the
        // "already streamed this turn" branch of the TextOutput case and skipped
        // the append entirely. A successful turn then reported an EMPTY response
        // — worse than the pre-fix loud failure.
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/single-chunk-resume");

        channel.HandleOutput(new TextDeltaOutput("stalled") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        // No further deltas — the resumed call's answer arrives as a single,
        // non-streamed TextOutput.
        channel.HandleOutput(new TextOutput("Resumed answer") { SessionId = sessionId }, null);

        Assert.Equal("Resumed answer", channel.ResponseBufferForTesting);
    }

    [Fact]
    public void TextOutput_with_IsCallBoundary_false_does_not_move_the_commit_marker_past_a_live_calls_partial_text()
    {
        // F2: EmitExpiredPromptNotice/EmitWrongRequesterApprovalNotice/
        // EmitUnavailableApprovalOptionNotice send a mid-stream TextOutput
        // (IsCallBoundary = false) while another call still streams.
        // Before the fix, ANY TextOutput advanced the commit marker over the
        // live call's partial text; a subsequent stall+discard then found
        // nothing left to remove, and the resumed call's answer glued onto
        // the dead partial.
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/notice-mid-stream");

        // Call: streams two real deltas.
        channel.HandleOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = sessionId }, null);

        // A notice fires mid-stream — a non-call-boundary TextOutput. Its own
        // text is handled exactly as today (logged only, since a delta is
        // already in flight); only its effect on the commit marker changes.
        channel.HandleOutput(new TextOutput("That approval prompt has expired.")
        {
            SessionId = sessionId,
            IsCallBoundary = false
        }, null);

        // The live call then dies and is discarded.
        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        // Resumed call streams the real final answer.
        channel.HandleOutput(new TextDeltaOutput("Done: the answer is X.") { SessionId = sessionId }, null);

        Assert.Equal("Done: the answer is X.", channel.ResponseBufferForTesting);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", channel.ResponseBufferForTesting, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageOutput_starts_on_its_own_line_after_a_streamed_turn()
    {
        // F1 regression: the "any text printed this turn" flag was narrowed to
        // a CALL-scoped flag that the final TextOutput always resets before
        // UsageOutput arrives (the actor always emits text then usage for a
        // completed call), so the newline guard never fired and [usage] glued
        // onto the end of the streamed answer with no line break between them.
        var channel = CreateChannel(jsonOutput: false);
        var sessionId = new SessionId("headless/usage-newline");

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            channel.HandleOutput(new TextDeltaOutput("Hello ") { SessionId = sessionId }, null);
            channel.HandleOutput(new TextDeltaOutput("world.") { SessionId = sessionId }, null);
            channel.HandleOutput(new TextOutput("Hello world.") { SessionId = sessionId }, null);
            channel.HandleOutput(new UsageOutput
            {
                SessionId = sessionId,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15
            }, null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("world.[usage]", output, StringComparison.Ordinal);
        Assert.Contains("\n[usage]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageOutput_omits_discarded_est_in_when_null_but_keeps_discarded_attempts()
    {
        // F3: a resume can happen without any completed call in the session
        // ever reporting real usage (see D4 in LlmSessionActor) — the
        // estimate is then null. The console line must omit discarded_est_in=
        // entirely rather than print it as an empty token, while
        // discarded_attempts= (a real, always-known count) still prints.
        var channel = CreateChannel(jsonOutput: false);
        var sessionId = new SessionId("headless/discarded-null-estimate");

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            channel.HandleOutput(new UsageOutput
            {
                SessionId = sessionId,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                DiscardedResumeEstimatedInputTokens = null,
                DiscardedResumeAttempts = 1
            }, null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = stdout.ToString();
        Assert.DoesNotContain("discarded_est_in=", output, StringComparison.Ordinal);
        Assert.Contains("discarded_attempts=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageOutput_prints_discarded_est_in_when_a_real_estimate_is_available()
    {
        // Companion to the null case above: once an earlier call in the
        // session has reported real usage, the estimate must still print.
        var channel = CreateChannel(jsonOutput: false);
        var sessionId = new SessionId("headless/discarded-real-estimate");

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            channel.HandleOutput(new UsageOutput
            {
                SessionId = sessionId,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                DiscardedResumeEstimatedInputTokens = 42,
                DiscardedResumeAttempts = 2
            }, null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = stdout.ToString();
        Assert.Contains("discarded_est_in=42 discarded_attempts=2", output, StringComparison.Ordinal);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }
}
