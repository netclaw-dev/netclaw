using Akka.Event;
using Netclaw.Security;

namespace Netclaw.Channels;

public enum ClassificationOutcome { Allow, Block, DetectorUnavailable }

public readonly record struct Classification(ClassificationOutcome Outcome, string? Reason);

public static class PromptClassifier
{
    public static async Task<Classification> ClassifyAsync(
        IPromptInjectionDetector detector,
        string? text,
        string sourceContext,
        ILoggingAdapter log,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Classification(ClassificationOutcome.Allow, null);

        PromptInjectionResult detection;
        try
        {
            detection = await detector.DetectAsync(text, sourceContext, cancellationToken);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Prompt injection detector failed for source={Source}", sourceContext);
            return new Classification(ClassificationOutcome.DetectorUnavailable, ex.Message);
        }

        if (detection.Risk != PromptInjectionRisk.High)
            return new Classification(ClassificationOutcome.Allow, null);

        var reason = string.IsNullOrWhiteSpace(detection.Message)
            ? "High-risk prompt injection pattern detected"
            : detection.Message;
        return new Classification(ClassificationOutcome.Block, reason);
    }
}
