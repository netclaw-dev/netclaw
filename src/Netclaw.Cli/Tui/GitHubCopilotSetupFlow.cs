// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotSetupFlow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;

namespace Netclaw.Cli.Tui;

public enum GitHubCopilotAuthHostMode
{
    GitHubCom,
    Enterprise
}

internal static class GitHubCopilotSetupFlow
{
    public const string ProviderType = "github-copilot";
    public const string GitHubComLabel = "GitHub.com";
    public const string EnterpriseLabel = "GitHub Enterprise";

    public static readonly IReadOnlyList<string> AuthHostLabels = [GitHubComLabel, EnterpriseLabel];

    public static bool IsGitHubCopilot(string? providerType)
        => string.Equals(providerType, ProviderType, StringComparison.OrdinalIgnoreCase);

    public static GitHubCopilotAuthHostMode ParseAuthHostLabel(string label)
        => string.Equals(label, EnterpriseLabel, StringComparison.Ordinal)
            ? GitHubCopilotAuthHostMode.Enterprise
            : GitHubCopilotAuthHostMode.GitHubCom;

    public static bool TryResolveEnterpriseVendorOptions(
        string? gitHubHost,
        string? gitHubApiBase,
        out IReadOnlyDictionary<string, object?>? vendorOptions,
        out string error)
    {
        vendorOptions = null;
        error = string.Empty;

        if (!GitHubCopilotAuthResolver.TryResolveSetupOptions(
                gitHubHost,
                gitHubApiBase,
                includeAmbientEnvironment: false,
                out var setupOptions,
                out var setupError))
        {
            error = setupError ?? "GitHub Copilot Enterprise host settings are invalid.";
            return false;
        }

        vendorOptions = GitHubCopilotAuthResolver.ToVendorOptions(setupOptions);
        return true;
    }

    public static string GetApiBasePlaceholder(string? gitHubHost)
    {
        if (GitHubCopilotAuthResolver.TryResolveSetupOptions(
                gitHubHost,
                gitHubApiBase: null,
                includeAmbientEnvironment: false,
                out var options,
                out _))
        {
            return options.GitHubApiBase.ToString().TrimEnd('/');
        }

        return "https://ghe.example.com/api/v3";
    }

    public static ProviderEntry BuildOAuthEntry(IReadOnlyDictionary<string, object?>? vendorOptions)
    {
        var entry = new ProviderEntry
        {
            Type = ProviderType,
            AuthMethod = AuthMethod.OAuthDevice,
        };
        entry.SetVendorOptions(ToJsonObject(vendorOptions));
        return entry;
    }

    public static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? vendorOptions)
    {
        if (vendorOptions is null || vendorOptions.Count == 0)
            return null;

        return JsonNode.Parse(JsonSerializer.Serialize(vendorOptions, JsonDefaults.ConfigFile))?.AsObject();
    }
}
