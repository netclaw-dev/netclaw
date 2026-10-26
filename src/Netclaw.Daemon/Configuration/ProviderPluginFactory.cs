// -----------------------------------------------------------------------
// <copyright file="ProviderPluginFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private readonly IModelCapabilityResolver _capabilities;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public ProviderPluginFactory(
        Dictionary<string, ProviderEntry> providers,
        IEnumerable<ILlmProviderPlugin> plugins,
        IModelCapabilityResolver capabilities,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        _providers = providers;
        _capabilities = capabilities;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<ProviderPluginFactory>();
        _plugins = new Dictionary<string, ILlmProviderPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
            _plugins[plugin.TypeKey] = plugin;
    }

    /// <summary>
    /// Builds the client of every configured role once and drops it, so that what startup would
    /// throw is found before startup: an unknown provider type, a missing credential, an endpoint
    /// that is not a URL. Building a client makes no network call. Returns the error text, or null.
    /// </summary>
    public string? Validate(ModelSelection models)
    {
        foreach (var (role, model) in new[] { ("Main", models.Main), ("Fallback", models.Fallback), ("Compaction", models.Compaction) })
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Provider))
                continue;

            try
            {
                (Create(model) as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // UriFormatException is a FormatException: its text does not say which key to fix.
                var reason = ex is UriFormatException
                    ? $"{ex.Message.TrimEnd('.')}. Set Providers:{model.Provider}:Endpoint to a URL."
                    : ex.Message.TrimEnd();
                if (!reason.EndsWith('.'))
                    reason += ".";

                return $"Invalid model configuration: model '{role}' uses provider '{model.Provider}', which cannot be used: {reason} Fix the Providers section of netclaw.json.";
            }
        }

        return null;
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

        // Startup checks only the dialect: the model capability is resolved at runtime (D5). A
        // hand-edited level that the dialect cannot send fails here, never at the first call.
        if (ModelSettingsValidator.CheckDialect(model.Provider, provider, plugin, model) is { } settingsError)
            throw new InvalidOperationException(settingsError);
        var dialect = plugin.ResolveThinkingDialect(provider);

        var client = plugin.CreateChatClient(provider, model);
        var vendorOptions = plugin.CreateVendorOptionsSource(provider);
        IChatClient decorated = vendorOptions is null
            ? client
            : new VendorOptionsChatClient(client, vendorOptions);

        // The outermost wrapper of every client the daemon builds, for every role: it copies the
        // caller's options before any wrapper below changes them (#2681), applies the definition's
        // limit and level, and always removes the NetclawChatOptionKeys.SuppressReasoning intent key.
        var settings = new ModelRequestSettingsChatClient(
            decorated, model, dialect, plugin.IsSelfHosted, _capabilities, _timeProvider,
            _loggerFactory.CreateLogger<ModelRequestSettingsChatClient>());
        LogModelClient(model, dialect, plugin.IsSelfHosted, settings.ResolvedCapability);
        return settings;
    }

    private void LogModelClient(
        ModelReference model, ThinkingDialect dialect, bool selfHosted, ResolvedModelCapabilities? capability)
    {
        var window = model.ContextWindow?.ToString() ?? capability?.ContextWindowTokens?.ToString() ?? "unknown";
        var limit = model.MaxOutputTokens is { } configured
            ? $"{configured} (configured)"
            : selfHosted
                ? $"default {OutputLimitResolver.DefaultLimit} (clamped per call to the endpoint window; none while the window is unknown)"
                : $"default {OutputLimitResolver.DefaultLimit} (clamped per call to the model max and the window)";
        var thinking = model.Thinking is { } level ? $"{level} (configured)" : "unset";
        _logger.LogInformation(
            "Model client: provider={Provider} model={ModelId} maxOutputTokens={MaxOutputTokens} window={Window} modelMax={ModelMax} thinking={Thinking} dialect={Dialect} capability={Capability}",
            model.Provider, model.ModelId, limit, window, capability?.MaxOutputTokens?.ToString() ?? "unknown",
            thinking, dialect, capability?.Thinking.ToString() ?? "unresolved");
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
