namespace Netclaw.Configuration;

public sealed class NotificationConfigValidationResult(IReadOnlyList<NotificationConfigValidationIssue> issues)
{
    public static NotificationConfigValidationResult Valid { get; } = new([]);

    public IReadOnlyList<NotificationConfigValidationIssue> Issues { get; } = issues;

    public bool IsValid => Issues.Count == 0;
}
