// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotAuthOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Auth/mode/host configuration for GitHub Copilot token exchange.
/// </summary>
public sealed class GitHubCopilotAuthOptions : IVendorOptions
{
    /// <summary>
    /// Public Copilot API endpoint. Keep this separate from the GitHub OAuth/API
    /// host used for OAuth device flow.
    /// </summary>
    public Uri CopilotApiBase { get; init; } = new("https://api.githubcopilot.com");

    /// <summary>
    /// GitHub auth host for OAuth flows. For GitHub Enterprise, set to
    /// your tenant host such as <c>https://my-company-ghe.ghe.com</c>.
    /// </summary>
    public Uri GitHubHost { get; init; } = new("https://github.com");

    /// <summary>
    /// GitHub API host used for token exchange and OAuth token refresh.
    /// For GitHub Enterprise, set to your tenant API host such as <c>https://api.my-company-ghe.ghe.com</c>.
    /// </summary>
    public Uri GitHubApiBase { get; init; } = new("https://api.github.com");

    /// <summary>
    /// Relative exchange path appended to <see cref="GitHubApiBase"/>.
    /// </summary>
    public string CopilotTokenExchangePath { get; init; } = "/copilot_internal/v2/token";

    /// <summary>
    /// How to source the long-lived GitHub credential for Copilot exchange.
    /// </summary>
    public GitHubCopilotAuthMode AuthMode { get; init; } = GitHubCopilotAuthMode.OAuthDevice;

    /// <summary>
    /// Optional configured GitHub token (for ApiKey or GitHubAppUser auth modes).
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>
    /// Ordered environment variables to inspect for short-lived token lookup.
    /// </summary>
    public string[] TokenEnvVars { get; init; } =
        ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    public Uri TokenExchangeEndpoint =>
        new(GitHubApiBase, CopilotTokenExchangePath.TrimStart('/'));

    public Uri OAuthDeviceEndpoint =>
        new(GitHubHost, "/login/device/code");

    public Uri OAuthTokenEndpoint =>
        new(GitHubHost, "/login/oauth/access_token");
}

public enum GitHubCopilotAuthMode
{
    OAuthDevice,
    Environment,
    ApiKey,
    GitHubAppUser
}
