// -----------------------------------------------------------------------
// <copyright file="NetclawChatOptionKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Provider-agnostic intent keys for <see cref="Microsoft.Extensions.AI.ChatOptions.AdditionalProperties"/>.
///
/// Call sites (actors, pipelines) that need a provider-serving-stack behavior express the
/// <em>intent</em> using one of these keys — never a raw wire field name. The provider layer
/// (<c>ReasoningSuppressionChatClient</c> in Netclaw.Daemon, wrapping every
/// <see cref="Microsoft.Extensions.AI.IChatClient"/> the daemon constructs) reads the intent key,
/// removes it, and maps it to whichever dialect the active provider plugin declares understanding
/// of (<c>ILlmProviderPlugin.SuppressionDialect</c>) — or strips it with no replacement for
/// providers with no equivalent. This keeps call sites free of knowledge about which serving
/// stack (vLLM, llama.cpp, Ollama, official OpenAI/Anthropic SDKs, ...) sits on the other end of
/// the wire for the model currently selected.
/// </summary>
public static class NetclawChatOptionKeys
{
    /// <summary>
    /// Intent: suppress the model's extended-thinking/reasoning output for this call. Set to
    /// <see langword="true"/> to request suppression. The provider layer maps a <c>true</c>
    /// value to the dialect-specific field for providers that support it (e.g. vLLM/llama.cpp's
    /// <c>chat_template_kwargs.enable_thinking</c>, Ollama's <c>think</c>) and drops the intent
    /// key entirely for providers with no equivalent, so it never reaches a strict SDK
    /// (official OpenAI, Anthropic) as an unrecognized top-level request field.
    /// </summary>
    public const string SuppressReasoning = "netclaw:suppress-reasoning";
}
