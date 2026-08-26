// -----------------------------------------------------------------------
// <copyright file="ILlmProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers;

/// <summary>
/// Extends <see cref="IProviderDescriptor"/> with SDK-specific methods
/// that only the daemon uses (client construction, vendor options).
/// </summary>
public interface ILlmProviderPlugin : IProviderDescriptor
{
    /// <summary>
    /// Create an <see cref="IChatClient"/> for the given provider entry and model.
    /// </summary>
    IChatClient CreateChatClient(ProviderEntry entry, ModelReference model);

    /// <summary>
    /// Create a vendor-specific options source, if any.
    /// Returns null for providers that don't need special options handling.
    /// </summary>
    IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry) => null;

    /// <summary>
    /// Declares which wire dialect this provider's serving stack understands for the
    /// <see cref="Netclaw.Configuration.NetclawChatOptionKeys.SuppressReasoning"/> intent key.
    /// <c>ReasoningSuppressionChatClient</c> (Netclaw.Daemon) reads this to decide what, if
    /// anything, to emit on the wire.
    ///
    /// Defaults to <see cref="ReasoningSuppressionDialect.None"/> — this is a genuine domain
    /// default, not a compatibility shim carved out to avoid touching every plugin's
    /// constructor: a provider plugin that hasn't declared a dialect has, by construction, no
    /// way of knowing a serving-stack-specific field name, so "strip the intent key and emit
    /// nothing" is the only behavior that is correct for every such plugin, known or future.
    /// Concretely it also matches the verified adapter behavior for the strict SDKs (official
    /// OpenAI, Anthropic): their ChatOptions→wire mapping never forwards
    /// <see cref="ChatOptions.AdditionalProperties"/> to the request body, so <c>None</c> costs
    /// those providers nothing — the intent key was already inert there, this just stops it
    /// from being sent as a stray unknown key to whichever SDK/provider a future plugin wraps.
    /// </summary>
    ReasoningSuppressionDialect SuppressionDialect => ReasoningSuppressionDialect.None;
}

/// <summary>
/// Wire dialects a provider's serving stack may understand for the
/// <see cref="Netclaw.Configuration.NetclawChatOptionKeys.SuppressReasoning"/> intent key.
/// </summary>
public enum ReasoningSuppressionDialect
{
    /// <summary>
    /// No known dialect for this provider — the intent key is stripped and nothing is emitted
    /// in its place. Correct for providers backed by strict SDKs (official OpenAI, Anthropic,
    /// GitHub Copilot, OpenRouter, Venice.ai) that reject or silently ignore unrecognized
    /// top-level request fields.
    /// </summary>
    None,

    /// <summary>
    /// vLLM/llama.cpp/SGLang-style OpenAI-compatible servers: emits top-level
    /// <c>chat_template_kwargs: { enable_thinking: false }</c>, which the pass-through
    /// self-hosted client forwards verbatim as a request body field.
    /// </summary>
    ChatTemplateKwargs,

    /// <summary>
    /// Ollama via OllamaSharp: emits top-level <c>think: false</c>, the field OllamaSharp's
    /// request mapping consumes.
    /// </summary>
    OllamaThink,

    /// <summary>
    /// DeepSeek's hosted API: emits top-level <c>thinking: { type: "disabled" }</c>.
    /// </summary>
    DeepSeekThinking,
}
