// -----------------------------------------------------------------------
// <copyright file="ToolCallArgumentErrors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Wire-level markers for tool-call argument failures detected at the provider
/// boundary. When a model emits a tool call whose arguments JSON cannot be
/// deserialized, the provider attaches <see cref="ArgsParseErrorKey"/> instead
/// of dispatching null arguments; the session pipeline detects the sentinel
/// before meta extraction and rejects the call id with a model-facing error
/// (tool-arg-validation spec). The raw payload needed for a useful error is
/// only available at the provider, which is why the failure travels with the
/// call rather than being reconstructed downstream.
/// </summary>
public static class ToolCallArgumentErrors
{
    /// <summary>
    /// Sentinel argument key carrying the parse-failure detail. Never collides
    /// with validation: the pipeline consumes it before the unknown-key gate,
    /// and it appears in no tool schema, so a leak is rejected loudly anyway.
    /// </summary>
    public const string ArgsParseErrorKey = "__netclaw_args_parse_error";
}
