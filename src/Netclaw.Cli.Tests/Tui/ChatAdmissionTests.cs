// -----------------------------------------------------------------------
// <copyright file="ChatAdmissionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ChatAdmissionTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();
    public void Dispose() => _directory.Dispose();
    private NetclawPaths Paths
    {
        get { var paths = new NetclawPaths(_directory.Path); paths.EnsureDirectoriesExist(); return paths; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_Enter_then_repeated_CtrlQ_waits_for_admission_without_a_model_response(bool heldAdmission)
    {
        var entered = Gate();
        var release = Gate();
        if (!heldAdmission) release.SetResult();
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (_, _, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); }
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        var navigation = new ChatNavigationState();
        var input = new VirtualInputSource();
        using var chat = new ChatViewModel(client, TimeProvider.System, new ModelCapabilities { ModelId = "test" }, navigation, Paths);
        await using var provider = CreateHost(chat, input, new VirtualTerminal(100, 30));
        input.EnqueueString("typed-before-quit");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);
        input.EnqueueString("rejected-after-quit");
        input.EnqueueKey(ConsoleKey.Enter);
        var run = provider.GetRequiredService<TerminaApplication>().RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (heldAdmission) Assert.False(run.IsCompleted);
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(Assert.IsType<ChatCloseReceipt>(navigation.CloseReceipt).Inputs);
            Assert.Same(client.CloseReceipt, navigation.CloseReceipt);
            var send = Assert.Single(transport.Invocations, call => call.Method == "SendMessage");
            Assert.Equal("typed-before-quit", send.Args[1]);
        }
        finally { release.TrySetResult(); }
    }

    private static ServiceProvider CreateHost(ChatViewModel chat, VirtualInputSource input, VirtualTerminal terminal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(input);
        services.AddTermina(ChatViewModel.Route, routes => routes.RegisterRoute<ChatPage, ChatViewModel>(
            ChatViewModel.Route, provider => new ChatPage(provider.GetRequiredService<IAnsiTerminal>()), _ => chat));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_bursts_apply_page_state_before_transcript_publication_and_preserve_order(bool approval)
    {
        SessionOutputDto Dto(string type) => new() { Type = type, SessionId = "fake/session" };
        var burst = new List<SessionOutputDto>
        {
            Dto(SessionOutputTypes.TextDelta) with { Text = "burst-one " },
            Dto(SessionOutputTypes.TextDelta) with { Text = "burst-two" },
            Dto(SessionOutputTypes.Text) with { Text = "burst-one burst-two" },
            Dto(SessionOutputTypes.Usage) with { InputTokens = 25, OutputTokens = 5, ContextWindowTokens = 100 }
        };
        if (approval) burst.Add(Dto(SessionOutputTypes.ToolInteraction) with
        {
            CallId = "call-1",
            ToolName = "shell_execute",
            InteractionDisplayText = "echo hello",
            InteractionOptions = [new ToolInteractionOption(new ApprovalOptionKey("deny"), "Deny")]
        });
        burst.Add(Dto(SessionOutputTypes.TurnCompleted) with { TurnNumber = new TurnNumber(1) });
        var transport = new FakeDaemonHubTransport();
        transport.VoidInvokeHook = (_, _, _) =>
        {
            foreach (var output in burst) transport.PushOutput(output);
            return Task.CompletedTask;
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        var input = new VirtualInputSource();
        var terminal = new VirtualTerminal(120, 40);
        using var chat = new ChatViewModel(client, TimeProvider.System, new ModelCapabilities { ModelId = "test" }, new ChatNavigationState(), Paths);
        var observed = new List<(Type Type, string? Text, string Status, bool Pending, bool Generating, string? Usage)>();
        using var subscription = chat.SessionOutput.Subscribe(output =>
        {
            var text = output switch { TextDeltaOutput delta => delta.Delta, TextOutput final => final.Text, _ => null };
            observed.Add((output.GetType(), text, chat.StatusMessage.Value, chat.HasPendingInteraction, chat.IsGenerating.Value, chat.UsageDisplay.Value));
            if (output is TurnCompleted) input.EnqueueKey(ConsoleKey.Q, false, false, true);
        });
        await using var provider = CreateHost(chat, input, terminal);
        input.EnqueueString("start-burst");
        input.EnqueueKey(ConsoleKey.Enter);
        await provider.GetRequiredService<TerminaApplication>().RunAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(burst.Select(output => SessionOutputDtoMapper.FromDto(output).GetType()), observed.Select(output => output.Type));
        Assert.Equal(["burst-one ", "burst-two", "burst-one burst-two"], observed.Where(output => output.Text is not null).Select(output => output.Text));
        var usage = Assert.Single(observed, output => output.Type == typeof(UsageOutput));
        Assert.Contains("in=25 out=5", usage.Usage);
        Assert.Contains("ctx", usage.Usage);
        if (approval)
        {
            var prompt = Assert.Single(observed, output => output.Type == typeof(ToolInteractionRequest));
            Assert.True(prompt.Pending);
            Assert.False(prompt.Generating);
            Assert.Equal("Approval required", prompt.Status);
        }
        var completed = observed.Last();
        Assert.Equal("Ready", completed.Status);
        Assert.False(completed.Pending);
        Assert.False(completed.Generating);
        var screen = terminal.ToString();
        Assert.Equal(1, screen.Split("burst-one burst-two", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(transport.Invocations, call => call.Method == "RespondToInteraction");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dormant_client_does_not_create_an_actor_system(bool close)
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient("http://localhost", transport);
        if (close) Assert.Empty((await client.CloseAsync()).Inputs);
        Assert.False(client.HasLocalRuntime);
        Assert.Equal(0, transport.StartAttempts);
        Assert.Empty(transport.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_facade_close_calls_share_one_task_and_receipt(bool active)
    {
        var entered = Gate();
        var release = Gate();
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (_, _, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); }
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        Task? send = null;
        try
        {
            if (active)
            {
                await client.CreateSessionAsync(DaemonClient.TuiChannelType, TestContext.Current.CancellationToken);
                send = client.SendAsync("before-close", TestContext.Current.CancellationToken);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            var close = client.CloseAsync();
            Assert.Same(close, client.CloseAsync());
            if (active) Assert.False(close.IsCompleted);
            release.TrySetResult();
            var receipt = await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (send is not null) await send;
            Assert.Same(receipt, await client.CloseAsync());
            Assert.Empty(receipt.Inputs);
            Assert.Equal(active, client.HasLocalRuntime);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_late_admission_response_does_not_restore_generation_after_turn_completion()
    {
        var release = Gate();
        var completed = Gate();
        var transport = new FakeDaemonHubTransport();
        transport.VoidInvokeHook = (_, args, token) =>
        {
            transport.PushOutput(new SessionOutputDto { Type = "turn_completed", SessionId = (string)args[0]!, TurnNumber = new TurnNumber(1) });
            return release.Task.WaitAsync(token);
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        using var chat = new ChatViewModel(client, TimeProvider.System, new ModelCapabilities { ModelId = "test" }, new ChatNavigationState(), Paths);
        chat.OnActivated();
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        using var status = chat.StatusMessage.Subscribe(value =>
        {
            if (value == "Ready" && transport.Invocations.Any(call => call.Method == "SendMessage")) completed.TrySetResult();
        });
        try
        {
            await chat.SubmitAsync("first");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(chat.IsGenerating.Value);
            release.TrySetResult();
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.False(chat.IsGenerating.Value);
            Assert.Equal("Ready", chat.StatusMessage.Value);
        }
        finally { release.TrySetResult(); }
    }
}
