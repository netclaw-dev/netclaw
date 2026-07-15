// -----------------------------------------------------------------------
// <copyright file="WebhookRequestVerifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookRequestVerifier(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public WebhookVerificationResult Verify(
        RegisteredWebhookRoute route,
        IHeaderDictionary headers,
        byte[] bodyBytes)
    {
        return route.Config.Verification.Kind switch
        {
            WebhookVerifierKind.Hmac => VerifyHmac(route, headers, bodyBytes),
            WebhookVerifierKind.HmacTimestamped => VerifyTimestampedHmac(route, headers, bodyBytes),
            WebhookVerifierKind.HeaderSecret => VerifyHeaderSecret(route, headers),
            _ => throw new ArgumentOutOfRangeException(nameof(route.Config.Verification.Kind), route.Config.Verification.Kind, null)
        };
    }

    private WebhookVerificationResult VerifyTimestampedHmac(
        RegisteredWebhookRoute route,
        IHeaderDictionary headers,
        byte[] bodyBytes)
    {
        var signatureHeader = RegisteredWebhookRoute.GetHeaderValue(headers, route.SignatureHeaderName);
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return WebhookVerificationResult.Reject("missing_signature");

        if (!TryParseTimestampedHeader(
                signatureHeader,
                route.TimestampField,
                route.TimestampSignatureField,
                out var timestampText,
                out var signatures))
        {
            return WebhookVerificationResult.Reject("invalid_signature_header");
        }

        if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            return WebhookVerificationResult.Reject("invalid_signature_header");

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return WebhookVerificationResult.Reject("invalid_signature_header");
        }

        if ((_timeProvider.GetUtcNow() - signedAt).Duration()
            > TimeSpan.FromSeconds(route.TimestampToleranceSeconds))
        {
            return WebhookVerificationResult.Reject("timestamp_out_of_tolerance");
        }

        var secret = route.Config.Verification.Secret!.Value;
        var signedPayload = CreateTimestampedPayload(
            timestampText,
            route.SignedPayloadSeparator,
            bodyBytes);
        var expected = ComputeHmac(
            route.Config.Verification.HmacAlgorithm,
            secret,
            signedPayload);
        if (!signatures.Any(signature => IsMatchingHexSignature(expected, signature)))
            return WebhookVerificationResult.Reject("invalid_signature");

        return WebhookVerificationResult.Accept(
            RegisteredWebhookRoute.GetHeaderValue(headers, route.EventHeaderName),
            RegisteredWebhookRoute.GetHeaderValue(headers, route.DeliveryIdHeaderName));
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
        var hash = ComputeHmac(route.Config.Verification.HmacAlgorithm, secret, bodyBytes);
        var expected = string.Concat(
            route.SignaturePrefix,
            Convert.ToHexString(hash).ToLowerInvariant());

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

    private static byte[] ComputeHmac(
        WebhookHmacAlgorithm algorithm,
        string secret,
        byte[] payload)
    {
        using HMAC hmac = algorithm switch
        {
            WebhookHmacAlgorithm.Sha256 => new HMACSHA256(Encoding.UTF8.GetBytes(secret)),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
        };
        return hmac.ComputeHash(payload);
    }

    private static byte[] CreateTimestampedPayload(
        string timestamp,
        string separator,
        byte[] bodyBytes)
    {
        var prefixBytes = Encoding.UTF8.GetBytes(timestamp + separator);
        var signedPayload = new byte[prefixBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, signedPayload, 0, prefixBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, signedPayload, prefixBytes.Length, bodyBytes.Length);

        return signedPayload;
    }

    private static bool IsMatchingHexSignature(byte[] expected, string providedHex)
    {
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return provided.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static bool TryParseTimestampedHeader(
        string header,
        string timestampField,
        string signatureField,
        out string timestamp,
        out List<string> signatures)
    {
        timestamp = string.Empty;
        signatures = [];

        foreach (var component in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var separatorIndex = component.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex == component.Length - 1)
                return false;

            var key = component[..separatorIndex].Trim();
            var value = component[(separatorIndex + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
                return false;

            if (string.Equals(key, timestampField, StringComparison.Ordinal))
            {
                if (timestamp.Length > 0)
                    return false;

                timestamp = value;
            }
            else if (string.Equals(key, signatureField, StringComparison.Ordinal))
            {
                signatures.Add(value);
            }
        }

        return timestamp.Length > 0 && signatures.Count > 0;
    }
}

public sealed record WebhookVerificationResult(bool IsAccepted, string? RejectionReason, string? EventType, string? DeliveryId)
{
    public static WebhookVerificationResult Accept(string? eventType, string? deliveryId)
        => new(true, null, eventType, deliveryId);

    public static WebhookVerificationResult Reject(string reason)
        => new(false, reason, null, null);
}
