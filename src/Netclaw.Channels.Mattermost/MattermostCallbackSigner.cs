// -----------------------------------------------------------------------
// <copyright file="MattermostCallbackSigner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// HMAC-SHA256 signing and verification for interactive button callback context.
/// The signing key is ephemeral — generated per daemon lifetime — so buttons
/// from a previous process are automatically rejected on restart.
/// </summary>
internal static class MattermostCallbackSigner
{
    public static byte[] GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static string Sign(byte[] key, string callId, string selectedKey, string requesterSenderId, string rootPostId)
    {
        var message = $"{callId}\n{selectedKey}\n{requesterSenderId}\n{rootPostId}";
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(key, messageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Verify(byte[] key, string callId, string selectedKey, string requesterSenderId, string rootPostId, string signature)
    {
        var expected = Sign(key, callId, selectedKey, requesterSenderId, rootPostId);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }
}

/// <summary>
/// Holds the ephemeral HMAC signing key for Mattermost callback verification.
/// Generated once per daemon lifetime; registered as a singleton.
/// </summary>
public sealed class MattermostCallbackSigningKey(byte[] key)
{
    public byte[] Key { get; } = key;
}
