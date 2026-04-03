using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookRequestVerifier
{
    public WebhookVerificationResult Verify(
        RegisteredWebhookRoute route,
        IHeaderDictionary headers,
        byte[] bodyBytes)
    {
        return route.Config.Verification.Kind switch
        {
            WebhookVerifierKind.Hmac => VerifyHmac(route, headers, bodyBytes),
            WebhookVerifierKind.HeaderSecret => VerifyHeaderSecret(route, headers),
            _ => throw new ArgumentOutOfRangeException(nameof(route.Config.Verification.Kind), route.Config.Verification.Kind, null)
        };
    }

    private static WebhookVerificationResult VerifyHmac(
        RegisteredWebhookRoute route,
        IHeaderDictionary headers,
        byte[] bodyBytes)
    {
        var signature = RegisteredWebhookRoute.GetHeaderValue(headers, route.SignatureHeaderName);
        if (string.IsNullOrWhiteSpace(signature))
            return WebhookVerificationResult.Reject("missing_signature");

        var secret = route.Config.Verification.Secret!.Value;
        var expected = route.Config.Verification.HmacAlgorithm switch
        {
            WebhookHmacAlgorithm.Sha256 => ComputeExpectedSha256(secret, bodyBytes, route.SignaturePrefix),
            _ => throw new ArgumentOutOfRangeException(nameof(route.Config.Verification.HmacAlgorithm), route.Config.Verification.HmacAlgorithm, null)
        };

        if (!FixedTimeEquals(signature, expected))
            return WebhookVerificationResult.Reject("invalid_signature");

        return WebhookVerificationResult.Accept(
            RegisteredWebhookRoute.GetHeaderValue(headers, route.EventHeaderName),
            RegisteredWebhookRoute.GetHeaderValue(headers, route.DeliveryIdHeaderName));
    }

    private static WebhookVerificationResult VerifyHeaderSecret(
        RegisteredWebhookRoute route,
        IHeaderDictionary headers)
    {
        var provided = RegisteredWebhookRoute.GetHeaderValue(headers, route.SecretHeaderName);
        if (string.IsNullOrWhiteSpace(provided))
            return WebhookVerificationResult.Reject("missing_secret_header");

        if (!FixedTimeEquals(provided, route.Config.Verification.Secret!.Value))
            return WebhookVerificationResult.Reject("invalid_secret_header");

        return WebhookVerificationResult.Accept(
            RegisteredWebhookRoute.GetHeaderValue(headers, route.EventHeaderName),
            RegisteredWebhookRoute.GetHeaderValue(headers, route.DeliveryIdHeaderName));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string ComputeExpectedSha256(string secret, byte[] bodyBytes, string prefix)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant();
        return string.Concat(prefix, hash);
    }
}

public sealed record WebhookVerificationResult(bool IsAccepted, string? RejectionReason, string? EventType, string? DeliveryId)
{
    public static WebhookVerificationResult Accept(string? eventType, string? deliveryId)
        => new(true, null, eventType, deliveryId);

    public static WebhookVerificationResult Reject(string reason)
        => new(false, reason, null, null);
}
