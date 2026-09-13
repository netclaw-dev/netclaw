// -----------------------------------------------------------------------
// <copyright file="PairingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Hosting;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Owns the active code and completes each exchange before the next ordinary command.
/// </summary>
internal sealed class PairingActor : ReceiveActor
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<PairedDevice, CancellationToken, Task> _addDevice;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private PendingCode? _pending;

    public PairingActor(DeviceRegistry registry, TimeProvider timeProvider)
        : this(registry.AddAsync, timeProvider)
    {
    }

    public PairingActor(Func<PairedDevice, CancellationToken, Task> addDevice, TimeProvider timeProvider)
    {
        _addDevice = addDevice;
        _timeProvider = timeProvider;

        Receive<GenerateCode>(command =>
        {
            try
            {
                Generate(command);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Pairing code generation failed unexpectedly.");
                Sender.Tell(new Status.Failure(new InvalidOperationException("The pairing code request failed unexpectedly.")));
                throw;
            }
        });
        ReceiveAsync<ExchangeCode>(ExchangeAsync);
        Receive<GetPendingExpiry>(_ => Sender.Tell(new PendingExpiry(GetExpiry())));
    }

    protected override void PostStop()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.PostStop();
    }

    private void Generate(GenerateCode command)
    {
        if (command.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new Status.Failure(new OperationCanceledException(command.CancellationToken)));
            return;
        }

        // The 32-symbol alphabet divides the byte range evenly, so modulo mapping has no bias.
        var bytes = RandomNumberGenerator.GetBytes(8);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];

        var code = new string(chars);
        var expiresAt = _timeProvider.GetUtcNow().Add(CodeLifetime);
        _pending = new PendingCode(code, expiresAt);
        _log.Info("Generated a host pairing code with expiration {ExpiresAt:o}.", expiresAt);
        Sender.Tell(new PairingCodeResultDto($"{code[..4]}-{code[4..]}", expiresAt));
    }

    private async Task ExchangeAsync(ExchangeCode command)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            command.CancellationToken, _lifetimeCancellation.Token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (GetExpiry() is null)
            {
                Sender.Tell(PairingExchangeResult.NoCode());
                return;
            }

            var normalized = command.Code.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!string.Equals(normalized, _pending!.Code, StringComparison.Ordinal))
            {
                Sender.Tell(PairingExchangeResult.InvalidCode());
                return;
            }

            var rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var now = _timeProvider.GetUtcNow();
            var device = new PairedDevice
            {
                Name = command.DeviceName.Trim(),
                TokenHash = PairedDevice.ComputeTokenHash(rawToken, salt),
                Salt = salt,
                CreatedAt = now,
                LastUsedAt = now,
            };

            try
            {
                await _addDevice(device, cancellation.Token);
            }
            catch (DeviceNameConflictException ex)
            {
                Sender.Tell(PairingExchangeResult.DuplicateName(ex.Message));
                return;
            }

            // ReceiveAsync keeps the mailbox suspended across the write. After commit, neither
            // elapsed time nor caller cancellation can make the admitted code reusable.
            _pending = null;
            Sender.Tell(PairingExchangeResult.Success(rawToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            // A recoverable store failure must not restart the actor and erase the retained code.
            _log.Warning("Pairing exchange failed: {FailureType}.", ex.GetType().Name);
            Sender.Tell(new Status.Failure(ex));
        }
        catch (Exception ex)
        {
            // Supervision restarts the actor but does not complete an HTTP caller's Ask.
            _log.Error(ex, "Pairing exchange failed unexpectedly.");
            Sender.Tell(new Status.Failure(new InvalidOperationException("The pairing exchange failed unexpectedly.")));
            throw;
        }
    }

    private DateTimeOffset? GetExpiry()
    {
        if (_pending is not null && _timeProvider.GetUtcNow() >= _pending.ExpiresAt)
            _pending = null;
        return _pending?.ExpiresAt;
    }

    private sealed record PendingCode(string Code, DateTimeOffset ExpiresAt);

    // These messages stay inside the daemon process. No HTTP context or bearer token crosses this boundary.
    internal sealed record GenerateCode(CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

    internal sealed record ExchangeCode(string Code, string DeviceName, CancellationToken CancellationToken)
        : INoSerializationVerificationNeeded
    {
        public override string ToString() => nameof(ExchangeCode);
    }

    internal sealed class GetPendingExpiry : INoSerializationVerificationNeeded
    {
        public static GetPendingExpiry Instance { get; } = new();
        private GetPendingExpiry() { }
    }

    internal sealed record PendingExpiry(DateTimeOffset? ExpiresAt) : INoSerializationVerificationNeeded;
}

internal static class PairingActorHostingExtensions
{
    internal static AkkaConfigurationBuilder WithPairingActor(this AkkaConfigurationBuilder builder)
        => builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(resolver.Props<PairingActor>(), "pairing");
            registry.Register<PairingActor>(actor);
        });
}

internal sealed record PairingExchangeResult
{
    public override string ToString() => $"{nameof(PairingExchangeResult)}({Status})";

    private PairingExchangeResult(PairingExchangeStatus status, string? token, string? error)
    {
        Status = status;
        Token = token;
        Error = error;
    }

    internal PairingExchangeStatus Status { get; }

    internal string? Token { get; }

    internal string? Error { get; }

    internal static PairingExchangeResult Success(string token) =>
        new(PairingExchangeStatus.Success, token, null);

    internal static PairingExchangeResult NoCode() =>
        new(PairingExchangeStatus.NoCode, null, null);

    internal static PairingExchangeResult InvalidCode() =>
        new(PairingExchangeStatus.InvalidCode, null, null);

    internal static PairingExchangeResult DuplicateName(string error) =>
        new(PairingExchangeStatus.DuplicateName, null, error);
}

internal enum PairingExchangeStatus
{
    Success,
    NoCode,
    InvalidCode,
    DuplicateName,
}
