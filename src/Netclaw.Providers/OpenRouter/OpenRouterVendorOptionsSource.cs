// -----------------------------------------------------------------------
// <copyright file="OpenRouterVendorOptionsSource.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.OpenRouter;

public sealed class OpenRouterVendorOptions : IVendorOptions
{
    public bool ExcludeReasoning { get; set; } = true;
}

/// <summary>
/// Vendor options source for OpenRouter that clears reasoning options.
/// This prevents OpenRouter from including reasoning/reasoning_details
/// fields in SSE chunks that the OpenAI .NET SDK cannot deserialize.
/// </summary>
public sealed class OpenRouterVendorOptionsSource : IVendorOptionsSource
{
    public void Apply(ChatOptions options)
    {
        // Remove reasoning to avoid OpenRouter SSE deserialization issues
        // in the OpenAI .NET SDK.
        options.AdditionalProperties?.Remove("reasoning");
    }
}
