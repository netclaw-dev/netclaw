// -----------------------------------------------------------------------
// <copyright file="PairingActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

public sealed class PairingActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time = new(Start);
    private NetclawPaths _paths = null!;
    private DeviceRegistry _registry = null!;
    private bool _failNextRegistryWrite;
    private byte[]? _bytesBeforeReplacement;
    private IReadOnlyList<PairedDevice>? _devicesInFailedTempFile;
    private UnixFileMode? _failedTempFileMode;

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        _paths = new NetclawPaths(_dir.Path);
        _registry = new DeviceRegistry(
            _paths,
            _time,
            NullLogger<DeviceRegistry>.Instance,
            InspectAndHardenRegistryTempFile);
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_registry);
        Directory.CreateDirectory(_paths.LogsDirectory);
        services.AddSingleton<ILoggerProvider>(_ => new RollingFileLoggerProvider(_paths.DaemonLogPath, _time));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithNetclawActorLogging(LogLevel.Error).WithPairingActor();
    }

    protected override async Task AfterAllAsync()
    {
        try
        {
            await base.AfterAllAsync();
        }
        finally
        {
            _dir.Dispose();
        }
    }

    [Fact]
    public async Task Generate_code_uses_reduced_alphabet_and_reports_five_minute_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();

        var result = await GenerateCodeAsync(actor, ct);

        Assert.Matches(
            "^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$",
            result.FormattedCode);
        Assert.Equal(Start.AddMinutes(5), result.ExpiresAt);
        Assert.Equal(result.ExpiresAt, await GetPendingExpiryAsync(actor, ct));
    }

    [Fact]
    public void Secret_bearing_message_text_excludes_codes_tokens_names_and_errors()
    {
        const string Code = "2345-6789";
        const string DeviceName = "private-laptop";
        const string Token = "secret-bearer-token";
        const string Error = "A paired device named 'private-laptop' already exists.";
        var expiry = Start.AddMinutes(5);

        var commandText = new PairingActor.ExchangeCode(Code, DeviceName, CancellationToken.None).ToString();
        var codeResultText = new PairingCodeResultDto(Code, expiry).ToString();
        var successText = PairingExchangeResult.Success(Token).ToString();
        var failureText = PairingExchangeResult.DuplicateName(Error).ToString();

        Assert.Equal(nameof(PairingActor.ExchangeCode), commandText);
        Assert.Equal(nameof(PairingCodeResultDto), codeResultText);
        Assert.Equal("PairingExchangeResult(Success)", successText);
        Assert.Equal("PairingExchangeResult(DuplicateName)", failureText);
        Assert.DoesNotContain(Code, commandText, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceName, commandText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, successText, StringComparison.Ordinal);
        Assert.DoesNotContain(Error, failureText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_code_replaces_the_prior_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();
        var first = await GenerateCodeAsync(actor, ct);
        var second = await GenerateCodeAsync(actor, ct);

        var staleResult = await ExchangeCodeAsync(actor, first.FormattedCode, "laptop", ct);
        var currentResult = await ExchangeCodeAsync(actor, second.FormattedCode, "laptop", ct);

        Assert.Equal(PairingExchangeStatus.InvalidCode, staleResult.Status);
        Assert.Equal(PairingExchangeStatus.Success, currentResult.Status);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
    }

    [Fact]
    public async Task Unexpected_generation_failure_returns_safe_failure_restarts_and_allows_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ThrowOnceTimeProvider(Start);
        var actor = CreateControlledActor((_, _) => Task.CompletedTask, time);

        actor.Tell(new PairingActor.GenerateCode(ct), TestActor);
        actor.Tell(PairingActor.GetPendingExpiry.Instance, TestActor);
        var failure = await ExpectMsgAsync<Status.Failure>(
            RemainingOrDefault,
            cancellationToken: ct);
        var pending = await ExpectMsgAsync<PairingActor.PendingExpiry>(
            RemainingOrDefault,
            cancellationToken: ct);

        Assert.Equal(
            "The pairing code request failed unexpectedly.",
            Assert.IsType<InvalidOperationException>(failure.Cause).Message);
        Assert.Null(failure.Cause.InnerException);
        Assert.Null(pending.ExpiresAt);

        var retry = await GenerateCodeAsync(actor, ct);
        Assert.Equal(Start.AddMinutes(5), retry.ExpiresAt);
        await AssertDaemonFailureLogAsync(
            "Pairing code generation failed unexpectedly.",
            new ApplicationException("Secret clock failure."),
            [retry.FormattedCode],
            ct);
    }

    [Fact]
    public async Task Exchange_accepts_case_and_separator_variants_then_consumes_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();
        var code = await GenerateCodeAsync(actor, ct);
        var normalizedVariant = code.FormattedCode.Replace("-", string.Empty).ToLowerInvariant();

        var first = await ExchangeCodeAsync(actor, normalizedVariant, " laptop ", ct);
        var second = await ExchangeCodeAsync(actor, code.FormattedCode, "phone", ct);

        Assert.Equal(PairingExchangeStatus.Success, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.Token));
        Assert.Equal(PairingExchangeStatus.NoCode, second.Status);
        Assert.Equal("laptop", Assert.Single(await _registry.ListAsync(ct)).Name);
    }

    [Fact]
    public async Task Duplicate_device_name_retains_code_for_a_different_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();
        var (_, existingDevice) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(existingDevice, ct);
        var code = await GenerateCodeAsync(actor, ct);

        var duplicate = await ExchangeCodeAsync(actor, code.FormattedCode, "LAPTOP", ct);

        Assert.Equal(PairingExchangeStatus.DuplicateName, duplicate.Status);
        Assert.Contains("already exists", duplicate.Error, StringComparison.Ordinal);
        Assert.Equal(code.ExpiresAt, await GetPendingExpiryAsync(actor, ct));

        var retry = await ExchangeCodeAsync(actor, code.FormattedCode, "phone", ct);
        Assert.Equal(PairingExchangeStatus.Success, retry.Status);
        Assert.Equal(2, (await _registry.ListAsync(ct)).Count);
    }

    [Fact]
    public async Task Exchange_distinguishes_no_code_invalid_code_and_exact_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();

        var absent = await ExchangeCodeAsync(actor, "0000-0000", "laptop", ct);
        var code = await GenerateCodeAsync(actor, ct);
        var invalid = await ExchangeCodeAsync(actor, "0000-0000", "laptop", ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        var expired = await ExchangeCodeAsync(actor, code.FormattedCode, "laptop", ct);

        Assert.Equal(PairingExchangeStatus.NoCode, absent.Status);
        Assert.Equal(PairingExchangeStatus.InvalidCode, invalid.Status);
        Assert.Equal(PairingExchangeStatus.NoCode, expired.Status);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
        Assert.Empty(await _registry.ListAsync(ct));
    }

    [Fact]
    public async Task Competing_exchanges_allow_exactly_one_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();
        var code = await GenerateCodeAsync(actor, ct);

        for (var index = 0; index < 8; index++)
            actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, $"device-{index}", ct), TestActor);

        var results = new List<PairingExchangeResult>();
        for (var index = 0; index < 8; index++)
        {
            results.Add(await ExpectMsgAsync<PairingExchangeResult>(
                RemainingOrDefault,
                cancellationToken: ct));
        }

        Assert.Single(results, result => result.Status == PairingExchangeStatus.Success);
        Assert.All(
            results.Where(result => result.Status != PairingExchangeStatus.Success),
            result => Assert.Equal(PairingExchangeStatus.NoCode, result.Status));
        Assert.Single(await _registry.ListAsync(ct));
    }

    [Fact]
    public async Task Registry_write_failure_preserves_bytes_cache_code_and_same_code_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = ActorRegistry.Get<PairingActor>();
        var (existingToken, existingDevice) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(existingDevice, ct);
        var priorBytes = await File.ReadAllBytesAsync(_paths.DevicesPath, ct);
        _failNextRegistryWrite = true;
        var code = await GenerateCodeAsync(actor, ct);

        actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, "tablet", ct), TestActor);
        var failure = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);

        Assert.Equal(
            "Injected failure before registry replacement.",
            Assert.IsType<IOException>(failure.Cause).Message);
        Assert.Equal(priorBytes, _bytesBeforeReplacement);
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(_paths.DevicesPath, ct));
        Assert.Equal(existingDevice, Assert.Single(await _registry.ListAsync(ct)));
        Assert.Equal(code.ExpiresAt, await GetPendingExpiryAsync(actor, ct));
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<PairedDevice>>(_devicesInFailedTempFile).Count);
        Assert.Contains(_devicesInFailedTempFile!, device => device.Name == "tablet");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                _failedTempFileMode);
        }

        var reopenedAfterFailure = CreateRegistry();
        Assert.Equal(existingDevice, await reopenedAfterFailure.LookupByTokenAsync(existingToken, ct));

        var retry = await ExchangeCodeAsync(actor, code.FormattedCode, "tablet", ct);

        Assert.Equal(PairingExchangeStatus.Success, retry.Status);
        Assert.NotNull(retry.Token);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
        var reopenedAfterRetry = CreateRegistry();
        Assert.Equal(existingDevice, await reopenedAfterRetry.LookupByTokenAsync(existingToken, ct));
        Assert.Equal("tablet", (await reopenedAfterRetry.LookupByTokenAsync(retry.Token!, ct))?.Name);
        Assert.Equal(2, (await reopenedAfterRetry.ListAsync(ct)).Count);
        Assert.Empty(Directory.GetFiles(_paths.ConfigDirectory, "*.tmp-*"));
    }

    [Fact]
    public async Task Expiry_after_exchange_admission_does_not_split_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = NewSignal<PairedDevice>();
        var release = NewSignal();
        var devices = new ConcurrentQueue<PairedDevice>();
        var actor = CreateControlledActor(async (device, storeToken) =>
        {
            entered.TrySetResult(device);
            await release.Task.WaitAsync(storeToken);
            devices.Enqueue(device);
        });
        var code = await GenerateCodeAsync(actor, ct);

        actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct), TestActor);
        await entered.Task.WaitAsync(RemainingOrDefault, ct);
        _time.Advance(TimeSpan.FromMinutes(6));
        release.TrySetResult();
        var result = await ExpectMsgAsync<PairingExchangeResult>(RemainingOrDefault, cancellationToken: ct);

        Assert.Equal(PairingExchangeStatus.Success, result.Status);
        Assert.Single(devices);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
    }

    [Fact]
    public async Task Held_write_keeps_competing_exchange_and_generation_in_mailbox_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = NewSignal();
        var release = NewSignal();
        var writeCount = 0;
        var actor = CreateControlledActor(async (_, storeToken) =>
        {
            Interlocked.Increment(ref writeCount);
            entered.TrySetResult();
            await release.Task.WaitAsync(storeToken);
        });
        var code = await GenerateCodeAsync(actor, ct);
        var admittedCaller = CreateTestProbe("admitted-caller");
        var queuedCaller = CreateTestProbe("queued-caller");

        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct),
            admittedCaller.Ref);
        await entered.Task.WaitAsync(RemainingOrDefault, ct);
        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "phone", ct),
            queuedCaller.Ref);
        actor.Tell(new PairingActor.GenerateCode(ct), queuedCaller.Ref);
        release.TrySetResult();

        var first = await admittedCaller.ExpectMsgAsync<PairingExchangeResult>(
            RemainingOrDefault,
            cancellationToken: ct);
        var second = await queuedCaller.ExpectMsgAsync<PairingExchangeResult>(
            RemainingOrDefault,
            cancellationToken: ct);
        var generated = await queuedCaller.ExpectMsgAsync<PairingCodeResultDto>(
            RemainingOrDefault,
            cancellationToken: ct);

        Assert.Equal(PairingExchangeStatus.Success, first.Status);
        Assert.Equal(PairingExchangeStatus.NoCode, second.Status);
        Assert.Equal(1, writeCount);
        Assert.Equal(generated.ExpiresAt, await GetPendingExpiryAsync(actor, ct));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("json")]
    [InlineData("canceled")]
    public async Task Expected_write_failure_replies_with_failure_without_restart(string failureKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var failNext = true;
        var devices = new ConcurrentQueue<PairedDevice>();
        var actor = CreateControlledActor((device, _) =>
        {
            if (failNext)
            {
                failNext = false;
                return Task.FromException(CreateExpectedFailure(failureKind));
            }

            devices.Enqueue(device);
            return Task.CompletedTask;
        });
        var code = await GenerateCodeAsync(actor, ct);

        actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct), TestActor);
        var failure = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);

        Assert.IsType(CreateExpectedFailure(failureKind).GetType(), failure.Cause);
        Assert.Equal(code.ExpiresAt, await GetPendingExpiryAsync(actor, ct));

        var retry = await ExchangeCodeAsync(actor, code.FormattedCode, "laptop", ct);

        Assert.Equal(PairingExchangeStatus.Success, retry.Status);
        Assert.Single(devices);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
    }

    [Fact]
    public async Task Canceled_queued_commands_do_not_call_store_or_replace_or_consume_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = NewSignal();
        var release = NewSignal();
        var writeCount = 0;
        var actor = CreateControlledActor(async (_, storeToken) =>
        {
            var call = Interlocked.Increment(ref writeCount);
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(storeToken);
                throw new IOException("First write failed.");
            }
        });
        var code = await GenerateCodeAsync(actor, ct);
        actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct), TestActor);
        await entered.Task.WaitAsync(RemainingOrDefault, ct);
        using var canceledCommand = new CancellationTokenSource();
        canceledCommand.Cancel();
        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "phone", canceledCommand.Token),
            TestActor);
        actor.Tell(new PairingActor.GenerateCode(canceledCommand.Token), TestActor);
        release.TrySetResult();

        var firstFailure = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);
        var canceledExchange = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);
        var canceledGeneration = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);

        Assert.IsType<IOException>(firstFailure.Cause);
        Assert.IsAssignableFrom<OperationCanceledException>(canceledExchange.Cause);
        Assert.IsAssignableFrom<OperationCanceledException>(canceledGeneration.Cause);
        Assert.Equal(1, writeCount);
        Assert.Equal(code.ExpiresAt, await GetPendingExpiryAsync(actor, ct));

        var retry = await ExchangeCodeAsync(actor, code.FormattedCode, "tablet", ct);
        Assert.Equal(PairingExchangeStatus.Success, retry.Status);
        Assert.Equal(2, writeCount);
    }

    [Fact]
    public async Task Cancellation_during_write_retains_code_for_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = NewSignal();
        var neverCompletes = NewSignal();
        var writeCount = 0;
        var actor = CreateControlledActor(async (_, storeToken) =>
        {
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                entered.TrySetResult();
                await neverCompletes.Task.WaitAsync(storeToken);
            }
        });
        var code = await GenerateCodeAsync(actor, ct);
        using var command = new CancellationTokenSource();
        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "laptop", command.Token),
            TestActor);
        await entered.Task.WaitAsync(RemainingOrDefault, ct);

        command.Cancel();
        var failure = await ExpectMsgAsync<Status.Failure>(RemainingOrDefault, cancellationToken: ct);

        Assert.IsAssignableFrom<OperationCanceledException>(failure.Cause);
        Assert.Equal(code.ExpiresAt, await GetPendingExpiryAsync(actor, ct));

        var retry = await ExchangeCodeAsync(actor, code.FormattedCode, "laptop", ct);
        Assert.Equal(PairingExchangeStatus.Success, retry.Status);
        Assert.Equal(2, writeCount);
    }

    [Fact]
    public async Task Cancellation_after_store_commit_does_not_roll_back_success()
    {
        var ct = TestContext.Current.CancellationToken;
        using var command = new CancellationTokenSource();
        var devices = new ConcurrentQueue<PairedDevice>();
        var actor = CreateControlledActor((device, _) =>
        {
            devices.Enqueue(device);
            command.Cancel();
            return Task.CompletedTask;
        });
        var code = await GenerateCodeAsync(actor, ct);

        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "laptop", command.Token),
            TestActor);
        var result = await ExpectMsgAsync<PairingExchangeResult>(RemainingOrDefault, cancellationToken: ct);

        Assert.Equal(PairingExchangeStatus.Success, result.Status);
        Assert.Single(devices);
        Assert.Null(await GetPendingExpiryAsync(actor, ct));
    }

    [Fact]
    public async Task Actor_stop_cancels_an_active_store_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = NewSignal();
        var canceled = NewSignal();
        var neverCompletes = NewSignal();
        var actor = CreateControlledActor(async (_, storeToken) =>
        {
            entered.TrySetResult();
            try
            {
                await neverCompletes.Task.WaitAsync(storeToken);
            }
            catch (OperationCanceledException) when (storeToken.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        var code = await GenerateCodeAsync(actor, ct);
        var replyProbe = CreateTestProbe("stopped-write-reply");
        var watcher = CreateTestProbe("stopped-write-watcher");
        actor.Tell(
            new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct),
            replyProbe.Ref);
        await entered.Task.WaitAsync(RemainingOrDefault, ct);
        watcher.Watch(actor);

        Sys.Stop(actor);

        await canceled.Task.WaitAsync(RemainingOrDefault, ct);
        var failure = await replyProbe.ExpectMsgAsync<Status.Failure>(
            RemainingOrDefault,
            cancellationToken: ct);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.Cause);
        await watcher.ExpectTerminatedAsync(actor, RemainingOrDefault, cancellationToken: ct);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("invalid-operation")]
    public async Task Unexpected_write_failure_returns_safe_failure_restarts_and_clears_code(
        string failureKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var storeFailure = CreateUnexpectedFailure(failureKind);
        var actor = CreateControlledActor(
            (_, _) => Task.FromException(storeFailure));
        var code = await GenerateCodeAsync(actor, ct);

        actor.Tell(new PairingActor.ExchangeCode(code.FormattedCode, "laptop", ct), TestActor);
        actor.Tell(PairingActor.GetPendingExpiry.Instance, TestActor);
        var failure = await ExpectMsgAsync<Status.Failure>(
            RemainingOrDefault,
            cancellationToken: ct);
        var pending = await ExpectMsgAsync<PairingActor.PendingExpiry>(
            RemainingOrDefault,
            cancellationToken: ct);

        Assert.Equal(
            "The pairing exchange failed unexpectedly.",
            Assert.IsType<InvalidOperationException>(failure.Cause).Message);
        Assert.Null(failure.Cause.InnerException);
        Assert.Null(pending.ExpiresAt);
        var next = await GenerateCodeAsync(actor, ct);
        Assert.Equal(Start.AddMinutes(5), next.ExpiresAt);
        await AssertDaemonFailureLogAsync(
            "Pairing exchange failed unexpectedly.",
            storeFailure,
            [code.FormattedCode, next.FormattedCode, "laptop"],
            ct);
    }

    private async Task AssertDaemonFailureLogAsync(
        string message,
        Exception exception,
        IReadOnlyList<string> excludedValues,
        CancellationToken cancellationToken)
    {
        await AwaitAssertAsync(async () =>
        {
            var path = Assert.Single(Directory.GetFiles(_paths.LogsDirectory, "daemon-*.log"));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var log = await reader.ReadToEndAsync(cancellationToken);

            // Require exception details on this entry, not only on the separate supervision entry.
            Assert.Contains($"{message}\nCause: {exception.GetType().FullName}: {exception.Message}", log.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("[ERR]", log, StringComparison.Ordinal);
            Assert.Contains("at Netclaw.Daemon.Security.PairingActor", log, StringComparison.Ordinal);
            foreach (var value in excludedValues)
                Assert.DoesNotContain(value, log, StringComparison.Ordinal);
        }, RemainingOrDefault, cancellationToken: cancellationToken);
    }

    private IActorRef CreateControlledActor(
        Func<PairedDevice, CancellationToken, Task> addDevice)
    {
        return CreateControlledActor(addDevice, _time);
    }

    private IActorRef CreateControlledActor(
        Func<PairedDevice, CancellationToken, Task> addDevice,
        TimeProvider timeProvider)
    {
        return Sys.ActorOf(
            Props.Create(() => new PairingActor(addDevice, timeProvider)),
            $"pairing-controlled-{Guid.NewGuid():N}");
    }

    private async Task<PairingCodeResultDto> GenerateCodeAsync(
        IActorRef actor,
        CancellationToken cancellationToken)
    {
        actor.Tell(new PairingActor.GenerateCode(cancellationToken), TestActor);
        return await ExpectMsgAsync<PairingCodeResultDto>(
            RemainingOrDefault,
            cancellationToken: cancellationToken);
    }

    private async Task<PairingExchangeResult> ExchangeCodeAsync(
        IActorRef actor,
        string code,
        string deviceName,
        CancellationToken cancellationToken)
    {
        actor.Tell(new PairingActor.ExchangeCode(code, deviceName, cancellationToken), TestActor);
        return await ExpectMsgAsync<PairingExchangeResult>(
            RemainingOrDefault,
            cancellationToken: cancellationToken);
    }

    private async Task<DateTimeOffset?> GetPendingExpiryAsync(
        IActorRef actor,
        CancellationToken cancellationToken)
    {
        actor.Tell(PairingActor.GetPendingExpiry.Instance, TestActor);
        var pending = await ExpectMsgAsync<PairingActor.PendingExpiry>(
            RemainingOrDefault,
            cancellationToken: cancellationToken);
        return pending.ExpiresAt;
    }

    private DeviceRegistry CreateRegistry()
    {
        return new DeviceRegistry(
            _paths,
            _time,
            NullLogger<DeviceRegistry>.Instance);
    }

    private void InspectAndHardenRegistryTempFile(string tempPath)
    {
        if (!OperatingSystem.IsWindows())
            _failedTempFileMode = File.GetUnixFileMode(tempPath);

        AtomicFile.HardenOwnerOnly(tempPath);
        if (!_failNextRegistryWrite)
            return;

        _failNextRegistryWrite = false;
        _bytesBeforeReplacement = File.ReadAllBytes(_paths.DevicesPath);
        _devicesInFailedTempFile = JsonSerializer.Deserialize<List<PairedDevice>>(
            File.ReadAllText(tempPath));
        throw new IOException("Injected failure before registry replacement.");
    }

    private static Exception CreateExpectedFailure(string failureKind)
    {
        return failureKind switch
        {
            "io" => new IOException("Injected I/O failure."),
            "unauthorized" => new UnauthorizedAccessException("Injected access failure."),
            "json" => new JsonException("Injected JSON failure."),
            "canceled" => new OperationCanceledException("Injected cancellation."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null),
        };
    }

    private static Exception CreateUnexpectedFailure(string failureKind)
    {
        return failureKind switch
        {
            "application" => new ApplicationException("Secret application failure."),
            "invalid-operation" => new InvalidOperationException("Secret invalid operation."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null),
        };
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ThrowOnceTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private int _mustThrow = 1;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _mustThrow, 0) == 1)
                throw new ApplicationException("Secret clock failure.");

            return value;
        }
    }
}
