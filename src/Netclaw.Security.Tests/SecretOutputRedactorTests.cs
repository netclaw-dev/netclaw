using Xunit;

namespace Netclaw.Security.Tests;

public sealed class SecretOutputRedactorTests
{
    [Fact]
    public void Redact_masks_json_secret_values()
    {
        const string input = "{\"apiKey\": \"sk-or-test-123\", \"safe\": \"ok\"}";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("\"apiKey\": \"***REDACTED***\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"safe\": \"ok\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-test-123", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_masks_env_style_secrets()
    {
        const string input = "API_KEY=secret123\nNORMAL=value";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("API_KEY=***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.Contains("NORMAL=value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_masks_authorization_header()
    {
        const string input = "Authorization: Bearer abcdefghijklmnop";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Equal("Authorization: Bearer ***REDACTED***", redacted);
    }
}
