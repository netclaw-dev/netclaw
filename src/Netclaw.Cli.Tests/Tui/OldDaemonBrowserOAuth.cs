// -----------------------------------------------------------------------
// <copyright file="OldDaemonBrowserOAuth.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using R3;
using Termina.Input;
using Termina.Reactive;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

internal sealed class OldDaemonBrowserOAuth : IHttpClientFactory
{
    private const string ProviderType = "openai";

    // Without RunContinuationsAsynchronously, the CLI code that awaits the callback
    // runs on the thread that calls SetResult.
    private readonly TaskCompletionSource<HttpResponseMessage> _callbackResponse = new();
    private readonly TaskCompletionSource _callbackRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _authorizationCompleted;
    private int _pausedThreadId = -1;
    private Action? _whilePaused;
    private bool _paused;

    public void CompleteAuthorization() => Volatile.Write(ref _authorizationCompleted, 1);

    public HttpClient CreateClient(string name) => new(new Handler(this));

    public DaemonApi CreateApi(NetclawPaths paths)
        => new(this, new ConfigurationBuilder().Build(), paths, DaemonApiTestDependencies.CreateProofProtector(), TimeProvider.System);

    public ProviderDescriptorRegistry PauseViewBuildsAtTheProviderName(ProviderDescriptorRegistry registry)
        => new(registry.KnownTypeKeys.Select(key => key == ProviderType
            ? new PausingDescriptor(registry.Get(key), this)
            : registry.Get(key)));

    public async Task TypeAKeyWhileTheFlowReportsARefusalOnTheTestThreadAsync(
        VirtualInputSource input, ReactiveViewModel vm, VirtualTerminal terminal, CancellationToken ct)
    {
        await ExpectAsync(() => terminal.Contains("Paste the redirect URL"), "The paste box is not shown", terminal, ct);
        input.EnqueuePaste("http://localhost:1455/auth/callback?code=first&state=flow-state");
        input.EnqueueKey(ConsoleKey.Enter);
        await ExpectAsync(() => _callbackRequested.Task.IsCompleted, "The pasted URL did not reach the daemon", terminal, ct);
        input.EnqueuePaste("partial");
        await ExpectAsync(() => terminal.Contains("partial"), "The pasted text is not shown", terminal, ct);

        using var keyHandled = new ManualResetEventSlim();
        using var keySubscription = vm.Input.OfType<IInputEvent, KeyPressed>()
            .Where(key => key.KeyInfo.KeyChar == 'x')
            .Subscribe(_ => vm.Post(keyHandled.Set));

        _whilePaused = () =>
        {
            input.EnqueueString("x");
            keyHandled.Wait(ct);
        };
        Volatile.Write(ref _pausedThreadId, Environment.CurrentManagedThreadId);
        try
        {
            _callbackResponse.SetResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
        finally
        {
            Volatile.Write(ref _pausedThreadId, -1);
        }

        if (!_paused)
            input.EnqueueString("x");
        keyHandled.Wait(ct);

        await ExpectAsync(() => terminal.Contains("partialx"), "The typed key is lost", terminal, ct);
    }

    private static async Task ExpectAsync(Func<bool> predicate, string failure, VirtualTerminal terminal, CancellationToken ct)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (stepCts.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                Assert.Fail($"{failure}. Screen:\n{string.Join("\n", terminal.GetAllLines())}");
            }

            await Task.Yield();
        }
    }

    private void OnDisplayNameRead()
    {
        if (_paused || Volatile.Read(ref _pausedThreadId) != Environment.CurrentManagedThreadId)
            return;

        _paused = true;
        _whilePaused!();
    }

    private Task<HttpResponseMessage> Respond(HttpRequestMessage request)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/api/provider/oauth/start":
                // An empty URL makes Process.Start throw before it launches anything.
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(new
                {
                    authorizationUrl = "",
                    state = "flow-state",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                }));
            case "/api/provider/oauth/status/flow-state":
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(new
                {
                    status = Volatile.Read(ref _authorizationCompleted) == 1 ? "Completed" : "Pending",
                }));
            case "/api/provider/oauth/callback":
                _callbackRequested.TrySetResult();
                return _callbackResponse.Task;
            case "/api/local-control/v1/provider-oauth-result":
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            case var path:
                throw new InvalidOperationException($"Unexpected daemon request: {path}");
        }
    }

    private sealed class Handler(OldDaemonBrowserOAuth daemon) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => daemon.Respond(request);
    }

    private sealed class PausingDescriptor(IProviderDescriptor inner, OldDaemonBrowserOAuth daemon) : IProviderDescriptor
    {
        public string TypeKey => inner.TypeKey;

        public ThinkingDialect ResolveThinkingDialect(ProviderEntry entry) => inner.ResolveThinkingDialect(entry);

        public string DisplayName
        {
            get
            {
                daemon.OnDisplayNameRead();
                return inner.DisplayName;
            }
        }

        public string DefaultEndpoint => inner.DefaultEndpoint;
        public string ModelListingPath => inner.ModelListingPath;
        public IProviderAuth Auth => inner.Auth;

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => inner.ProbeAsync(entry, ct);
    }
}
