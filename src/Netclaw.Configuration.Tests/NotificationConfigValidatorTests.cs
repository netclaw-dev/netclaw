using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NotificationConfigValidatorTests
{
    [Fact]
    public void AcceptsValidHttpsTarget()
    {
        var result = NotificationConfigValidator.Validate(new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = new SensitiveString("https://alerts.example/hooks/netclaw") }]
        });

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("http://localhost:8080/hooks/netclaw")]
    [InlineData("http://127.0.0.1:8080/hooks/netclaw")]
    [InlineData("http://[::1]:8080/hooks/netclaw")]
    public void AcceptsLoopbackHttpTargets(string url)
    {
        var result = NotificationConfigValidator.Validate(new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = new SensitiveString(url) }]
        });

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RejectsNonLoopbackHttpTarget()
    {
        var result = NotificationConfigValidator.Validate(new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = new SensitiveString("http://alerts.internal.example/hooks/netclaw") }]
        });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("Notifications.Webhooks[0].Url", issue.FieldPath);
        Assert.Contains("plaintext HTTP", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsFragmentBearingTarget()
    {
        var result = NotificationConfigValidator.Validate(new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = new SensitiveString("https://alerts.example/hooks/netclaw#fragment") }]
        });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("fragment", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, 2, 10, "Notifications.DeduplicationWindowSeconds")]
    [InlineData(86401, 2, 10, "Notifications.DeduplicationWindowSeconds")]
    [InlineData(300, -1, 10, "Notifications.MaxRetries")]
    [InlineData(300, 6, 10, "Notifications.MaxRetries")]
    [InlineData(300, 2, 0, "Notifications.TimeoutSeconds")]
    [InlineData(300, 2, 61, "Notifications.TimeoutSeconds")]
    public void RejectsOutOfRangeDeliverySettings(int dedupSeconds, int maxRetries, int timeoutSeconds, string expectedField)
    {
        var result = NotificationConfigValidator.Validate(new NotificationsConfig
        {
            DeduplicationWindowSeconds = dedupSeconds,
            MaxRetries = maxRetries,
            TimeoutSeconds = timeoutSeconds,
            Webhooks = [new WebhookTarget { Url = new SensitiveString("https://alerts.example/hooks/netclaw") }]
        });

        Assert.Contains(result.Issues, issue => issue.FieldPath == expectedField);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("Api-Key")]
    [InlineData("api_key")]
    public void DetectsAuthLikeHeaderNames(string headerName)
    {
        Assert.True(NotificationConfigValidator.IsAuthLikeHeaderName(headerName));
    }

    [Fact]
    public void RedactsHeadersForDiagnostics()
    {
        var redacted = NotificationConfigValidator.FormatRedactedHeaders(new Dictionary<string, SensitiveString>
        {
            ["Authorization"] = new SensitiveString("Bearer top-secret"),
            ["X-Custom"] = new SensitiveString("visible-value")
        });

        Assert.Contains("Authorization=<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("X-Custom=<redacted>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("visible-value", redacted, StringComparison.Ordinal);
    }
}
