// -----------------------------------------------------------------------
// <copyright file="MemoryPolicyEvaluator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

public sealed record MemoryPolicyDecision(bool Allowed, string? Reason = null);

public sealed class MemoryPolicyEvaluator
{
    public MemoryPolicyDecision EvaluateWrite(
        string sensitivity,
        string recallMode,
        double confidence,
        bool isExplicitRequest,
        string? audience = null)
    {
        if (!string.IsNullOrWhiteSpace(audience)
            && !SecurityPolicyDefaults.TryParseAudience(audience, out _))
            return new MemoryPolicyDecision(false, "invalid-audience");

        if (!MemoryDomainEnumExtensions.TryFromWireValue(recallMode, out MemoryRecallMode parsedMode)
            || parsedMode == MemoryRecallMode.Searchable)
            return new MemoryPolicyDecision(false, "invalid-recall-mode");

        if (MemoryDomainEnumExtensions.TryFromWireValue(sensitivity, out MemorySensitivity parsedSensitivity)
            && parsedSensitivity == MemorySensitivity.Secret
            && parsedMode == MemoryRecallMode.Auto)
            return new MemoryPolicyDecision(false, "secret-cannot-be-auto");

        if (!isExplicitRequest && confidence < 0.55)
            return new MemoryPolicyDecision(false, "low-confidence");

        return new MemoryPolicyDecision(true);
    }

    public static string ResolveAudience(string? audience, TrustAudience fallback)
        => SecurityPolicyDefaults.TryParseAudience(audience, out var parsed)
            ? parsed.ToWireValue()
            : fallback.ToWireValue();

    public static IReadOnlyList<string> AllowedAudienceWireValues(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => [TrustAudience.Public.ToWireValue()],
        TrustAudience.Team => [TrustAudience.Public.ToWireValue(), TrustAudience.Team.ToWireValue()],
        TrustAudience.Personal => [TrustAudience.Public.ToWireValue(), TrustAudience.Team.ToWireValue(), TrustAudience.Personal.ToWireValue()],
        _ => [TrustAudience.Public.ToWireValue()]
    };
}
