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
    /// The default-limit rule of the provider (design D4). A hosted API gets min(32000, its known model
    /// max) and no window clamp. A self-hosted server gets the window clamp only for a window that its
    /// own probe or the operator states, and no limit field when the window is unknown.
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
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var effective = await PrepareAsync(list, options, cancellationToken);
        return await base.GetResponseAsync(list, effective, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var effective = await PrepareAsync(list, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(list, effective, cancellationToken))
            yield return update;
    }

    private async Task<ChatOptions> PrepareAsync(
        IReadOnlyCollection<ChatMessage> messages, ChatOptions? callerOptions, CancellationToken cancellationToken)
    {
        var options = callerOptions?.Clone() ?? new ChatOptions();
        var capability = await LookupCapabilityAsync().WaitAsync(cancellationToken);

        // A self-hosted server: only its own probe or the operator knows its window, and a catalog
        // maximum describes another host. A hosted API: its known model max caps the default, and a
        // catalog window describes the same hosted model, so it clamps too (a hosted API can reject
        // input + max_tokens above the window).
        var window = _model.ContextWindow ?? (capability is { ContextWindowTokens: > 0 }
                                              && (!_selfHosted || !capability.ContextWindowFromCatalog)
            ? capability.ContextWindowTokens
            : null);
        var modelMax = _selfHosted ? null : capability?.MaxOutputTokens;
        var prompt = EstimatePromptTokens(messages, options);
        string limitText;
        if (_selfHosted && window is null && options.MaxOutputTokens is null && _model.MaxOutputTokens is null)
        {
            // A guess could exceed what the server has left for the reply, so the server decides.
            limitText = "none (window unknown)";
        }
        else
        {
            var limit = OutputLimitResolver.Resolve(options.MaxOutputTokens, _model.MaxOutputTokens, prompt, window, modelMax);
            options.MaxOutputTokens = limit.Value;
            limitText = $"{limit.Value} ({FormatSource(limit.Source)})";
            if (limit.FloorApplied)
            {
                _logger.LogWarning(
                    "Model {ModelId}: the estimated prompt ({PromptEstimate} tokens) leaves less than {Floor} output tokens in the context window ({Window}). The request sends the floor of {Floor}; the provider can reject it.",
                    _model.ModelId, prompt, OutputLimitResolver.FloorLimit, window, OutputLimitResolver.FloorLimit);
            }
        }

        var (level, levelSource) = ApplyThinking(options);

        _logger.LogDebug(
            "Model {ModelId} request: maxOutputTokens={MaxOutputTokens} promptEstimate={PromptEstimate} window={Window} thinking={Thinking} ({ThinkingSource}) dialect={Dialect}",
            _model.ModelId, limitText, prompt, window?.ToString() ?? "unknown",
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

    // The compaction rule (PromptTokenEstimate): characters / 4, with media at its base64 size. The
    // tool schemas travel in the same request, so they count too.
    private static int EstimatePromptTokens(IReadOnlyCollection<ChatMessage> messages, ChatOptions options)
    {
        var characters = (long)(options.Instructions?.Length ?? 0);
        foreach (var tool in options.Tools ?? [])
        {
            characters += tool.Name.Length + tool.Description.Length
                          + (tool is AIFunctionDeclaration function ? function.JsonSchema.GetRawText().Length : 0);
        }

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                characters += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                    FunctionCallContent call => call.Arguments?.Sum(pair => pair.Key.Length + (pair.Value?.ToString()?.Length ?? 0)) ?? 0,
                    FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                    DataContent data => PromptTokenEstimate.MediaCharacters(data.Data.Length),
                    _ => 0,
                };
            }
        }

        return PromptTokenEstimate.FromCharacters(characters);
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
/// <param name="FloorApplied">True when the clamp reached the 1024-token floor; the client logs a warning.</param>
internal sealed record OutputLimit(int Value, OutputLimitSource Source, bool FloorApplied);

/// <summary>The per-call output limit decision: call site, then the definition, then the clamped default.</summary>
internal static class OutputLimitResolver
{
    public const int DefaultLimit = ModelReference.DefaultMaxOutputTokens;
    public const int FloorLimit = 1_024;

    /// <summary>
    /// An explicit value is sent as is. The default is min(32000, model max, window - prompt - margin)
    /// with margin = max(1024, window / 20) and a floor of 1024. The margin covers the error of the
    /// character estimate. An unknown window or model max does not clamp.
    /// </summary>
    public static OutputLimit Resolve(
        int? callValue,
        int? configuredValue,
        int promptTokens,
        int? contextWindow,
        int? modelMaxOutput)
    {
        if (callValue is { } call)
            return new OutputLimit(call, OutputLimitSource.Call, false);
        if (configuredValue is { } configured)
            return new OutputLimit(configured, OutputLimitSource.Configured, false);

        var value = Math.Min(DefaultLimit, modelMaxOutput is > 0 ? modelMaxOutput.Value : DefaultLimit);
        var floorApplied = false;
        if (contextWindow is { } window)
        {
            var margin = Math.Max(FloorLimit, window / 20);
            var headroom = (long)window - promptTokens - margin;
            if (headroom < value)
            {
                floorApplied = headroom < FloorLimit && value > FloorLimit;
                value = (int)Math.Min(value, Math.Max(FloorLimit, headroom));
            }
        }

        return new OutputLimit(
            value, value == DefaultLimit ? OutputLimitSource.Default : OutputLimitSource.DefaultClamped, floorApplied);
    }
}
