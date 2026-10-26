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
    private readonly ILogger _logger;
    private readonly Lazy<Task<ResolvedModelCapabilities?>> _capability;

    public ModelRequestSettingsChatClient(
        IChatClient innerClient,
        ModelReference model,
        ThinkingDialect dialect,
        IModelCapabilityResolver capabilities,
        ILogger logger)
        : base(innerClient)
    {
        _model = model;
        _dialect = dialect;
        _capabilities = capabilities;
        _logger = logger;
        _capability = new Lazy<Task<ResolvedModelCapabilities?>>(ResolveCapabilityAsync);
    }

    /// <summary>The model capability when its first-use lookup has finished, else null.</summary>
    internal ResolvedModelCapabilities? ResolvedCapability
        => _capability.IsValueCreated && _capability.Value.IsCompletedSuccessfully ? _capability.Value.Result : null;

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
        var capability = await _capability.Value.WaitAsync(cancellationToken);

        var window = _model.ContextWindow ?? (capability?.ContextWindowTokens is > 0 ? capability.ContextWindowTokens : null);
        var prompt = EstimatePromptTokens(messages, options);
        var limit = OutputLimitResolver.Resolve(
            options.MaxOutputTokens, _model.MaxOutputTokens, prompt, window, capability?.MaxOutputTokens);
        options.MaxOutputTokens = limit.Value;
        if (limit.FloorApplied)
        {
            _logger.LogWarning(
                "Model {ModelId}: the estimated prompt ({PromptEstimate} tokens) leaves less than {Floor} output tokens in the context window ({Window}). The request sends the floor of {Floor}; the provider can reject it.",
                _model.ModelId, prompt, OutputLimitResolver.FloorLimit, window, OutputLimitResolver.FloorLimit);
        }

        var (level, levelSource) = ApplyThinking(options);

        _logger.LogDebug(
            "Model {ModelId} request: maxOutputTokens={MaxOutputTokens} ({LimitSource}) promptEstimate={PromptEstimate} window={Window} thinking={Thinking} ({ThinkingSource}) dialect={Dialect}",
            _model.ModelId, limit.Value, FormatSource(limit.Source), prompt, window?.ToString() ?? "unknown",
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

    private async Task<ResolvedModelCapabilities?> ResolveCapabilityAsync()
    {
        try
        {
            var capability = await _capabilities.ResolveAsync(_model.ModelId, CancellationToken.None);
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
            _logger.LogWarning(ex,
                "Model {ModelId} capability lookup failed; the model maximum output and the thinking capability are unknown.",
                _model.ModelId);
            return null;
        }
    }

    // The compaction rule (PromptTokenEstimate): characters / 4, with media at its base64 size.
    private static int EstimatePromptTokens(IReadOnlyCollection<ChatMessage> messages, ChatOptions options)
    {
        var characters = (long)(options.Instructions?.Length ?? 0);
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
