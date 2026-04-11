using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Outcome of a <see cref="ChannelIngressCapabilityQuery"/>. The discriminator
/// is <see cref="Success"/>; on failure, <see cref="FailureReason"/> carries a
/// short human-readable explanation that channel adapters surface to the user
/// as part of a rejection reply. On success, <see cref="InputModalities"/> and
/// <see cref="OutputModalities"/> carry the model's reported modality flags
/// exactly as the <see cref="ModelCapabilityActor"/> resolved them.
/// Failure modes include timeouts and unexpected exceptions. Adapters SHALL
/// NOT guess modalities when <see cref="Success"/> is false — per the
/// no-silent-fallbacks rule, they SHALL post a user-visible reply and skip
/// the attachment.
/// </summary>
public sealed record CapabilityQueryResult(
    bool Success,
    ModelModality InputModalities,
    ModelModality OutputModalities,
    string? FailureReason)
{
    public static CapabilityQueryResult Ok(ModelModality input, ModelModality output)
        => new(true, input, output, FailureReason: null);

    public static CapabilityQueryResult TimedOut(string modelId)
        => new(
            Success: false,
            InputModalities: default,
            OutputModalities: default,
            FailureReason: $"ModelCapabilityActor did not respond within the capability query deadline for model '{modelId}'.");

    public static CapabilityQueryResult Failed(string modelId, string reason)
        => new(
            Success: false,
            InputModalities: default,
            OutputModalities: default,
            FailureReason: $"Capability query failed for model '{modelId}': {reason}");
}

/// <summary>
/// Shared helper for channel ingress actors (Slack, Discord, Teams, web, …)
/// to query <see cref="ModelCapabilityActor"/> for the active model's
/// modalities before deciding whether to inline an inbound attachment as
/// <c>DataContent</c>. Uses a short timeout because cache hits resolve in
/// a few milliseconds, and a stuck capability actor indicates a system
/// problem the adapter SHALL surface loudly rather than papering over.
/// </summary>
public static class ChannelIngressCapabilityQuery
{
    /// <summary>
    /// Default timeout applied when callers do not specify one. The capability
    /// actor resolves cache hits in ~1–10 ms, so 2 seconds leaves ample headroom
    /// for a cold-cache round-trip while still failing loudly on a hung actor.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public static async Task<CapabilityQueryResult> QueryAsync(
        IActorRef capabilityActor,
        string modelId,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (capabilityActor is null)
            throw new ArgumentNullException(nameof(capabilityActor));
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("modelId must be non-empty", nameof(modelId));

        var deadline = timeout ?? DefaultTimeout;

        try
        {
            var response = await capabilityActor
                .Ask<ModelCapabilitiesResponse>(
                    new GetModelCapabilities(modelId),
                    deadline,
                    cancellationToken)
                .ConfigureAwait(false);

            return CapabilityQueryResult.Ok(response.InputModalities, response.OutputModalities);
        }
        catch (AskTimeoutException)
        {
            return CapabilityQueryResult.TimedOut(modelId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityQueryResult.Failed(modelId, ex.Message);
        }
    }
}
