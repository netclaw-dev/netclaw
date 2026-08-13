// -----------------------------------------------------------------------
// <copyright file="InlineChatPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tools;
using Termina;
using Termina.Clipboard;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Tui;

public sealed class InlineChatPageTests
{
    private static readonly SessionId SessionId = new("test/chat");

    [Fact]
    public async Task ShiftEnter_AddsNewline_AndEnterSubmitsExactText()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Input.EnqueueString("first line");
        harness.Input.EnqueueKey(ConsoleKey.Enter, shift: true);
        harness.Input.EnqueueString("second line");
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        var submitted = await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token);

        Assert.Equal("first line\nsecond line", submitted);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("second line"));
        await harness.StopAsync(runTask);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public async Task ModifiedEnterUnavailable_OmitsTheUnavailableShortcut(int width)
    {
        await using var harness = CreateHarness(width: width);
        var runTask = harness.StartAsync();

        harness.Events.Enqueue(new TerminalInputCapabilitiesChanged(
            new TerminalInputCapabilities(
                TerminalCapabilityAvailability.Unavailable,
                TerminalInputCapabilitySource.LegacyTerminal)));

        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Enter send"));
        Assert.DoesNotContain("Shift+Enter", harness.Terminal.ToString(), StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ModifiedEnterAvailable_ShowsTheNewlineShortcut()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Events.Enqueue(new TerminalInputCapabilitiesChanged(
            new TerminalInputCapabilities(
                TerminalCapabilityAvailability.Available,
                TerminalInputCapabilitySource.KittyKeyboardProtocol)));

        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Shift+Enter newline"));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task HistoryUp_RecallsThePreviousPrompt()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Input.EnqueueString("previous prompt");
        harness.Input.EnqueueKey(ConsoleKey.Enter);
        Assert.Equal("previous prompt",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));

        harness.Input.EnqueueString("saved draft");
        harness.Input.EnqueueKey(ConsoleKey.UpArrow);
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        Assert.Equal("previous prompt",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task HistoryDown_RestoresTheSavedDraft()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Input.EnqueueString("previous prompt");
        harness.Input.EnqueueKey(ConsoleKey.Enter);
        Assert.Equal("previous prompt",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));

        harness.Input.EnqueueString("saved draft");
        harness.Input.EnqueueKey(ConsoleKey.UpArrow);
        harness.Input.EnqueueKey(ConsoleKey.DownArrow);
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        Assert.Equal("saved draft",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task DoubleEscape_ClearsRecalledInput()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Input.EnqueueString("previous prompt");
        harness.Input.EnqueueKey(ConsoleKey.Enter);
        _ = await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token);
        harness.Input.EnqueueKey(ConsoleKey.UpArrow);
        harness.Input.EnqueueKey(ConsoleKey.Escape);
        harness.Input.EnqueueKey(ConsoleKey.Escape);
        harness.Input.EnqueueString("replacement");
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        Assert.Equal("replacement",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task OneEscape_DoesNotClearInput()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.Input.EnqueueString("keep this");
        harness.Input.EnqueueKey(ConsoleKey.Escape);
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        Assert.Equal("keep this",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task MultilinePaste_SubmitsTheExactOriginalText()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();
        const string pasted = "first pasted line\nsecond pasted line";

        harness.Events.Enqueue(new PasteEvent(pasted));
        harness.Events.Enqueue(new KeyPressed(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)));

        Assert.Equal(pasted,
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Approval_BlocksPasteFromTheHiddenComposer()
    {
        await using var harness = CreateHarness(approval: BuildApproval());
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Approval required"));

        harness.Events.Enqueue(new PasteEvent("blocked paste"));
        harness.Events.Enqueue(new KeyPressed(
            new ConsoleKeyInfo('\0', ConsoleKey.O, false, false, true)));
        await harness.WaitUntilAsync(() => harness.ViewModel.IsApprovalDetailVisible.Value);
        harness.Input.EnqueueKey(ConsoleKey.Escape);
        _ = await harness.ViewModel.ReadApprovalAsync(harness.Cancellation.Token);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("MESSAGE"));

        harness.Input.EnqueueString("safe prompt");
        harness.Input.EnqueueKey(ConsoleKey.Enter);
        Assert.Equal("safe prompt",
            await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task CtrlO_KeepsTheApprovalSelection()
    {
        var approval = BuildApproval();
        await using var harness = CreateHarness(approval: approval);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Approval required"));

        harness.Input.EnqueueKey(ConsoleKey.DownArrow);
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        harness.Input.EnqueueKey(ConsoleKey.Enter);

        Assert.Equal(ApprovalOptionKeys.ApproveSession,
            await harness.ViewModel.ReadApprovalAsync(harness.Cancellation.Token));
        Assert.True(harness.ViewModel.IsApprovalDetailVisible.Value);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Escape_DeniesAnApproval()
    {
        await using var harness = CreateHarness(approval: BuildApproval());
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Approval required"));
        var screen = harness.Terminal.ToString();
        Assert.Contains("Netclaw requests permission to run shell_execute", screen, StringComparison.Ordinal);
        Assert.Contains("This chat — until this chat ends", screen, StringComparison.Ordinal);
        Assert.Contains("Deny — do not run", screen, StringComparison.Ordinal);
        AssertHasNoDecorativeTrim(screen);

        harness.Input.EnqueueKey(ConsoleKey.Escape);

        Assert.Equal(ApprovalOptionKeys.Deny,
            await harness.ViewModel.ReadApprovalAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ParallelApprovals_ShowOneDecisionGateAndQueueTheRemainingRequests()
    {
        var firstApproval = BuildApproval() with
        {
            CallId = new ToolCallId("call-a"),
            DisplayText = "first protected command"
        };
        var secondApproval = BuildApproval() with
        {
            CallId = new ToolCallId("call-b"),
            DisplayText = "second protected command"
        };
        var outputs = new SessionOutput[]
        {
            ToolCall("call-a", "shell_execute", 1),
            ToolCall("call-b", "shell_execute", 2),
            firstApproval,
            secondApproval
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("Approval required  1 of 2")
            && harness.Terminal.Contains("first protected command")
            && harness.Terminal.Contains("Waiting"));

        var firstScreen = harness.Terminal.ToString();
        Assert.Contains("Decision Inspect call-a", firstScreen, StringComparison.Ordinal);
        Assert.Contains("Waiting  Inspect call-b", firstScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("second protected command", firstScreen, StringComparison.Ordinal);

        harness.ViewModel.Emit(new ApprovalOutcomeOutput
        {
            SessionId = SessionId,
            TimestampMs = 3,
            CallId = new ToolCallId("call-a"),
            ToolName = new ToolName("shell_execute"),
            SelectedKey = ApprovalOptionKeys.ApproveOnceKey
        });
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("second protected command"));

        var secondScreen = harness.Terminal.ToString();
        Assert.DoesNotContain("Approval required  1 of 2", secondScreen, StringComparison.Ordinal);
        Assert.Contains("Approval required  Netclaw", secondScreen, StringComparison.Ordinal);
        Assert.Contains("Decision Inspect call-b", secondScreen, StringComparison.Ordinal);
        Assert.NotNull(harness.Focus.CurrentFocus);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task CtrlO_ShowsApprovalSecurityContext()
    {
        var approval = BuildApproval() with
        {
            Patterns = ["dotnet"],
            CandidateVerbs = ["dotnet"],
            Cwd = "/work/netclaw",
            IsMessy = true,
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            PersistedAdoptedContext = true
        };
        await using var harness = CreateHarness(approval: approval);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Approval required"));

        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        var screen = string.Empty;
        await harness.WaitUntilAsync(() =>
        {
            screen = harness.Terminal.ToString();
            return screen.Contains("Requester: Netclaw", StringComparison.Ordinal)
                   && screen.Contains("Action: Run shell_execute", StringComparison.Ordinal)
                   && screen.Contains("Patterns: dotnet", StringComparison.Ordinal)
                   && screen.Contains("Verbs: dotnet", StringComparison.Ordinal)
                   && screen.Contains("Directory: /work/netclaw", StringComparison.Ordinal)
                   && screen.Contains("Complex command", StringComparison.Ordinal)
                   && screen.Contains("third-party context", StringComparison.Ordinal);
        });

        Assert.Contains("Patterns: dotnet", screen, StringComparison.Ordinal);
        Assert.Contains("Verbs: dotnet", screen, StringComparison.Ordinal);
        Assert.Contains("Requester: Netclaw", screen, StringComparison.Ordinal);
        Assert.Contains("Action: Run shell_execute", screen, StringComparison.Ordinal);
        Assert.Contains("Complex command", screen, StringComparison.Ordinal);
        Assert.Contains("third-party context", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ApprovalDetail_PreservesSelectionAndScrollPositionAcrossCollapse()
    {
        var detail = string.Join('\n', Enumerable.Range(0, 40).Select(index => $"command line {index}"));
        var approval = BuildApproval() with { DisplayText = detail };
        await using var harness = CreateHarness(approval: approval, height: 24);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Approval required"));

        harness.Input.EnqueueKey(ConsoleKey.DownArrow);
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Page.ApprovalDetailCanScrollDown);
        harness.Input.EnqueueKey(ConsoleKey.PageDown);
        await harness.WaitUntilAsync(() => harness.Page.ApprovalDetailScrollOffset > 0);
        var offset = harness.Page.ApprovalDetailScrollOffset;

        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => !harness.ViewModel.IsApprovalDetailVisible.Value);
        Assert.Equal(offset, harness.Page.ApprovalDetailScrollOffset);
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.ViewModel.IsApprovalDetailVisible.Value);
        Assert.Equal(offset, harness.Page.ApprovalDetailScrollOffset);

        harness.Input.EnqueueKey(ConsoleKey.Enter);
        Assert.Equal(ApprovalOptionKeys.ApproveSession,
            await harness.ViewModel.ReadApprovalAsync(harness.Cancellation.Token));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Generation_ShowsEveryQueuedPromptInOrder()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("MESSAGE"));
        Assert.NotNull(harness.Focus.CurrentFocus);

        harness.ViewModel.IsGenerating.Value = true;
        harness.ViewModel.StatusMessage.Value = "Generating...";
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("Thinking")
            && harness.Terminal.Contains("MESSAGE")
            && harness.Focus.CurrentFocus is not null);

        string[] prompts = ["queue this first", "queue this second", "queue this third"];
        foreach (var prompt in prompts)
        {
            harness.Input.EnqueueString(prompt);
            harness.Input.EnqueueKey(ConsoleKey.Enter);
        }

        foreach (var prompt in prompts)
        {
            Assert.Equal(prompt,
                await harness.ViewModel.ReadSubmissionAsync(harness.Cancellation.Token));
        }

        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("QUEUED  3 messages")
            && harness.Terminal.Contains("1  sending  queue this first")
            && harness.Terminal.Contains("2  sending  queue this second")
            && harness.Terminal.Contains("3  sending  queue this third"));

        for (var index = 0; index < prompts.Length; index++)
        {
            harness.ViewModel.Emit(new UserMessageQueuedOutput
            {
                SessionId = SessionId,
                TimestampMs = index + 1,
                MessageId = harness.ViewModel.MessageIdFor(prompts[index]),
                TurnId = new Netclaw.Actors.Protocol.TurnId("turn-1"),
                QueueDepth = index + 1
            });
        }
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("1  queued   queue this first")
            && harness.Terminal.Contains("2  queued   queue this second")
            && harness.Terminal.Contains("3  queued   queue this third"));

        harness.ViewModel.Emit(new TextDeltaOutput("I will inspect the current state.")
        {
            SessionId = SessionId,
            TimestampMs = 5
        });
        harness.ViewModel.Emit(new UserMessagesPulledOutput
        {
            SessionId = SessionId,
            TimestampMs = 6,
            BatchId = "batch-1",
            TurnId = new Netclaw.Actors.Protocol.TurnId("turn-1"),
            Messages =
            [
                new PulledUserMessage(harness.ViewModel.MessageIdFor(prompts[0]), prompts[0]),
                new PulledUserMessage(harness.ViewModel.MessageIdFor(prompts[1]), prompts[1])
            ]
        });
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("QUEUED  1 message")
            && harness.Terminal.Contains("1  queued   queue this third")
            && harness.Terminal.Contains("Pulled by agent  · 2 messages")
            && harness.Terminal.Contains("NETCLAW  LIVE"));

        var screen = harness.Terminal.ToString();
        Assert.True(
            screen.IndexOf("queue this first", StringComparison.Ordinal)
            < screen.IndexOf("queue this second", StringComparison.Ordinal));
        Assert.True(
            screen.IndexOf("queue this second", StringComparison.Ordinal)
            < screen.IndexOf("queue this third", StringComparison.Ordinal));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task AssistantText_UpdatesAsEachStreamDeltaArrives()
    {
        await using var harness = CreateHarness();
        var runTask = harness.StartAsync();

        harness.ViewModel.Emit(new TextDeltaOutput("The first")
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("The first"));

        harness.ViewModel.Emit(new TextDeltaOutput(" streamed reply")
        {
            SessionId = SessionId,
            TimestampMs = 2
        });
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("The first streamed reply")
            && harness.Terminal.Contains("MESSAGE"));

        var screen = harness.Terminal.ToString();
        Assert.Contains("NETCLAW  LIVE", screen, StringComparison.Ordinal);
        Assert.Contains("MESSAGE", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task LongAssistantStream_KeepsTheComposerVisible()
    {
        await using var harness = CreateHarness(height: 20);
        var runTask = harness.StartAsync();

        harness.ViewModel.Emit(new TextDeltaOutput(string.Join(
            '\n',
            Enumerable.Range(1, 30).Select(index => $"stream line {index}")))
        {
            SessionId = SessionId,
            TimestampMs = 1
        });

        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("stream line 30")
            && harness.Terminal.Contains("MESSAGE"));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task LargeStreamUpdate_StaysAtTheTailWithoutAnUnseenBadge()
    {
        await using var harness = CreateHarness(height: 20);
        var runTask = harness.StartAsync();

        harness.ViewModel.Emit(new TextDeltaOutput(string.Join(
            '\n',
            Enumerable.Range(1, 12).Select(index => $"stream line {index}")))
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("stream line 12"));

        harness.ViewModel.Emit(new TextDeltaOutput("\n" + string.Join(
            '\n',
            Enumerable.Range(13, 30).Select(index => $"stream line {index}")))
        {
            SessionId = SessionId,
            TimestampMs = 2
        });
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("stream line 42"));

        Assert.False(harness.Page.AssistantCanScrollDown);
        Assert.Equal(0, harness.Page.UnseenAssistantEventCount);
        Assert.DoesNotContain("new events", harness.Terminal.ToString(), StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task MouseWheelUp_PausesTailFollowUntilEnd()
    {
        await using var harness = CreateHarness(height: 20);
        var runTask = harness.StartAsync();

        harness.ViewModel.Emit(new TextDeltaOutput(string.Join(
            '\n',
            Enumerable.Range(1, 30).Select(index => $"stream line {index}")))
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("stream line 30")
            && harness.Page.AssistantScrollOffset > 0);

        harness.Events.Enqueue(new MouseScrollEvent(+1) { X = 3, Y = 1 });
        await harness.WaitUntilAsync(() => harness.Page.AssistantCanScrollDown);
        var pausedOffset = harness.Page.AssistantScrollOffset;

        harness.ViewModel.Emit(new TextDeltaOutput("\nstream line 31")
        {
            SessionId = SessionId,
            TimestampMs = 2
        });
        harness.ViewModel.Emit(new TextDeltaOutput("\nstream line 32")
        {
            SessionId = SessionId,
            TimestampMs = 3
        });
        await harness.WaitUntilAsync(() =>
            harness.Page.UnseenAssistantEventCount == 1
            && harness.Terminal.Contains("1 new event"));

        Assert.Equal(pausedOffset, harness.Page.AssistantScrollOffset);
        harness.Input.EnqueueKey(ConsoleKey.End);
        await harness.WaitUntilAsync(() =>
            !harness.Page.AssistantCanScrollDown
            && harness.Page.UnseenAssistantEventCount == 0
            && harness.Terminal.Contains("stream line 32"));

        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task PageUp_GroupsUpdatesByWorkItem()
    {
        await using var harness = CreateHarness(height: 20);
        var runTask = harness.StartAsync();

        harness.ViewModel.Emit(new TextDeltaOutput(string.Join(
            '\n',
            Enumerable.Range(1, 30).Select(index => $"stream line {index}")))
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        await harness.WaitUntilAsync(() => harness.Page.AssistantScrollOffset > 0);

        harness.Input.EnqueueKey(ConsoleKey.PageUp);
        await harness.WaitUntilAsync(() => harness.Page.AssistantCanScrollDown);
        harness.ViewModel.Emit(ToolCall("call-tail", "shell_execute", 2));
        harness.ViewModel.Emit(new ToolActivityOutput
        {
            SessionId = SessionId,
            TimestampMs = 3,
            CallId = new ToolCallId("call-tail"),
            ToolName = new ToolName("shell_execute"),
            TurnId = new Netclaw.Actors.Protocol.TurnId("turn-1"),
            Phase = "running",
            Summary = "Inspect the repository."
        });

        await harness.WaitUntilAsync(() =>
            harness.Page.UnseenAssistantEventCount == 1
            && harness.Terminal.Contains("1 new event"));

        for (var index = 0; index < 5; index++)
            harness.Input.EnqueueKey(ConsoleKey.PageDown);
        await harness.WaitUntilAsync(() =>
            !harness.Page.AssistantCanScrollDown
            && harness.Page.UnseenAssistantEventCount == 0);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task StableTranscript_UsesPrimaryScrollbackWithoutAnOuterBorder()
    {
        var output = new TextOutput("A stable answer")
        {
            SessionId = SessionId,
            TimestampMs = 0
        };
        await using var harness = CreateHarness(outputs: [output]);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("A stable answer"));

        Assert.Equal("  Netclaw", harness.Terminal.GetLine(0));
        Assert.Equal("  A stable answer", harness.Terminal.GetLine(1));
        Assert.Equal(string.Empty, harness.Terminal.GetLine(2));
        AssertHasNoDecorativeTrim(harness.Terminal.ToString());
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Wide_transcript_caps_the_assistant_line_measure()
    {
        var output = new TextOutput(string.Join(' ', Enumerable.Repeat("readable", 40)))
        {
            SessionId = SessionId,
            TimestampMs = 0
        };
        await using var harness = CreateHarness(outputs: [output], width: 160);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("readable"));

        Assert.InRange(harness.Terminal.GetLine(1).TrimEnd().Length, 1, 120);
        Assert.NotEmpty(harness.Terminal.GetLine(2).TrimEnd());
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ActivityDeck_ShowsParallelToolsThoughtAndSubAgent()
    {
        var outputs = new SessionOutput[]
        {
            new SessionJoined
            {
                SessionId = SessionId,
                TimestampMs = 0,
                TurnCount = 0
            },
            new ThinkingDeltaOutput("Compare both results")
            {
                SessionId = SessionId,
                TimestampMs = 1
            },
            ToolCall("call-a", "search", 2),
            ToolCall("call-b", "fetch", 3),
            new ToolActivityOutput
            {
                SessionId = SessionId,
                TimestampMs = 4,
                CallId = new ToolCallId("call-b"),
                ToolName = new ToolName("fetch"),
                TurnId = new TurnId("turn-1"),
                Phase = "running",
                Summary = "documentation"
            },
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 5,
                AgentName = new AgentName("reviewer"),
                Phase = SubAgentPhase.Activity,
                RunId = new SubAgentRunId("run-a"),
                ParentCallId = new ToolCallId("call-a"),
                ActivityPhase = "reviewing",
                ActivitySummary = "API surface"
            }
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("Compare both results")
            && harness.Terminal.Contains("search")
            && harness.Terminal.Contains("fetch")
            && harness.Terminal.Contains("reviewer"));

        var screen = harness.Terminal.ToString();
        Assert.Contains("Inspect call-a", screen, StringComparison.Ordinal);
        Assert.Contains("Inspect call-b", screen, StringComparison.Ordinal);
        Assert.Contains("· search", screen, StringComparison.Ordinal);
        Assert.Contains("· fetch", screen, StringComparison.Ordinal);
        Assert.Contains("Agent  reviewer", screen, StringComparison.Ordinal);
        Assert.Contains("MESSAGE", screen, StringComparison.Ordinal);
        Assert.True(screen.IndexOf("Inspect call-a", StringComparison.Ordinal)
                    < screen.IndexOf("connected", StringComparison.Ordinal));
        Assert.True(screen.IndexOf("connected", StringComparison.Ordinal)
                    < screen.IndexOf("MESSAGE", StringComparison.Ordinal));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ActivityDeck_NestsTheActiveToolUnderItsSubagent()
    {
        var outputs = new SessionOutput[]
        {
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 1,
                AgentName = new AgentName("interface-reviewer"),
                Phase = SubAgentPhase.Started,
                RunId = new SubAgentRunId("run-a"),
                ParentCallId = new ToolCallId("parent-a")
            },
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 2,
                AgentName = new AgentName("interface-reviewer"),
                Phase = SubAgentPhase.Activity,
                RunId = new SubAgentRunId("run-a"),
                ParentCallId = new ToolCallId("parent-a"),
                ActivityPhase = "running tools: shell_execute"
            },
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 3,
                AgentName = new AgentName("interface-reviewer"),
                Phase = SubAgentPhase.Activity,
                RunId = new SubAgentRunId("run-a"),
                ParentCallId = new ToolCallId("parent-a"),
                ActivityPhase = "awaiting human approval"
            }
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Tool  shell_execute"));

        var screen = harness.Terminal.ToString();
        Assert.True(screen.IndexOf("Agent  interface-reviewer", StringComparison.Ordinal)
                    < screen.IndexOf("Tool  shell_execute", StringComparison.Ordinal));
        Assert.Contains("awaiting human approval", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task SubagentApproval_ShowsRequesterPathAndCapsTheGateWidth()
    {
        var output = new SubAgentOutput
        {
            SessionId = SessionId,
            TimestampMs = 1,
            AgentName = new AgentName("interface-reviewer"),
            Phase = SubAgentPhase.Started,
            RunId = new SubAgentRunId("run-a"),
            ParentCallId = new ToolCallId("parent-a")
        };
        var approval = BuildApproval() with
        {
            CallId = new ToolCallId("parent-a/subagent-approval/approval-a")
        };
        await using var harness = CreateHarness(outputs: [output], approval: approval, width: 160);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("interface-reviewer requests permission to run shell_execute"));

        var longestLine = Enumerable.Range(0, harness.Terminal.Height)
            .Select(index => harness.Terminal.GetLine(index).TrimEnd().Length)
            .Max();
        Assert.InRange(longestLine, 1, 120);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task ParallelSubagentApprovals_ShowDecisionAndWaitingStates()
    {
        var outputs = new SessionOutput[]
        {
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 1,
                AgentName = new AgentName("reviewer-a"),
                Phase = SubAgentPhase.Started,
                RunId = new SubAgentRunId("run-a"),
                ParentCallId = new ToolCallId("parent-a")
            },
            new SubAgentOutput
            {
                SessionId = SessionId,
                TimestampMs = 2,
                AgentName = new AgentName("reviewer-b"),
                Phase = SubAgentPhase.Started,
                RunId = new SubAgentRunId("run-b"),
                ParentCallId = new ToolCallId("parent-b")
            },
            BuildApproval() with
            {
                CallId = new ToolCallId("parent-a/subagent-approval/approval-a")
            },
            BuildApproval() with
            {
                CallId = new ToolCallId("parent-b/subagent-approval/approval-b")
            }
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() =>
            harness.Terminal.Contains("Approval required  1 of 2")
            && harness.Terminal.Contains("reviewer-a")
            && harness.Terminal.Contains("reviewer-b"));

        var screen = harness.Terminal.ToString();
        Assert.Contains("Decision Agent  reviewer-a", screen, StringComparison.Ordinal);
        Assert.Contains("Waiting  Agent  reviewer-b", screen, StringComparison.Ordinal);
        Assert.Contains("reviewer-a requests permission", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-b requests permission", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task AssistantMarkdown_UsesPlainDisplayAndKeepsSemanticCopy()
    {
        const string markdown = "# Result\n\n**Passed** with `dotnet test`.";
        var output = new TextOutput(markdown)
        {
            SessionId = SessionId,
            TimestampMs = 1
        };
        await using var harness = CreateHarness(outputs: [output]);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Passed with dotnet test."));

        var screen = harness.Terminal.ToString();
        Assert.DoesNotContain("# Result", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("**Passed**", screen, StringComparison.Ordinal);
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));
        Assert.DoesNotContain("# Result", harness.Terminal.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("**Passed**", harness.Terminal.ToString(), StringComparison.Ordinal);
        harness.Input.EnqueueKey(ConsoleKey.Y);
        await harness.WaitUntilAsync(() => harness.Clipboard.LastCopiedText is not null);
        Assert.Contains(markdown, harness.Clipboard.LastCopiedText!, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_ShowsTheCompleteSemanticToolResult()
    {
        var outputs = new SessionOutput[]
        {
            ToolCall("call-a", "search", 1),
            new ToolResultOutput
            {
                SessionId = SessionId,
                TimestampMs = 2,
                CallId = new ToolCallId("call-a"),
                ToolName = new ToolName("search"),
                Result = "compact line\ncomplete hidden line"
            }
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Completed work"));

        Assert.DoesNotContain("compact line", harness.Terminal.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("complete hidden line", harness.Terminal.ToString(), StringComparison.Ordinal);
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));
        harness.Input.EnqueueKey(ConsoleKey.End);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("complete hidden line"));

        Assert.Contains("Reply", harness.Terminal.ToString(), StringComparison.Ordinal);
        Assert.Contains("complete hidden line", harness.Terminal.ToString(), StringComparison.Ordinal);
        Assert.Contains("Arguments: {\"call\":\"call-a\"}",
            harness.Terminal.ToString(), StringComparison.Ordinal);
        AssertHasNoDecorativeTrim(harness.Terminal.ToString());
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_KeepsItsHeaderAndFooterInTheSameFrame()
    {
        var output = new TextOutput("viewport proof")
        {
            SessionId = SessionId,
            TimestampMs = 1
        };
        await using var harness = CreateHarness(outputs: [output], width: 120, height: 24);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("viewport proof"));

        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        var screen = string.Empty;
        await harness.WaitUntilAsync(() =>
        {
            screen = harness.Terminal.ToString();
            return screen.Contains("INSPECTOR", StringComparison.Ordinal)
                   && screen.Contains("Up/Down event", StringComparison.Ordinal);
        });

        Assert.Contains("  INSPECTOR", screen, StringComparison.Ordinal);
        Assert.Contains("TURN EVENTS", screen, StringComparison.Ordinal);
        Assert.Contains("Up/Down event", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_WrapsAssistantProseAtWordBoundaries()
    {
        var output = new TextOutput(
            "Accessibility reviewers verify every expandable control before release.")
        {
            SessionId = SessionId,
            TimestampMs = 1
        };
        await using var harness = CreateHarness(outputs: [output], width: 40);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("expandable"));

        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));

        var screen = harness.Terminal.ToString();
        Assert.Contains("expandable", screen, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Accessibility reviewers verify every expandable control before release.",
            screen,
            StringComparison.Ordinal);
        Assert.DoesNotContain("expanda\nble", screen, StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_DefersNewStableBlocksUntilItCloses()
    {
        var first = new TextOutput("first answer")
        {
            SessionId = SessionId,
            TimestampMs = 1
        };
        await using var harness = CreateHarness(outputs: [first]);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("first answer"));
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));

        harness.ViewModel.Emit(new TextOutput("queued answer")
        {
            SessionId = SessionId,
            TimestampMs = 2
        });
        harness.ViewModel.Emit(new TurnCompleted
        {
            SessionId = SessionId,
            TimestampMs = 3,
            TurnNumber = new TurnNumber(2),
            Outcome = TurnOutcome.Completed
        });
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("event 1 of 2"));
        Assert.DoesNotContain("queued answer", harness.Terminal.ToString(), StringComparison.Ordinal);

        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("queued answer"));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_EventCopyUsesCompleteSemanticText()
    {
        var outputs = new SessionOutput[]
        {
            ToolCall("call-a", "search", 1),
            new ToolResultOutput
            {
                SessionId = SessionId,
                TimestampMs = 2,
                CallId = new ToolCallId("call-a"),
                ToolName = new ToolName("search"),
                Result = "first line\n\x1b[31mcomplete result\x1b[0m"
            }
        };
        await using var harness = CreateHarness(outputs: outputs);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Completed work"));
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));

        harness.Input.EnqueueKey(ConsoleKey.Y);
        await harness.WaitUntilAsync(() => harness.Clipboard.LastCopiedText is not null);

        var copied = harness.Clipboard.LastCopiedText!;
        Assert.Contains("complete result", copied, StringComparison.Ordinal);
        Assert.Contains("Arguments: {\"call\":\"call-a\"}", copied, StringComparison.Ordinal);
        Assert.DoesNotContain('\x1b', copied);
        Assert.DoesNotContain('│', copied);
        Assert.DoesNotContain('╭', copied);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_ShiftYCopiesTheCompleteTurnInOrder()
    {
        var joined = new SessionJoined
        {
            SessionId = SessionId,
            TimestampMs = 1,
            TurnCount = 1,
            RecentTranscript =
            [
                new SessionTranscriptEntry
                {
                    Type = SessionTranscriptEntryTypes.User,
                    Text = "check status",
                    TurnId = "turn-1"
                },
                new SessionTranscriptEntry
                {
                    Type = SessionTranscriptEntryTypes.Tool,
                    ToolName = "status",
                    CallId = "call-a",
                    Result = "healthy",
                    TurnId = "turn-1"
                },
                new SessionTranscriptEntry
                {
                    Type = SessionTranscriptEntryTypes.Assistant,
                    Text = "all healthy",
                    TurnId = "turn-1"
                }
            ]
        };
        await using var harness = CreateHarness(outputs: [joined]);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("all healthy"));
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));

        harness.Input.EnqueueKey(ConsoleKey.Y, shift: true);
        await harness.WaitUntilAsync(() => harness.Clipboard.LastCopiedText is not null);

        var copied = harness.Clipboard.LastCopiedText!;
        Assert.True(copied.IndexOf("check status", StringComparison.Ordinal)
                    < copied.IndexOf("healthy", StringComparison.Ordinal));
        Assert.True(copied.IndexOf("healthy", StringComparison.Ordinal)
                    < copied.IndexOf("all healthy", StringComparison.Ordinal));
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task Inspector_CopyFailureStaysVisibleAndKeepsTheEvent()
    {
        var output = new TextOutput("copy target")
        {
            SessionId = SessionId,
            TimestampMs = 1
        };
        await using var harness = CreateHarness(outputs: [output], clipboardSucceeds: false);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("copy target"));
        harness.Input.EnqueueKey(ConsoleKey.O, control: true);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("INSPECTOR"));

        harness.Input.EnqueueKey(ConsoleKey.Y);
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("Copy failed"));

        harness.Input.EnqueueKey(ConsoleKey.Y);
        await harness.WaitUntilAsync(() => harness.Clipboard.CopyCount == 2);
        Assert.Equal("NETCLAW\ncopy target", harness.Clipboard.LastCopiedText);
        await harness.StopAsync(runTask);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public async Task CommonWidths_KeepTheComposerAndStatusVisible(int width)
    {
        await using var harness = CreateHarness(width: width);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("MESSAGE"));

        var screen = harness.Terminal.ToString();
        Assert.Contains("NETCLAW", screen, StringComparison.Ordinal);
        Assert.Contains("MESSAGE", screen, StringComparison.Ordinal);
        Assert.Contains("Enter", screen, StringComparison.Ordinal);
        var header = Enumerable.Range(0, harness.Terminal.Height)
            .Select(harness.Terminal.GetLine)
            .First(line => line.Contains("NETCLAW", StringComparison.Ordinal));
        if (width >= 60)
            Assert.StartsWith("  NETCLAW", header, StringComparison.Ordinal);
        AssertHasNoDecorativeTrim(screen);
        await harness.StopAsync(runTask);
    }

    [Fact]
    public async Task NarrowHeader_KeepsACompactConnectionCue()
    {
        var joined = new SessionJoined
        {
            SessionId = SessionId,
            TimestampMs = 1,
            TurnCount = 0
        };
        await using var harness = CreateHarness(outputs: [joined], width: 40);
        var runTask = harness.StartAsync();
        await harness.WaitUntilAsync(() => harness.Terminal.Contains("connected"));

        Assert.Contains("NETCLAW", harness.Terminal.ToString(), StringComparison.Ordinal);
        Assert.Contains("connected", harness.Terminal.ToString(), StringComparison.Ordinal);
        await harness.StopAsync(runTask);
    }

    private static ToolCallOutput ToolCall(string callId, string name, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName(name),
        Rationale = $"Inspect {callId}",
        ArgumentsJson = $"{{\"call\":\"{callId}\"}}"
    };

    private static void AssertHasNoDecorativeTrim(string screen)
    {
        const string decorativeTrim = "╭╮╰╯┌┐└┘│─╷╵█░▒▓✓◌↳";
        foreach (var character in decorativeTrim)
            Assert.DoesNotContain(character, screen);
    }

    private static ToolInteractionRequest BuildApproval() => new()
    {
        SessionId = SessionId,
        TimestampMs = 1,
        Kind = "approval",
        CallId = new ToolCallId("approval-call"),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "dotnet test src/Netclaw.Cli.Tests",
        Options =
        [
            new ToolInteractionOption(
                ApprovalOptionKeys.ApproveOnceKey,
                ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(
                ApprovalOptionKeys.ApproveSessionKey,
                ApprovalOptionKeys.ApproveSessionLabel),
            new ToolInteractionOption(
                ApprovalOptionKeys.DenyKey,
                ApprovalOptionKeys.DenyLabel)
        ]
    };

    private static InlineHarness CreateHarness(
        IReadOnlyList<SessionOutput>? outputs = null,
        ToolInteractionRequest? approval = null,
        bool clipboardSucceeds = true,
        int width = 120,
        int height = 40)
    {
        var terminal = new VirtualTerminal(width, height);
        var input = new VirtualInputSource();
        var events = new TestEventInputSource();
        var clipboard = new TestClipboardService(clipboardSucceeds);
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        TestChatViewModel? viewModel = null;
        InlineChatPage? page = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IClipboardService>(clipboard);
        services.AddTerminaVirtualInput(input);
        services.AddSingleton<IInputSource>(events);
        services.AddTermina("/chat", builder =>
        {
            builder.ConfigureRuntime(options =>
            {
                options.PresentationMode = TerminalPresentationMode.Inline;
                options.ScrollInputMode = ScrollInputMode.NativeTerminal;
            });
            builder.RegisterRoute<InlineChatPage, ChatViewModel>(
                "/chat",
                serviceProvider => page = new InlineChatPage(
                    serviceProvider.GetRequiredService<IAnsiTerminal>(),
                    serviceProvider.GetRequiredService<IInlineOutput>(),
                    serviceProvider.GetRequiredService<IClipboardService>(),
                    serviceProvider.GetRequiredService<TimeProvider>()),
                _ => viewModel = new TestChatViewModel(outputs ?? [], approval));
        });

        var provider = services.BuildServiceProvider();
        var app = provider.GetRequiredService<TerminaApplication>();
        return new InlineHarness(
            provider,
            terminal,
            input,
            events,
            clipboard,
            app.Focus,
            page!,
            app,
            viewModel!);
    }

    private sealed class InlineHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public InlineHarness(
            ServiceProvider provider,
            VirtualTerminal terminal,
            VirtualInputSource input,
            TestEventInputSource events,
            TestClipboardService clipboard,
            IFocusManager focus,
            InlineChatPage page,
            TerminaApplication app,
            TestChatViewModel viewModel)
        {
            _provider = provider;
            Terminal = terminal;
            Input = input;
            Events = events;
            Clipboard = clipboard;
            Focus = focus;
            Page = page;
            App = app;
            ViewModel = viewModel;
        }

        public VirtualTerminal Terminal { get; }

        public VirtualInputSource Input { get; }

        public TestEventInputSource Events { get; }

        public TestClipboardService Clipboard { get; }

        public IFocusManager Focus { get; }

        public InlineChatPage Page { get; }

        public TerminaApplication App { get; }

        public TestChatViewModel ViewModel { get; }

        public CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromSeconds(10));

        private Task? RunTask { get; set; }

        public Task StartAsync()
        {
            RunTask = App.RunAsync(Cancellation.Token);
            return RunTask;
        }

        public async Task StopAsync(Task runTask)
        {
            Input.EnqueueKey(ConsoleKey.Q, control: true);
            await runTask;
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                if (RunTask is { IsCompleted: true })
                    await RunTask;
                Cancellation.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class TestChatViewModel : ChatViewModel
    {
        private readonly IReadOnlyList<SessionOutput> _outputs;
        private readonly ToolInteractionRequest? _approval;
        private readonly Channel<string> _submissions = Channel.CreateUnbounded<string>();
        private readonly Dictionary<string, string> _messageIds = new(StringComparer.Ordinal);
        private readonly Channel<string> _approvalSelections = Channel.CreateUnbounded<string>();

        public TestChatViewModel(
            IReadOnlyList<SessionOutput> outputs,
            ToolInteractionRequest? approval)
            : base(
                new DaemonClient("http://127.0.0.1:1"),
                TimeProvider.System,
                new ModelCapabilities { ModelId = "test-model" },
                new ChatNavigationState(),
                new NetclawPaths())
        {
            _outputs = outputs;
            _approval = approval;
        }

        protected override Task InitializeSessionAsync() => Task.CompletedTask;

        public override void OnActivated()
        {
            base.OnActivated();
            foreach (var output in _outputs)
                PublishOutputForTesting(output);
            if (_outputs.Count > 0
                && !_outputs.Any(output => output is TurnCompleted)
                && _outputs.Any(output => output is TextOutput or ToolResultOutput))
            {
                PublishOutputForTesting(new TurnCompleted
                {
                    SessionId = SessionId,
                    TimestampMs = _outputs.Max(output => output.TimestampMs) + 1,
                    TurnNumber = new TurnNumber(1),
                    Outcome = TurnOutcome.Completed
                });
            }
            if (_approval is not null)
                SeedPendingInteractionForTesting(_approval);
        }

        public override Task SubmitAsync(string text)
        {
            _submissions.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public override Task SubmitAsync(string text, string messageId)
        {
            _messageIds[text] = messageId;
            _submissions.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public string MessageIdFor(string text) => _messageIds[text];

        protected override Task SubmitInteractionSelectionAsync(string selectedKey)
        {
            _approvalSelections.Writer.TryWrite(selectedKey);
            PublishOutputForTesting(new TurnCompleted
            {
                SessionId = SessionId,
                TimestampMs = 10,
                TurnNumber = new TurnNumber(1),
                Outcome = TurnOutcome.Completed
            });
            return Task.CompletedTask;
        }

        public ValueTask<string> ReadSubmissionAsync(CancellationToken cancellationToken) =>
            _submissions.Reader.ReadAsync(cancellationToken);

        public ValueTask<string> ReadApprovalAsync(CancellationToken cancellationToken) =>
            _approvalSelections.Reader.ReadAsync(cancellationToken);

        public void Emit(SessionOutput output)
        {
            PublishOutputForTesting(output);
        }
    }

    private sealed class TestEventInputSource : IInputSource
    {
        private readonly Channel<object> _events = Channel.CreateUnbounded<object>();

        public void Enqueue(IInputEvent input)
        {
            _events.Writer.TryWrite(input);
        }

        public async Task RunAsync(
            ChannelWriter<object> writer,
            CancellationToken cancellationToken)
        {
            await foreach (var input in _events.Reader.ReadAllAsync(cancellationToken))
                await writer.WriteAsync(input, cancellationToken);
        }
    }

    private sealed class TestClipboardService(bool succeeds) : IClipboardService
    {
        public string? LastCopiedText { get; private set; }

        public int CopyCount { get; private set; }

        public bool Copy(string text)
        {
            CopyCount++;
            LastCopiedText = text;
            return succeeds;
        }
    }
}
