// -----------------------------------------------------------------------
// <copyright file="GitHubAccessTokenSources.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.OAuth;

namespace Netclaw.Providers.GitHubCopilot;

public interface IGitHubAccessTokenSource
{
    ValueTask<SensitiveString> GetAsync(CancellationToken ct);
}

internal sealed class StoredOAuthTokenSource(
    ProviderEntry entry,
    string? providerName = null,
    OAuthAuth? oauth = null,
    ProviderOAuthTokenRefreshService? tokenRefreshService = null)
    : IGitHubAccessTokenSource
{
    public async ValueTask<SensitiveString> GetAsync(CancellationToken ct)
    {
        if (providerName is not null && oauth is not null && tokenRefreshService is not null)
        {
            return await tokenRefreshService.GetValidAccessTokenAsync(
                providerName,
                entry,
                oauth,
                ct);
        }

        return entry.OAuthAccessToken.RequireValid(
            "GitHub OAuth access token (re-run 'netclaw provider add <name> github-copilot --auth oauth-device')");
    }
}

internal sealed class EnvironmentGitHubTokenSource(GitHubCopilotAuthOptions options) : IGitHubAccessTokenSource
{
    public ValueTask<SensitiveString> GetAsync(CancellationToken ct)
    {
        foreach (var name in options.TokenEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return ValueTask.FromResult(new SensitiveString(value.Trim()));
        }

        throw new InvalidOperationException(
            "No GitHub token found. Set COPILOT_GITHUB_TOKEN, GH_TOKEN, or GITHUB_TOKEN.");
    }
}

internal sealed class ConfiguredGitHubTokenSource(
    ProviderEntry entry,
    GitHubCopilotAuthOptions options,
    string missingMessage)
    : IGitHubAccessTokenSource
{
    public ValueTask<SensitiveString> GetAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.GitHubToken))
            return ValueTask.FromResult(new SensitiveString(options.GitHubToken.Trim()));

        return ValueTask.FromResult(entry.ApiKey.RequireValid(missingMessage));
    }
}

internal static class GitHubCopilotTokenValidator
{
    public static void Validate(string token, GitHubCopilotAuthMode authMode)
    {
        if (authMode == GitHubCopilotAuthMode.GitHubAppUser
            && !token.StartsWith("ghu_", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "GitHubAppUser auth mode requires a GitHub App user token (ghu_).");
        }

        if (token.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Classic GitHub PATs (ghp_) are not supported for GitHub Copilot. "
                + "Use a fine-grained PAT (github_pat_), OAuth token (gho_), or GitHub App user token (ghu_).");
        }

        if (token.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("gho_", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("ghu_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new NotSupportedException(
            "Unsupported GitHub token type for Copilot. Expected github_pat_, gho_, or ghu_.");
    }
}
