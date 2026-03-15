namespace Netclaw.Actors.Memory;

public sealed record MemoryPolicyDecision(bool Allowed, string? Reason = null);

public sealed class MemoryPolicyEvaluator
{
    public MemoryPolicyDecision EvaluateWrite(
        string domain,
        string sensitivity,
        string recallMode,
        double confidence,
        bool isExplicitRequest)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return new MemoryPolicyDecision(false, "missing-domain");

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
}
