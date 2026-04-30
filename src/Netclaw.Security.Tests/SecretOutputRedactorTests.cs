// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class SecretOutputRedactorTests
{
    // ── JSON secret key redaction ──

    [Theory]
    [InlineData("""{"apiKey": "sk-or-test-123", "safe": "ok"}""", "sk-or-test-123")]
    [InlineData("""{"client_secret": "super-secret-value-123"}""", "super-secret-value-123")]
    [InlineData("""{"signing_key": "hmac-sha256-key-abc"}""", "hmac-sha256-key-abc")]
    [InlineData("""{"credential": "some-cred-value"}""", "some-cred-value")]
    [InlineData("""{"access_token": "tok-abc-123"}""", "tok-abc-123")]
    [InlineData("""{"refresh_token": "rt-xyz-456"}""", "rt-xyz-456")]
    public void Redact_masks_json_secret_values(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_preserves_safe_json_keys()
    {
        const string input = """{"apiKey": "sk-or-test-123", "safe": "ok"}""";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("\"safe\": \"ok\"", redacted, StringComparison.Ordinal);
    }

    // ── Environment variable redaction ──

    [Fact]
    public void Redact_masks_env_style_secrets()
    {
        const string input = "API_KEY=secret123\nNORMAL=value";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("API_KEY=***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.Contains("NORMAL=value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", redacted, StringComparison.Ordinal);
    }

    // ── Authorization header redaction ──

    [Fact]
    public void Redact_masks_authorization_header()
    {
        const string input = "Authorization: Bearer abcdefghijklmnop";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Equal("Authorization: Bearer ***REDACTED***", redacted);
    }

    // ── Connection string redaction ──

    [Theory]
    [InlineData("Server=db.example.com;Database=mydb;User Id=admin;Password=s3cret!;", "s3cret!")]
    [InlineData("Server=localhost;Pwd=hunter2;Database=test;", "hunter2")]
    public void Redact_masks_connection_string_passwords(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***;", redacted, StringComparison.Ordinal);
    }

    // ── Provider-specific token redaction ──

    [Theory]
    [InlineData("Found key: AKIAIOSFODNN7EXAMPLE in config", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("sk-1234567890abcdef", "sk-1234567890abcdef")]
    [InlineData("xoxb-123456789-abcdefgh", "xoxb-123456789-abcdefgh")]
    [InlineData("ghp_ABCDEFghijklmnopqrstuvwx", "ghp_ABCDEFghijklmnopqrstuvwx")]
    public void Redact_masks_provider_tokens(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
    }

    // ── JWT redaction ──

    [Fact]
    public void Redact_masks_jwt_token()
    {
        const string input = "token: eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dGVzdHNpZ25hdHVyZQ";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
    }

    // ── Private key block redaction ──

    [Fact]
    public void Redact_masks_private_key_blocks()
    {
        const string input = """
            -----BEGIN OPENSSH PRIVATE KEY-----
            abcdefghijklmnop
            -----END OPENSSH PRIVATE KEY-----
            """;

        Assert.True(SecretOutputRedactor.ContainsSecretLikeContent(input));
        Assert.Equal("***REDACTED***", SecretOutputRedactor.Redact(input));
    }

    // ── False positive guards ──

    [Theory]
    [InlineData("""{"name": "Aaron", "email": "test@example.com"}""")]
    [InlineData("the word eyJust is not a JWT")]
    [InlineData("NORMAL=value")]
    [InlineData("ls -la /tmp")]
    public void Redact_does_not_touch_safe_content(string input)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Equal(input, redacted);
    }
}
