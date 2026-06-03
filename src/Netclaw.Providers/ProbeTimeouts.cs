// -----------------------------------------------------------------------
// <copyright file="ProbeTimeouts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Providers;

/// <summary>
/// The single source of truth for provider-probe timeout budgets, shared across the
/// providers layer (per-request deadlines) and the interactive CLI/TUI callers
/// (whole-operation wall-clock). Centralized deliberately: these used to be
/// hand-copied constants in each view model, and they drifted — raising one without
/// the others let a stale outer deadline silently truncate a longer inner one (#1292).
/// </summary>
public static class ProbeTimeouts
{
    /// <summary>
    /// Per-request deadline for hosted/cloud providers, which answer /models fast.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-request deadline for self-hosted endpoints (llama.cpp, vLLM, Ollama). A cold
    /// server loading a model — or one saturated with inference requests — can
    /// legitimately take far longer than a hosted API to answer /models, so 10s is too
    /// tight and produces false "timed out" failures against servers that are fine,
    /// just busy.
    /// </summary>
    public static readonly TimeSpan SelfHosted = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wall-clock bound on a full interactive probe, applied by the wizard/TUI callers.
    /// Unlike the per-request deadlines above (which the descriptor applies to the
    /// /models HTTP call only), this also covers work the descriptor does <em>before</em>
    /// that call — notably OAuth token exchange — which would otherwise be bounded only
    /// by the HttpClient default (~100s).
    /// <para>
    /// MUST stay strictly greater than <see cref="SelfHosted"/>: if it ever drops below,
    /// it preempts a legitimate slow self-hosted probe and re-creates the truncation bug.
    /// </para>
    /// </summary>
    public static readonly TimeSpan InteractiveWallClock = TimeSpan.FromSeconds(45);
}
