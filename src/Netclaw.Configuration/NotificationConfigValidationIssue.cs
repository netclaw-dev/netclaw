namespace Netclaw.Configuration;

public sealed record NotificationConfigValidationIssue(
    string FieldPath,
    string Message,
    string Remediation);
