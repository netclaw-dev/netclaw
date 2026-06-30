// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotAuthOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// GitHub host settings used by the GitHub Copilot provider.
/// </summary>
public sealed class GitHubCopilotAuthOptions : IVendorOptions
{
    public Uri GitHubHost { get; init; } = GitHubCopilotAuthResolver.PublicGitHubHost;
    public Uri GitHubApiBase { get; init; } = GitHubCopilotAuthResolver.PublicGitHubApiBase;
}

public sealed record GitHubCopilotResolvedAuthOptions(
    Uri GitHubHost,
    Uri GitHubApiBase,
    Uri DeviceEndpoint,
    Uri OAuthTokenEndpoint,
    Uri CopilotTokenExchangeEndpoint)
{
    public GitHubCopilotAuthOptions ToOptions() => new()
    {
        GitHubHost = GitHubHost,
        GitHubApiBase = GitHubApiBase,
    };
}

public static class GitHubCopilotAuthResolver
{
    public static readonly Uri PublicGitHubHost = new("https://github.com");
    public static readonly Uri PublicGitHubApiBase = new("https://api.github.com");

    private static readonly string[] GitHubHostEnvironmentVariables =
    [
        "COPILOT_GH_HOST",
        "GHE_HOST",
        "GH_HOST",
        "GITHUB_SERVER_URL",
    ];

    public static GitHubCopilotResolvedAuthOptions Resolve(ProviderEntry entry)
    {
        var options = entry.GetVendorOptions<GitHubCopilotAuthOptions>() ?? new GitHubCopilotAuthOptions();
        return Resolve(options);
    }

    public static GitHubCopilotResolvedAuthOptions Resolve(GitHubCopilotAuthOptions? options)
    {
        options ??= new GitHubCopilotAuthOptions();
        var gitHubHost = NormalizeGitHubHost(options.GitHubHost, nameof(options.GitHubHost));
        var gitHubApiBase = NormalizeGitHubApiBase(options.GitHubApiBase, nameof(options.GitHubApiBase));

        return new GitHubCopilotResolvedAuthOptions(
            gitHubHost,
            gitHubApiBase,
            AppendPath(gitHubHost, "login/device/code"),
            AppendPath(gitHubHost, "login/oauth/access_token"),
            AppendPath(gitHubApiBase, "copilot_internal/v2/token"));
    }

    public static bool TryResolveSetupOptions(
        string? gitHubHost,
        string? gitHubApiBase,
        bool includeAmbientEnvironment,
        out GitHubCopilotAuthOptions options,
        out string? error)
    {
        options = new GitHubCopilotAuthOptions();
        error = null;

        var hostValue = FirstNonEmpty(gitHubHost,
            includeAmbientEnvironment ? ReadFirstEnvironment(GitHubHostEnvironmentVariables) : null);
        var apiBaseValue = FirstNonEmpty(gitHubApiBase,
            includeAmbientEnvironment ? Environment.GetEnvironmentVariable("GITHUB_API_URL") : null);

        if (hostValue is null && apiBaseValue is null)
            return true;

        if (hostValue is null)
        {
            if (TryParseUri(apiBaseValue!, assumeHttps: true, out var apiOnly)
                && UriEquals(NormalizeGitHubApiBase(apiOnly, "GITHUB_API_URL"), PublicGitHubApiBase))
            {
                return true;
            }

            error = "GitHub Copilot enterprise API base requires a GitHub enterprise host.";
            return false;
        }

        Uri normalizedHost;
        try
        {
            if (!TryParseUri(hostValue, assumeHttps: true, out var parsedHost))
            {
                error = $"GitHub Copilot enterprise host must be an absolute HTTPS URI or hostname, got '{hostValue}'.";
                return false;
            }

            normalizedHost = NormalizeGitHubHost(parsedHost, "GitHubHost");
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        Uri normalizedApiBase;
        try
        {
            if (apiBaseValue is null)
            {
                normalizedApiBase = DeriveGitHubApiBase(normalizedHost);
            }
            else
            {
                if (!TryParseUri(apiBaseValue, assumeHttps: true, out var parsedApiBase))
                {
                    error = $"GitHub Copilot enterprise API base must be an absolute HTTPS URI or hostname, got '{apiBaseValue}'.";
                    return false;
                }

                normalizedApiBase = NormalizeGitHubApiBase(parsedApiBase, "GitHubApiBase");
            }
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        options = new GitHubCopilotAuthOptions
        {
            GitHubHost = normalizedHost,
            GitHubApiBase = normalizedApiBase,
        };
        return true;
    }

    public static IReadOnlyDictionary<string, object?>? ToVendorOptions(GitHubCopilotAuthOptions options)
    {
        var resolved = Resolve(options);
        if (UriEquals(resolved.GitHubHost, PublicGitHubHost)
            && UriEquals(resolved.GitHubApiBase, PublicGitHubApiBase))
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            [nameof(GitHubCopilotAuthOptions.GitHubHost)] = resolved.GitHubHost.ToString().TrimEnd('/'),
            [nameof(GitHubCopilotAuthOptions.GitHubApiBase)] = resolved.GitHubApiBase.ToString().TrimEnd('/'),
        };
    }

    public static IReadOnlyDictionary<string, object?>? ToVendorOptions(ProviderEntry entry) =>
        ToVendorOptions(Resolve(entry).ToOptions());

    private static Uri DeriveGitHubApiBase(Uri gitHubHost)
    {
        if (UriEquals(gitHubHost, PublicGitHubHost))
            return PublicGitHubApiBase;

        if (gitHubHost.Host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeGitHubApiBase(new UriBuilder(gitHubHost)
            {
                Host = $"api.{gitHubHost.Host}",
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            }.Uri, "GitHubApiBase");
        }

        return NormalizeGitHubApiBase(AppendPath(gitHubHost, "api/v3"), "GitHubApiBase");
    }

    private static Uri NormalizeGitHubHost(Uri uri, string name)
    {
        RequireSafeHttpsUri(uri, name);
        if (uri.AbsolutePath is not ("" or "/"))
            throw new InvalidOperationException($"{name} must be a host origin, not a URL with a path.");

        return new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static Uri NormalizeGitHubApiBase(Uri uri, string name)
    {
        RequireSafeHttpsUri(uri, name);
        var path = uri.AbsolutePath.TrimEnd('/');
        return new UriBuilder(uri)
        {
            Path = path == "/" ? string.Empty : path,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static void RequireSafeHttpsUri(Uri uri, string name)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name} must use HTTPS.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"{name} must not include user information.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{name} must not include a query string or fragment.");
    }

    private static Uri AppendPath(Uri baseUri, string relativePath)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (basePath == "/")
            basePath = string.Empty;

        return new UriBuilder(baseUri)
        {
            Path = $"{basePath}/{relativePath.TrimStart('/')}",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static bool TryParseUri(string value, bool assumeHttps, out Uri uri)
    {
        var normalized = assumeHttps && !value.Contains("://", StringComparison.Ordinal)
            ? $"https://{value.Trim()}"
            : value.Trim();
        return Uri.TryCreate(normalized, UriKind.Absolute, out uri!);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadFirstEnvironment(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.ToString().TrimEnd('/'), right.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
