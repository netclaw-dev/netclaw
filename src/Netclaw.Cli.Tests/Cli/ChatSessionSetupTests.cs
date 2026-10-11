// -----------------------------------------------------------------------
// <copyright file="ChatSessionSetupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Xunit;
using static Netclaw.Cli.Daemon.ChatClientProtocol;
using Netclaw.Tools;

namespace Netclaw.Cli.Tests.Cli;

public sealed class ChatSessionSetupTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly Channel<ClientEvent> _events = Channel.CreateUnbounded<ClientEvent>();
    private IActorRef Owner => _owner ??= Sys.ActorOf(Props.Create(() => new ChatClientActor(
        _transport, "http://localhost", new[] { TimeSpan.Zero }, TimeSpan.FromSeconds(30),
        TimeProvider.System, _events.Writer, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))));
    private IActorRef? _owner;
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private TestScheduler Scheduler => (TestScheduler)Sys.Scheduler;
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.AddHocon("akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"", HoconAddMode.Prepend);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task Post(Request request)
    {
        Owner.Tell(request);
        return request.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }
    private async Task<SessionId> Post(SessionRequest request)
    {
        await Post((Request)request);
        return await request.Reply.Task;
    }
    private Request Selection(string selection, string sessionId = "fake/resumed") => selection switch
    {
        "Create" => new Create(ChannelType.Tui, Token),
        "Keep" => new Keep(ChannelType.Tui, Token),
        "Resume" => new Resume(new SessionId(sessionId), ChannelType.Tui, Token),
        "Open" => new Open(null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(selection))
    };
    private async Task<DaemonConnectionEvent> Event(DaemonConnectionState state)
    {
        while (await _events.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token) is { } value)
            if (value is ConnectionChanged { Value: var connection } && connection.State == state) return connection;
        throw new InvalidOperationException("The event stream ended.");
    }
    private Task<ChatCloseReceipt> Close()
    {
        var reply = new TaskCompletionSource<ChatCloseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner.Tell(new Close(reply));
        return reply.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Queued_text_and_the_hidden_trigger_arrive_once_in_order(bool hiddenTrigger, bool dropDuringBind)
    {
        var entered = Gate();
        var release = Gate();
        _transport.EnsureSessionGate = _ => { entered.TrySetResult(); return release.Task; };
        var open = Post(new Open(null, hiddenTrigger ? "hidden" : null));
        if (!hiddenTrigger) await open;
        var first = Post(new SendText("first", Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var second = Post(new SendText("second", Token));
        if (dropDuringBind)
        {
            _transport.RaiseClosed();
            Owner.Tell(new TransportDropped(null));
            await Event(DaemonConnectionState.TransportClosed);
        }
        release.TrySetResult();
        await Task.WhenAll(open, first, second);
        var receipt = await Close();
        Assert.Empty(receipt.Inputs);
        Assert.Equal(hiddenTrigger ? ["first", "second", "hidden"] : new[] { "first", "second" },
            _transport.Invocations.Where(call => call.Method == "SendMessage").Select(call => (string)call.Args[1]!).ToArray());
        Assert.Equal(dropDuringBind ? 2 : 1, _transport.EnsureSessionCalls);
        if (dropDuringBind) Assert.Equal("fake/session", _transport.Invocations.Last(call => call.Method == "EnsureSession").Args[0]);
    }

    [Theory]
    [InlineData(ChannelType.Tui)]
    [InlineData(ChannelType.Headless)]
    public async Task Reconnect_retains_the_selected_channel_and_session(ChannelType channel)
    {
        var id = await Post(new Create(channel, Token));
        await Post(new SendText("first", Token));
        _transport.RaiseClosed();
        Owner.Tell(new TransportDropped(null));
        await Event(DaemonConnectionState.TransportClosed);
        await Event(DaemonConnectionState.Connected);
        await Post(new SendText("second", Token));
        var binding = _transport.Invocations.Last(call => call.Method == "EnsureSession");
        Assert.Equal(id.Value, binding.Args[0]);
        Assert.Equal(channel.ToWireValue(), binding.Args[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_drop_during_binding_retains_recovery_after_the_caller_leaves(bool deadline)
    {
        var session = await Post(new Create(ChannelType.Tui, Token));
        var binding = Gate();
        var bound = Gate();
        var connecting = Gate();
        var connected = Gate();
        _transport.EnsureSessionGate = _ => { binding.TrySetResult(); return bound.Task; };
        _transport.StartHook = _ => { connecting.TrySetResult(); return connected.Task; };
        using var cancelled = new CancellationTokenSource();
        var selection = Post(new Keep(ChannelType.Tui, cancelled.Token));
        try
        {
            await binding.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _transport.RaiseClosed();
            Owner.Tell(new TransportDropped(null));
            await Event(DaemonConnectionState.TransportClosed);
            bound.SetResult();
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (deadline) Scheduler.Advance(TimeSpan.FromSeconds(30));
            else cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
            var afterRecovery = Post(new Connect(Token));
            connected.SetResult();
            await afterRecovery;
            Assert.Equal(3, _transport.EnsureSessionCalls);
            Assert.Equal(session.Value, _transport.Invocations.Last(call => call.Method == "EnsureSession").Args[0]);
            Assert.DoesNotContain(_transport.Invocations, call => call.Method == "SendMessage");
            Assert.Equal(1, _transport.PeakConcurrency);
            Assert.Empty((await Close()).Inputs);
        }
        finally { bound.TrySetResult(); connected.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_single_close_deadline_classifies_active_and_queued_work(bool interaction)
    {
        var entered = Gate();
        var release = Gate();
        _transport.VoidInvokeHook = (_, _, _) => { entered.TrySetResult(); return release.Task; };
        await Post(new Open(null, null));
        var active = Post(interaction ? new Respond(new ToolCallId("call-1"), new ApprovalOptionKey("deny"), Token) : (Request)new SendText("active", Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var queued = Post(new SendText("queued", Token));
        var close = Close();
        await Event(DaemonConnectionState.Closing);
        // Drop notifications do not extend the fixed deadline.
        _transport.RaiseClosed();
        Owner.Tell(new TransportDropped(null));
        Scheduler.Advance(TimeSpan.FromSeconds(2));
        var receipt = await close;
        Assert.Equal([InputDeliveryStatus.Unconfirmed, InputDeliveryStatus.Unsent], receipt.Inputs.Select(input => input.Status));
        await Assert.ThrowsAnyAsync<Exception>(() => active);
        await Assert.ThrowsAnyAsync<Exception>(() => queued);
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(new SendText("after-close", Token)));
        Assert.Single(_transport.Invocations, call => call.Method is "SendMessage" or "RespondToInteraction");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_dispatch_sends_nothing_and_after_dispatch_does_not_replay(bool dispatched)
    {
        var entered = Gate();
        var release = Gate();
        _transport.VoidInvokeHook = (_, _, _) => { entered.TrySetResult(); return release.Task; };
        await Post(new Open(null, null));
        using var cancelled = new CancellationTokenSource();
        var first = Post(new SendText("first", dispatched ? cancelled.Token : Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var second = Post(new SendText("second", dispatched ? Token : cancelled.Token));
        cancelled.Cancel();
        if (dispatched) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(_transport.Invocations, call => call.Method == "SendMessage");
        release.TrySetResult();
        if (dispatched) await second;
        else { await first; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); }
        var receipt = await Close();
        Assert.Equal(dispatched ? ["first", "second"] : new[] { "first" },
            _transport.Invocations.Where(call => call.Method == "SendMessage").Select(call => (string)call.Args[1]!).ToArray());
        Assert.Equal(dispatched ? 1 : 0, receipt.Inputs.Length);
        Assert.Equal(1, _transport.PeakConcurrency);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_rpc_deadline_keeps_one_operation_until_the_old_task_ends(bool lateFailure)
    {
        var entered = Gate();
        var release = Gate();
        _transport.VoidInvokeHook = (_, args, _) =>
        {
            if ((string)args[1]! != "first") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        await Post(new Open(null, null));
        var first = Post(new SendText("first", Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var second = Post(new SendText("second", Token));
        Scheduler.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(_transport.Invocations, call => call.Method == "SendMessage");
        if (lateFailure) release.SetException(new IOException("late failure"));
        else release.SetResult();
        await second;
        var receipt = await Close();
        Assert.Equal(InputDeliveryStatus.Unconfirmed, Assert.Single(receipt.Inputs).Status);
        Assert.Equal(2, _transport.Invocations.Count(call => call.Method == "SendMessage"));
        Assert.Equal(1, _transport.PeakConcurrency);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    public async Task Unsupported_admission_versions_dispatch_zero_text_Rpcs(int version, bool afterDrop)
    {
        await Post(new Open(null, null));
        if (afterDrop) await Post(new SendText("admitted", Token));
        _transport.EnsureSessionResponder = args => new SessionEnsureResultDto((string?)args[0] ?? "fake/session", false) { TextAdmissionVersion = version };
        if (afterDrop)
        {
            _transport.RaiseClosed();
            Owner.Tell(new TransportDropped(null));
            await Event(DaemonConnectionState.TransportClosed);
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => Post(new SendText("blocked", Token)));
        Assert.Equal(afterDrop ? 1 : 0, _transport.Invocations.Count(call => call.Method == "SendMessage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_send_is_attempted_once_and_later_text_can_proceed(bool confirmedRejection)
    {
        var error = confirmedRejection ? (Exception)new Microsoft.AspNetCore.SignalR.HubException(SessionEnsureResultDto.TextRejectionPrefix + "denied")
            : new IOException("lost response");
        _transport.VoidInvokeHook = (_, args, _) => (string)args[1]! == "first" ? Task.FromException(error) : Task.CompletedTask;
        await Post(new Open(null, null));
        await Assert.ThrowsAsync(error.GetType(), () => Post(new SendText("first", Token)));
        await Post(new SendText("second", Token));
        var receipt = await Close();
        Assert.Equal(confirmedRejection ? InputDeliveryStatus.Rejected : InputDeliveryStatus.Unconfirmed, Assert.Single(receipt.Inputs).Status);
        Assert.Equal(2, _transport.Invocations.Count(call => call.Method == "SendMessage"));
    }

    [Theory]
    [InlineData("Create", true)]
    [InlineData("Keep", true)]
    [InlineData("Resume", true)]
    [InlineData("Open", true)]
    [InlineData("Open", false)]
    public async Task Session_selection_preserves_explicit_contracts_and_a_fresh_chat_is_lazy(string selection, bool submit)
    {
        var created = 0;
        _transport.EnsureSessionResponder = args => new SessionEnsureResultDto(
            (string?)args[0] ?? $"fake/{++created}", args[0] is null)
        { TextAdmissionVersion = 1 };
        var previous = await Post(new Create(ChannelType.Tui, Token));
        var request = Selection(selection);
        await Post(request);
        if (request is Open) Assert.Equal(1, _transport.EnsureSessionCalls);
        if (submit) await Post(new SendText("selected-session", Token));
        var receipt = await Close();
        Assert.Empty(receipt.Inputs);
        if (!submit)
        {
            Assert.Null(receipt.SessionId);
            Assert.Equal(1, created);
            Assert.DoesNotContain(_transport.Invocations, call => call.Method == "SendMessage");
            return;
        }
        var expected = request switch
        {
            Keep => previous.Value,
            Resume => "fake/resumed",
            _ => "fake/2"
        };
        Assert.Equal(expected, receipt.SessionId);
        Assert.Equal(expected, Assert.Single(_transport.Invocations, call => call.Method == "SendMessage").Args[0]);
        if (request is SessionRequest selected) Assert.Equal(expected, (await selected.Reply.Task).Value);
    }

    [Fact]
    public async Task Attachment_retries_follow_one_two_five_and_ten_second_timers()
    {
        _transport.EnsureSessionResponder = _ => _transport.EnsureSessionCalls <= 4
            ? throw new IOException("attachment unavailable")
            : new SessionEnsureResultDto("fake/recovered", true) { TextAdmissionVersion = 1 };
        await Post(new Open(null, null));
        var send = Post(new SendText("held-text", Token));
        foreach (var seconds in new[] { 1, 2, 5, 10 })
        {
            var retry = await Event(DaemonConnectionState.Reconnecting);
            Assert.Contains($"in {seconds}s", retry.Message);
            Assert.DoesNotContain(_transport.Invocations, call => call.Method == "SendMessage");
            Scheduler.Advance(TimeSpan.FromSeconds(seconds));
        }
        await send;
        Assert.Equal(5, _transport.EnsureSessionCalls);
        Assert.Single(_transport.Invocations, call => call.Method == "SendMessage");
        Assert.Empty((await Close()).Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_failure_stops_the_affected_request_without_transient_retries(bool duringBind)
    {
        var error = new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        if (duringBind)
        {
            await Post(new Open(null, null));
            _transport.EnsureSessionResponder = _ => throw error;
        }
        else _transport.StartHook = _ => Task.FromException(error);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Post(new SendText("blocked", Token)));
        Assert.Contains("re-pair", failure.Message);
        Assert.Equal(1, _transport.StartAttempts);
        Assert.Equal(duringBind ? 1 : 0, _transport.EnsureSessionCalls);
        Assert.DoesNotContain(_transport.Invocations, call => call.Method == "SendMessage");
        Assert.Equal(InputDeliveryStatus.Unsent, Assert.Single((await Close()).Inputs).Status);
    }


    [Fact]
    public async Task A_close_receipt_retains_the_session_of_an_earlier_uncertain_send()
    {
        await Post(new Resume(new SessionId("fake/earlier"), ChannelType.Tui, Token));
        _transport.VoidInvokeHook = (_, _, _) => Task.FromException(new IOException("lost response"));
        await Assert.ThrowsAsync<IOException>(() => Post(new SendText("uncertain", Token)));
        await Post(new Resume(new SessionId("fake/later"), ChannelType.Tui, Token));
        var receipt = await Close();
        Assert.Equal("fake/later", receipt.SessionId);
        Assert.Equal("fake/earlier", Assert.Single(receipt.Inputs).SessionId);
        Assert.Contains("fake/earlier", receipt.Notice);
        Assert.DoesNotContain("fake/later", receipt.Notice);
    }


    [Theory]
    [InlineData("Create")]
    [InlineData("Keep")]
    [InlineData("Resume")]
    public async Task An_explicit_selection_after_chat_restores_the_finite_retry_budget(string selection)
    {
        await Post(new Open(null, null));
        _transport.EnsureSessionResponder = _ => throw new IOException("attachment failed");
        await Assert.ThrowsAsync<IOException>(() => Post(Selection(selection, "fake/selected")));
        Assert.Equal(1, _transport.EnsureSessionCalls);
        Assert.Empty((await Close()).Inputs);
    }

}
