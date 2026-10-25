// -----------------------------------------------------------------------
// <copyright file="AnthropicCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers.Anthropic;

/// <summary>
/// Reads the thinking capability and the maximum output of a model from the Anthropic Models API
/// (<c>GET /v1/models/{id}</c>).
/// </summary>
public sealed class AnthropicCapabilityResolver
{
    /// <summary>Parses one Models API model object.</summary>
    internal static ResolvedModelCapabilities? ParseModel(string json, string modelId) => null;
}
