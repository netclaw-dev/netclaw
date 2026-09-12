// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using static Netclaw.Actors.Protocol.ModelCapabilityProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Singleton actor that caches model capabilities in memory.
/// Lazily resolves capabilities on first query per model ID.
/// Deduplicates concurrent queries for the same model using a waiting list.
/// </summary>
public sealed class ModelCapabilityActor : UntypedActor
{
    private readonly IModelCapabilityResolver _resolver;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly Dictionary<string, ModelCapabilitiesResponse> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IActorRef>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public ModelCapabilityActor(IModelCapabilityResolver resolver)
    {
        _resolver = resolver;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case GetModelCapabilities query:
                HandleQuery(query);
                break;

            case CapabilityResolved resolved:
                HandleResolved(resolved);
                break;
        }
    }

    private void HandleQuery(GetModelCapabilities query)
    {
        var modelKey = query.ModelId.Value;

        // Cache hit — respond immediately
        if (_cache.TryGetValue(modelKey, out var cached))
        {
            Sender.Tell(cached);
            return;
        }

        // Already in-flight — add sender to waiting list
        if (_pending.TryGetValue(modelKey, out var waiters))
        {
            waiters.Add(Sender);
            return;
        }

        // First query for this model — start resolution
        _pending[modelKey] = [Sender];
        var self = Self;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await _resolver.ResolveAsync(query.ModelId.Value, cts.Token);

                var input = result?.InputModalities ?? ModelModality.Text;
                var output = result?.OutputModalities ?? ModelModality.Text;

                self.Tell(new CapabilityResolved(query.ModelId, input, output, true));
            }
            catch
            {
                self.Tell(new CapabilityResolved(query.ModelId, ModelModality.Text, ModelModality.Text, false));
            }
        });
    }

    private void HandleResolved(CapabilityResolved resolved)
    {
        var response = new ModelCapabilitiesResponse(
            resolved.ModelId, resolved.InputModalities, resolved.OutputModalities);

        _cache[resolved.ModelId.Value] = response;

        if (!resolved.Success)
        {
            _log.Warning("Capability resolution failed for model {0}; cached text-only default", resolved.ModelId);
        }
        else
        {
            _log.Info("Cached capabilities for model {0}: input={1}, output={2}",
                resolved.ModelId, resolved.InputModalities, resolved.OutputModalities);
        }

        if (_pending.Remove(resolved.ModelId.Value, out var waiters))
        {
            foreach (var waiter in waiters)
                waiter.Tell(response);
        }
    }
}
