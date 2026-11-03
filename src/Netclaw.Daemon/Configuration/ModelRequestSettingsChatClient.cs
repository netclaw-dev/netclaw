// -----------------------------------------------------------------------
// <copyright file="ModelRequestSettingsChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// The outermost wrapper of each model client. It copies the caller's options, applies the effective
/// output limit and thinking level of the model definition, and maps them through the thinking dialect.
/// </summary>
/// <remarks>
/// The copy is the failover isolation of #2681: <c>RoutingChatClient</c> passes one options object to
/// each candidate, and every wrapper below this one changes only this candidate's copy. A call-site
/// value wins over the definition (D8). The suppression intent key is always removed, so it never
/// reaches a provider SDK.
/// </remarks>
internal sealed class ModelRequestSettingsChatClient : DelegatingChatClient
{
    private readonly ModelReference _model;
    private readonly ThinkingDialect _dialect;
    private readonly IModelCapabilityResolver _capabilities;
    private readonly bool _selfHosted;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lookupGate = new();
    private Task<ResolvedModelCapabilities?>? _lookup;
    private DateTimeOffset _missExpiresAt;
    private bool _missLogged;

    /// <summary>How long a lookup without a result stands before a call asks again.</summary>
    internal static readonly TimeSpan MissCacheDuration = TimeSpan.FromMinutes(10);

    /// <param name="selfHosted">
    /// The default-limit rule of the provider. A hosted API gets min(32000, its known model max, a
    /// quarter of its known window). A self-hosted server gets min(32000, a quarter of the window) only
    /// when its own probe or the operator states the window, and no limit field otherwise. Netclaw never
    /// sizes the limit from an estimate of the prompt: the server knows the real prompt size.
    /// </param>
    public ModelRequestSettingsChatClient(
        IChatClient innerClient,
        ModelReference model,
        ThinkingDialect dialect,
        bool selfHosted,
        IModelCapabilityResolver capabilities,
        TimeProvider timeProvider,
        ILogger logger)
        : base(innerClient)
    {
        _model = model;
        _dialect = dialect;
        _selfHosted = selfHosted;
        _capabilities = capabilities;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The model capability when a lookup has finished with a result, else null.</summary>
    internal ResolvedModelCapabilities? ResolvedCapability
        => _lookup is { IsCompletedSuccessfully: true } lookup ? lookup.Result : null;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = await PrepareAsync(options, cancellationToken);
        return await base.GetResponseAsync(messages, effective, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effective = await PrepareAsync(options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, effective, cancellationToken))
            yield return update;
    }

    private async Task<ChatOptions> PrepareAsync(ChatOptions? callerOptions, CancellationToken cancellationToken)
    {
        var options = callerOptions?.Clone() ?? new ChatOptions();
        var capability = await LookupCapabilityAsync().WaitAsync(cancellationToken);

        // A self-hosted server: only its own probe or the operator knows its window, and a catalog
        // maximum describes another host. A hosted API: its known model max caps the default, and a
        // catalog window describes the same hosted model.
        var window = _model.ContextWindow ?? (capability is { ContextWindowTokens: > 0 }
                                              && (!_selfHosted || !capability.ContextWindowFromCatalog)
            ? capability.ContextWindowTokens
            : null);
        var modelMax = _selfHosted ? null : capability?.MaxOutputTokens;
        string limitText;
        if (_selfHosted && window is null && options.MaxOutputTokens is null && _model.MaxOutputTokens is null)
        {
            // Without a known window, a default could exceed what the server has, so the server decides.
            limitText = "none (window unknown)";
        }
        else
        {
            var limit = OutputLimitResolver.Resolve(options.MaxOutputTokens, _model.MaxOutputTokens, modelMax, window);
            options.MaxOutputTokens = limit.Value;
            limitText = $"{limit.Value} ({FormatSource(limit.Source)})";
        }

        var (level, levelSource) = ApplyThinking(options);

        _logger.LogDebug(
            "Model {ModelId} request: maxOutputTokens={MaxOutputTokens} thinking={Thinking} ({ThinkingSource}) dialect={Dialect}",
            _model.ModelId, limitText,
            level, levelSource, _dialect);
        return options;
    }

