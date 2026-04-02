using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookRequestVerifierTests
{
    private readonly WebhookRequestVerifier _sut = new();

    [Fact]
    public void GitHubHmac_accepts_valid_signature_and_reads_headers()
    {
        var route = new RegisteredWebhookRoute("github-issues", new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.GitHubHmacSha256,
                Secret = new SensitiveString("super-secret")
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
    public void GitHubHmac_rejects_invalid_signature()
    {
        var route = new RegisteredWebhookRoute("github-issues", new WebhookRouteConfig
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.GitHubHmacSha256,
                Secret = new SensitiveString("super-secret")
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
        var route = new RegisteredWebhookRoute("internal-alerts", new WebhookRouteConfig
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

    private static string CreateGitHubSignature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"sha256={Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant()}";
    }
}
