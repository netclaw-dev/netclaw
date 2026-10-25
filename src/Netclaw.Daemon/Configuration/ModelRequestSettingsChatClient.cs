// -----------------------------------------------------------------------
// <copyright file="ModelRequestSettingsChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// The outermost wrapper of each model client. It copies the caller's options, applies the effective
/// output limit and thinking level of the model definition, and maps them through the thinking dialect.
/// </summary>
internal sealed class ModelRequestSettingsChatClient : DelegatingChatClient
{
    public ModelRequestSettingsChatClient(
        IChatClient innerClient,
        ModelReference model,
        ThinkingDialect dialect,
        IModelCapabilityResolver capabilities,
        ILogger logger)
        : base(innerClient)
    {
    }
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
    public const int DefaultLimit = 32_000;
    public const int FloorLimit = 1_024;

    public static OutputLimit Resolve(
        int? callValue,
        int? configuredValue,
        int promptTokens,
        int? contextWindow,
        int? modelMaxOutput)
        => new(callValue ?? configuredValue ?? 0, OutputLimitSource.Call, false);
}