    private (string Level, string Source) ApplyThinking(ChatOptions options)
    {
        var suppress = false;
        if (options.AdditionalProperties?.TryGetValue(NetclawChatOptionKeys.SuppressReasoning, out var intent) == true)
        {
            options.AdditionalProperties.Remove(NetclawChatOptionKeys.SuppressReasoning);
            suppress = intent is true;
        }

        if (suppress)
        {
            ThinkingDialectMap.ApplySuppression(_dialect, options);
            return (ThinkingDialectMap.SuppressionSendsOff(_dialect) ? nameof(ThinkingLevel.Off) : "unset", "suppressed");
        }

        if (options.Reasoning?.Effort is { } effort)
        {
            // A call-site effort goes through the dialect too, so a body-field provider gets its own field.
            var callLevel = ThinkingDialectMap.LevelOf(effort);
            options.Reasoning = null;
            ThinkingDialectMap.Apply(_dialect, options, callLevel);
            return (callLevel.ToString(), "call");
        }

        if (_model.Thinking is { } configured)
        {
            ThinkingDialectMap.Apply(_dialect, options, configured);
            return (configured.ToString(), "configured");
        }

        return ("unset", "unset");
    }

    // A result is kept for the client's lifetime. A lookup without a result (a network failure, or a
    // model that no source lists yet) stands for MissCacheDuration, so a later call looks the model
    // up again without a lookup on every call.
    private Task<ResolvedModelCapabilities?> LookupCapabilityAsync()
    {
        lock (_lookupGate)
        {
            var missed = _lookup is { IsCompleted: true } done && (!done.IsCompletedSuccessfully || done.Result is null);
            if (_lookup is null || (missed && _timeProvider.GetUtcNow() >= _missExpiresAt))
                _lookup = ResolveCapabilityAsync();
            return _lookup;
        }
    }

    private async Task<ResolvedModelCapabilities?> ResolveCapabilityAsync()
    {
        try
        {
            var capability = await _capabilities.ResolveAsync(_model, CancellationToken.None);
            if (capability is null)
            {
                RecordMiss();
                if (_missLogged)
                    return null;
                _missLogged = true;
            }

            _logger.LogInformation(
                "Model {ModelId} capability resolved: thinking={Thinking} modelMaxOutputTokens={ModelMax} window={Window}",
                _model.ModelId,
                capability?.Thinking.ToString() ?? "unknown",
                capability?.MaxOutputTokens?.ToString() ?? "unknown",
                capability?.ContextWindowTokens?.ToString() ?? "unknown");
            return capability;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed lookup is a normal runtime condition (network). The default limit then
            // uses only the configured window, and this line says that the inputs are unknown.
            RecordMiss();
            _logger.LogWarning(ex,
                "Model {ModelId} capability lookup failed; the model maximum output and the thinking capability are unknown.",
                _model.ModelId);
            return null;
        }
    }

    private void RecordMiss()
    {
        lock (_lookupGate)
            _missExpiresAt = _timeProvider.GetUtcNow() + MissCacheDuration;
    }

    internal static string FormatSource(OutputLimitSource source) => source switch
    {
        OutputLimitSource.Call => "call",
        OutputLimitSource.Configured => "configured",
        OutputLimitSource.Default => "default",
        OutputLimitSource.DefaultClamped => "default-clamped",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown output limit source."),
    };
}

/// <summary>The source of an effective output limit.</summary>
internal enum OutputLimitSource
{
    Call,
    Configured,
    Default,
    DefaultClamped,
}

/// <param name="Value">The limit that the request carries.</param>
/// <param name="Source">Where the value comes from.</param>
internal sealed record OutputLimit(int Value, OutputLimitSource Source);

/// <summary>The per-call output limit decision: call site, then the definition, then the capped default.</summary>
internal static class OutputLimitResolver
{
    public const int DefaultLimit = ModelReference.DefaultMaxOutputTokens;

    /// <summary>The share of a known context window that the default may use.</summary>
    public const int WindowDivisor = 4;

    /// <summary>
    /// An explicit value is sent as is. The default is min(32000, model max, window / 4); an unknown
    /// model max or window does not cap. The window cap is static: a hosted API rejects
    /// input + max_tokens above its window, so a 32000 default would fail every call on a 32k window.
    /// A quarter of the window matches compaction, which keeps the prompt below three quarters of it.
    /// The limit never depends on an estimate of the prompt.
    /// </summary>
    public static OutputLimit Resolve(int? callValue, int? configuredValue, int? modelMaxOutput, int? contextWindow)
    {
        if (callValue is { } call)
            return new OutputLimit(call, OutputLimitSource.Call);
        if (configuredValue is { } configured)
            return new OutputLimit(configured, OutputLimitSource.Configured);

        var value = Math.Min(DefaultLimit, modelMaxOutput is > 0 ? modelMaxOutput.Value : DefaultLimit);
        if (contextWindow is > 0)
            value = Math.Min(value, Math.Max(1, contextWindow.Value / WindowDivisor));
        return new OutputLimit(
            value, value == DefaultLimit ? OutputLimitSource.Default : OutputLimitSource.DefaultClamped);
    }
}
