// -----------------------------------------------------------------------
// <copyright file="WebhookRequestVerifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookRequestVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-02T18:30:00Z");
    private readonly WebhookRequestVerifier _sut = new(new FakeTimeProvider(Now));

    [Fact]
    public void Hmac_accepts_valid_signature_and_reads_headers()
    {
        var route = CreateRoute(new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("super-secret"),
                SignatureHeaderName = "X-Hub-Signature-256",
                SignaturePrefix = "sha256=",
                EventHeaderName = "X-GitHub-Event",
                DeliveryIdHeaderName = "X-GitHub-Delivery"
            }
        });

        var body = Encoding.UTF8.GetBytes("{\"repository\":{\"full_name\":\"petabridge/netclaw\"}}");
        var headers = new HeaderDictionary
        {
            ["X-Hub-Signature-256"] = CreateGitHubSignature("super-secret", body),
            ["X-GitHub-Event"] = "issues",
            ["X-GitHub-Delivery"] = "delivery-123"
        };

        var result = _sut.Verify(route, headers, body);

        Assert.True(result.IsAccepted);
        Assert.Equal("issues", result.EventType);
        Assert.Equal("delivery-123", result.DeliveryId);
    }

    [Fact]
    public void Hmac_rejects_invalid_signature()
    {
        var route = CreateRoute(new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("super-secret"),
                SignatureHeaderName = "X-Hub-Signature-256",
                SignaturePrefix = "sha256="
            }
        });

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["X-Hub-Signature-256"] = "sha256=deadbeef"
        }, Encoding.UTF8.GetBytes("{}"));

        Assert.False(result.IsAccepted);
        Assert.Equal("invalid_signature", result.RejectionReason);
    }

    [Fact]
    public void HeaderSecret_accepts_matching_secret_and_custom_headers()
    {
        var route = CreateRoute(new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.HeaderSecret,
                Secret = new SensitiveString("header-secret"),
                SecretHeaderName = "X-Internal-Secret",
                EventHeaderName = "X-Internal-Event",
                DeliveryIdHeaderName = "X-Internal-Delivery"
            }
        });

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["X-Internal-Secret"] = "header-secret",
            ["X-Internal-Event"] = "alert.created",
            ["X-Internal-Delivery"] = "evt-1"
        }, Encoding.UTF8.GetBytes("{}"));

        Assert.True(result.IsAccepted);
        Assert.Equal("alert.created", result.EventType);
        Assert.Equal("evt-1", result.DeliveryId);
    }

    [Fact]
    public void HeaderSecret_rejects_missing_secret_header()
    {
        var route = CreateRoute(new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.HeaderSecret,
                Secret = new SensitiveString("header-secret"),
                SecretHeaderName = "X-Internal-Secret"
            }
        });

        var result = _sut.Verify(route, new HeaderDictionary(), Encoding.UTF8.GetBytes("{}"));

        Assert.False(result.IsAccepted);
        Assert.Equal("missing_secret_header", result.RejectionReason);
    }

    [Fact]
    public void TimestampedHmac_accepts_exact_raw_body_and_default_fields()
    {
        var body = Encoding.UTF8.GetBytes("{\n  \"amount\": 1200, \"currency\": \"usd\"\n}");
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var route = CreateTimestampedRoute();
        var signature = CreateTimestampedSignature("super-secret", timestamp, ".", body);

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={timestamp},v1={signature}",
            ["X-Webhook-Event"] = "payment.succeeded",
            ["X-Webhook-Delivery"] = "evt-123"
        }, body);

        Assert.True(result.IsAccepted);
        Assert.Equal("payment.succeeded", result.EventType);
        Assert.Equal("evt-123", result.DeliveryId);
    }

    [Fact]
    public void TimestampedHmac_accepts_any_matching_rotation_signature()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"rotated\"}");
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var route = CreateTimestampedRoute();
        var signature = CreateTimestampedSignature("super-secret", timestamp, ".", body);

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={timestamp},v1={new string('0', 64)},v1={signature.ToUpperInvariant()}"
        }, body);

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void TimestampedHmac_uses_custom_fields_separator_and_tolerance_boundary()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"custom\"}");
        var timestamp = Now.AddSeconds(-30).ToUnixTimeSeconds().ToString();
        var route = CreateTimestampedRoute(new WebhookVerificationConfig
        {
            Kind = WebhookVerifierKind.HmacTimestamped,
            Secret = new SensitiveString("super-secret"),
            SignatureHeaderName = "X-Custom-Signature",
            TimestampField = "time",
            SignatureField = "sig",
            SignedPayloadSeparator = "::",
            ToleranceSeconds = 30
        });
        var signature = CreateTimestampedSignature("super-secret", timestamp, "::", body);

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["X-Custom-Signature"] = $"ignored=value,time={timestamp},sig={signature}"
        }, body);

        Assert.True(result.IsAccepted);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void TimestampedHmac_rejects_timestamp_outside_tolerance(int offsetSeconds)
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var timestamp = Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString();
        var route = CreateTimestampedRoute();
        var signature = CreateTimestampedSignature("super-secret", timestamp, ".", body);

        var result = _sut.Verify(route, new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={timestamp},v1={signature}"
        }, body);

        Assert.False(result.IsAccepted);
        Assert.Equal("timestamp_out_of_tolerance", result.RejectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t=123")]
    [InlineData("v1=abcd")]
    [InlineData("t=not-a-number,v1=abcd")]
    [InlineData("t=123,t=123,v1=abcd")]
    [InlineData("t=123,v1=")]
    [InlineData("t=123,broken,v1=abcd")]
    public void TimestampedHmac_rejects_missing_or_malformed_header(string header)
    {
        var result = _sut.Verify(CreateTimestampedRoute(), new HeaderDictionary
        {
            ["Stripe-Signature"] = header
        }, Encoding.UTF8.GetBytes("{}"));

        Assert.False(result.IsAccepted);
        Assert.Contains(result.RejectionReason, new[] { "missing_signature", "invalid_signature_header" });
    }

    [Fact]
    public void TimestampedHmac_rejects_invalid_hex_signature_without_throwing()
    {
        var timestamp = Now.ToUnixTimeSeconds();

        var result = _sut.Verify(CreateTimestampedRoute(), new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={timestamp},v1=not-hex"
        }, Encoding.UTF8.GetBytes("{}"));

        Assert.False(result.IsAccepted);
        Assert.Equal("invalid_signature", result.RejectionReason);
    }

    [Fact]
    public void TimestampedHmac_rejects_unsupported_hmac_algorithm()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{}");
        var route = CreateTimestampedRoute(new WebhookVerificationConfig
        {
            Kind = WebhookVerifierKind.HmacTimestamped,
            HmacAlgorithm = (WebhookHmacAlgorithm)99,
            Secret = new SensitiveString("super-secret"),
            SignatureHeaderName = "Stripe-Signature"
        });

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Verify(
            route,
            new HeaderDictionary
            {
                ["Stripe-Signature"] = $"t={timestamp},v1={new string('0', 64)}"
            },
            body));

        Assert.Equal("algorithm", exception.ParamName);
    }

    private static RegisteredWebhookRoute CreateRoute(WebhookRouteConfig config)
        => new(
            "github-issues",
            "/tmp/github-issues.json",
            DateTimeOffset.Parse("2026-04-02T18:30:00Z"),
            config);

    private static RegisteredWebhookRoute CreateTimestampedRoute(WebhookVerificationConfig? verification = null)
        => CreateRoute(new WebhookRouteConfig
        {
            Prompt = "process event",
            Verification = verification ?? new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.HmacTimestamped,
                Secret = new SensitiveString("super-secret"),
                SignatureHeaderName = "Stripe-Signature"
            }
        });

    private static string CreateGitHubSignature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"sha256={Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant()}";
    }

    private static string CreateTimestampedSignature(
        string secret,
        string timestamp,
        string separator,
        byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + separator);
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }
}
