namespace Netclaw.Actors.Memory;

public sealed record MemoryPolicyDecision(bool Allowed, string? Reason = null);

public sealed class MemoryPolicyEvaluator
{
    private static readonly HashSet<string> AllowedRecallModes =
    [
        "auto",
        "manual",
        "never"
    ];

    public MemoryPolicyDecision EvaluateWrite(
        string domain,
        string sensitivity,
        string recallMode,
        double confidence,
        bool isExplicitRequest)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return new MemoryPolicyDecision(false, "missing-domain");

        if (!AllowedRecallModes.Contains(recallMode))
            return new MemoryPolicyDecision(false, "invalid-recall-mode");

        if (string.Equals(sensitivity, "secret", StringComparison.OrdinalIgnoreCase)
            && string.Equals(recallMode, "auto", StringComparison.OrdinalIgnoreCase))
            return new MemoryPolicyDecision(false, "secret-cannot-be-auto");

        if (!isExplicitRequest && confidence < 0.55)
            return new MemoryPolicyDecision(false, "low-confidence");

        return new MemoryPolicyDecision(true);
    }
}
