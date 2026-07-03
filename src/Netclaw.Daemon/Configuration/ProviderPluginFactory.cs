// -----------------------------------------------------------------------
// <copyright file="ProviderPluginFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Configuration.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Creates <see cref="IChatClient"/> instances using the plugin registry.
/// Replaces <c>ChatClientFactory</c> by delegating to the appropriate
/// <see cref="ILlmProviderPlugin.CreateChatClient"/>.
/// </summary>
public sealed class ProviderPluginFactory
{
    private readonly Dictionary<string, ProviderEntry> _providers;
    private readonly Dictionary<string, ILlmProviderPlugin> _plugins;

    public ProviderPluginFactory(
        Dictionary<string, ProviderEntry> providers,
        IEnumerable<ILlmProviderPlugin> plugins)
    {
        _providers = providers;
        _plugins = new Dictionary<string, ILlmProviderPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
            _plugins[plugin.TypeKey] = plugin;
    }

    public IChatClient Create(ModelReference model)
    {
        if (!_providers.TryGetValue(model.Provider, out var provider))
            throw new InvalidOperationException(
                $"Provider '{model.Provider}' not found. "
                + $"Configured: {string.Join(", ", _providers.Keys)}");

        if (!_plugins.TryGetValue(provider.Type, out var plugin))
            throw new InvalidOperationException(
                $"Unknown provider type '{provider.Type}'. "
                + $"Supported: {string.Join(", ", _plugins.Keys)}");

        var client = plugin.CreateChatClient(provider, model);
        var vendorOptions = plugin.CreateVendorOptionsSource(provider);
        IChatClient decorated = vendorOptions is null
            ? client
            : new VendorOptionsChatClient(client, vendorOptions);

        // Applied to every provider/role, not just curation's — any call site can carry the
        // NetclawChatOptionKeys.SuppressReasoning intent key, and this is the one seam every
        // chat client the daemon constructs passes through, so it is the only place that can
        // guarantee the intent key never reaches a raw provider SDK unmapped.
        return new ReasoningSuppressionChatClient(decorated, plugin.SuppressionDialect);
    }
}

internal sealed class VendorOptionsChatClient : DelegatingChatClient
{
    private readonly IVendorOptionsSource _vendorOptionsSource;

    public VendorOptionsChatClient(IChatClient innerClient, IVendorOptionsSource vendorOptionsSource)
        : base(innerClient)
    {
        _vendorOptionsSource = vendorOptionsSource;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        _vendorOptionsSource.Apply(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        _vendorOptionsSource.Apply(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// Maps the <see cref="NetclawChatOptionKeys.SuppressReasoning"/> intent key to the wire
/// dialect the wrapped provider plugin declares (<see cref="ILlmProviderPlugin.SuppressionDialect"/>),
/// then always removes the intent key — regardless of dialect — so it never leaks onto the wire
/// as an unrecognized field.
///
/// Verified adapter behavior (see PR body / decompiled Microsoft.Extensions.AI.OpenAI and
/// Anthropic.SDK ChatOptions→wire mappings): neither the official OpenAI SDK adapter
/// (<c>OpenAIChatClient</c>/<c>OpenAIResponsesChatClient</c>) nor Anthropic.SDK's
/// <c>AsIChatClient()</c> adapter ever iterates <see cref="ChatOptions.AdditionalProperties"/>
/// to copy arbitrary entries onto the request body — both only special-case a
/// <c>"strict"</c> flag inside AdditionalProperties for JSON-schema response formats. So today,
/// unmapped dialect keys sent to those providers are silently inert, not a source of live 400s.
/// This decorator still strips unconditionally for every dialect (including <see cref="ReasoningSuppressionDialect.None"/>)
/// so that behavior doesn't become load-bearing, and so a future provider plugin wrapping a
/// different (possibly stricter, possibly RawRepresentationFactory-driven) adapter is safe by
/// construction rather than by accident.
///
/// Mutates the incoming <see cref="ChatOptions"/> in place, matching
/// <see cref="VendorOptionsChatClient"/>'s established pattern — call sites construct a fresh
/// <see cref="ChatOptions"/> per call, so there is no shared-instance/retry hazard from
/// in-place mutation.
/// </summary>
internal sealed class ReasoningSuppressionChatClient : DelegatingChatClient
{
    private readonly ReasoningSuppressionDialect _dialect;

    public ReasoningSuppressionChatClient(IChatClient innerClient, ReasoningSuppressionDialect dialect)
        : base(innerClient)
    {
        _dialect = dialect;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ApplyDialect(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ApplyDialect(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void ApplyDialect(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties)
            return;
        if (!properties.TryGetValue(NetclawChatOptionKeys.SuppressReasoning, out var intent))
            return;

        // Always remove the intent key itself — a call site's internal signal must never
        // reach a provider SDK verbatim, regardless of whether the dialect below applies.
        properties.Remove(NetclawChatOptionKeys.SuppressReasoning);

        if (intent is not true)
            return;

        switch (_dialect)
        {
            case ReasoningSuppressionDialect.ChatTemplateKwargs:
                properties["chat_template_kwargs"] = new Dictionary<string, object?> { ["enable_thinking"] = false };
                break;

            case ReasoningSuppressionDialect.OllamaThink:
                properties["think"] = false;
                break;

            case ReasoningSuppressionDialect.None:
            default:
                break;
        }
    }
}
