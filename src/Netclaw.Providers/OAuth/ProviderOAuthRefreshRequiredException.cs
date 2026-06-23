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
