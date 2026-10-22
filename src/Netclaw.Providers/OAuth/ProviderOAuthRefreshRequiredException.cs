// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthRefreshRequiredException.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Providers.OAuth;

/// <summary>
/// Raised when an inference provider's OAuth credential cannot be refreshed and
/// the operator must re-authorize the provider.
/// </summary>
public sealed class ProviderOAuthRefreshRequiredException : Exception
{
    public ProviderOAuthRefreshRequiredException(string message) : base(message) { }
}

/// <summary>
/// Raised when a token server rejects a refresh token with a code that only a new sign-in fixes.
/// </summary>
public sealed class OAuthRefreshTokenRejectedException(string code)
    : Exception($"The token server rejected the refresh token ({code}).")
{
    /// <summary>The error code from the token server, for example <c>refresh_token_reused</c>.</summary>
    public string Code { get; } = code;
}
