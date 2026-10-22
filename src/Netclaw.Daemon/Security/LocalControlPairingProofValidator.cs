// -----------------------------------------------------------------------
// <copyright file="LocalControlPairingProofValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Validates one local-control proof for one operation and records its nonce before
/// the operation runs. The bounded process-local cache prevents proof replay.
/// </summary>
internal sealed class LocalControlPairingProofValidator
{
    internal static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FutureClockSkew = TimeSpan.FromSeconds(5);
    internal const int MaximumLiveNonces = 1_024;

    private readonly LocalControlPairingProofProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalControlPairingProofValidator> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _liveNonces = new(StringComparer.Ordinal);

    public LocalControlPairingProofValidator(
        LocalControlPairingProofProtector protector,
        TimeProvider timeProvider,
        ILogger<LocalControlPairingProofValidator> logger)
    {
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal LocalControlPairingProofValidation ValidateAndConsume(LocalControlOperation operation, string proof)
    {
        LocalControlPairingProofPayload payload;
        try
        {
            payload = _protector.Unprotect(operation, proof);
        }
        catch (CryptographicException)
        {
            _logger.LogDebug("Rejected local-control proof: category=cryptographic-validation.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        if (payload.Version != LocalControlPairingProofProtector.CurrentVersion)
        {
            _logger.LogWarning("Rejected local-control proof: category=unsupported-version.");
            return LocalControlPairingProofValidation.UnsupportedVersion;
        }

        if (payload.Operation != operation.Code)
        {
            _logger.LogDebug("Rejected local-control proof: category=wrong-operation.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        var now = _timeProvider.GetUtcNow();
        if (payload.IssuedAt < now.Subtract(ProofLifetime)
            || payload.IssuedAt > now.Add(FutureClockSkew))
        {
            _logger.LogDebug("Rejected local-control proof: category=time-window.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        lock (_lock)
        {
            foreach (var expiredNonce in _liveNonces
                         .Where(entry => entry.Value < now)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _liveNonces.Remove(expiredNonce);
            }

            if (_liveNonces.ContainsKey(payload.Nonce))
            {
                _logger.LogWarning("Rejected local-control proof: category=replay.");
                return LocalControlPairingProofValidation.Unauthorized;
            }

            if (_liveNonces.Count >= MaximumLiveNonces)
            {
                _logger.LogError("Rejected local-control proof: category=replay-cache-capacity.");
                return LocalControlPairingProofValidation.CapacityExhausted;
            }

            _liveNonces.Add(payload.Nonce, payload.IssuedAt.Add(ProofLifetime));
        }

        return LocalControlPairingProofValidation.Valid;
    }
}

internal enum LocalControlPairingProofValidation
{
    Valid,
    Unauthorized,
    UnsupportedVersion,
    CapacityExhausted,
}

/// <summary>
/// A local-control request body. The proof in the body is the only authority.
/// </summary>
internal interface ILocalControlRequest
{
    string Proof { get; }

    /// <summary>The operation that the proof must grant, or null when the body names none.</summary>
    LocalControlOperation? Operation { get; }
}

/// <summary>
/// Shared request handling for the anonymous local-control endpoints. The proof in the
/// body is the only authority. A device token, a bootstrap token, or a loopback source
/// address does not replace it.
/// </summary>
internal sealed class LocalControlRequests
{
    internal const int MaximumRequestBytes = 4 * 1024;
    internal const int RefusalsPerWindow = 10;
    internal static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(1);

    private readonly LocalControlPairingProofValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;

    public LocalControlRequests(LocalControlPairingProofValidator validator, TimeProvider timeProvider)
    {
        _validator = validator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Reads the body and validates its proof. Returns the request when the proof is
    /// valid, or the HTTP refusal otherwise.
    /// </summary>
    internal async Task<(T? Request, IResult? Refusal)> AuthorizeAsync<T>(
        HttpContext httpContext,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class, ILocalControlRequest
    {
        var (request, refusal) = await ReadAsync(httpContext, typeInfo, cancellationToken);
        if (request is not null)
        {
            refusal = request.Operation is { } operation
                ? Reject(_validator.ValidateAndConsume(operation, request.Proof))
                : InvalidBody();
        }

        return refusal is null ? (request, null) : (null, CountRefusal(httpContext, refusal));
    }

    // Only refused requests count against the limit. A tunnel agent forwards remote
    // requests from loopback, so a limit on all requests lets remote junk block the
    // host CLI, which reads its OAuth result once.
    private IResult CountRefusal(HttpContext httpContext, IResult refusal)
    {
        var source = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = _timeProvider.GetUtcNow();
        int refusals;
        lock (_lock)
        {
            if (now >= _windowStart + RefusalWindow)
            {
                _refusals.Clear();
                _windowStart = now;
            }

            refusals = _refusals.GetValueOrDefault(source) + 1;
            _refusals[source] = refusals;
        }

        return refusals <= RefusalsPerWindow
            ? refusal
            : TypedResults.Json(
                new PairingErrorResponse("Too many refused local-control requests."),
                statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task<(T? Request, IResult? Refusal)> ReadAsync<T>(
        HttpContext httpContext,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        if (httpContext.Request.ContentLength > MaximumRequestBytes)
            return (null, TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));

        var body = new byte[MaximumRequestBytes + 1];
        var totalRead = 0;
        while (totalRead < body.Length)
        {
            var read = await httpContext.Request.Body.ReadAsync(body.AsMemory(totalRead), cancellationToken);
            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead > MaximumRequestBytes)
            return (null, TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));

        T? request;
        try
        {
            request = JsonSerializer.Deserialize(body.AsSpan(0, totalRead), typeInfo);
        }
        catch (JsonException)
        {
            return (null, InvalidBody());
        }

        return request is null ? (null, InvalidBody()) : (request, null);
    }

    private static IResult InvalidBody()
        => TypedResults.BadRequest(new PairingErrorResponse("The request body is invalid."));

    private static IResult? Reject(LocalControlPairingProofValidation validation) => validation switch
    {
        LocalControlPairingProofValidation.Valid => null,
        LocalControlPairingProofValidation.UnsupportedVersion =>
            TypedResults.BadRequest(new PairingErrorResponse("unsupported_protocol_version")),
        LocalControlPairingProofValidation.CapacityExhausted => TypedResults.Json(
            new PairingErrorResponse("Local control is temporarily unavailable."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => TypedResults.Json(
            new PairingErrorResponse("Invalid local-control proof."),
            statusCode: StatusCodes.Status401Unauthorized),
    };
}
