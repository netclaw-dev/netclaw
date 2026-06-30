// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotAuthResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class GitHubCopilotAuthResolverTests
{
    [Fact]
    public void Resolve_DefaultEntry_IgnoresAmbientGitHubHostEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("GH_HOST");
        try
        {
            Environment.SetEnvironmentVariable("GH_HOST", "enterprise.example.com");
            var entry = new ProviderEntry { Type = "github-copilot", AuthMethod = AuthMethod.OAuthDevice };

            var resolved = GitHubCopilotAuthResolver.Resolve(entry);

            Assert.Equal(new Uri("https://github.com/login/device/code"), resolved.DeviceEndpoint);
            Assert.Equal(new Uri("https://github.com/login/oauth/access_token"), resolved.OAuthTokenEndpoint);
            Assert.Equal(new Uri("https://api.github.com/copilot_internal/v2/token"), resolved.CopilotTokenExchangeEndpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_HOST", previous);
        }
    }

    [Fact]
    public void Resolve_GheComHost_DerivesApiSubdomainForSetup()
    {
        var ok = GitHubCopilotAuthResolver.TryResolveSetupOptions(
            gitHubHost: "my-company.ghe.com",
            gitHubApiBase: null,
            includeAmbientEnvironment: false,
            out var options,
            out var error);

        Assert.True(ok, error);
        var resolved = GitHubCopilotAuthResolver.Resolve(options);
        Assert.Equal(new Uri("https://my-company.ghe.com/login/device/code"), resolved.DeviceEndpoint);
        Assert.Equal(new Uri("https://api.my-company.ghe.com/copilot_internal/v2/token"), resolved.CopilotTokenExchangeEndpoint);
    }

    [Fact]
    public void Resolve_GhesApiBase_PreservesApiV3Path()
    {
        var entry = new ProviderEntry { Type = "github-copilot", AuthMethod = AuthMethod.OAuthDevice };
        entry.SetVendorOptions(new JsonObject
        {
            ["GitHubHost"] = "https://ghe.example.com",
            ["GitHubApiBase"] = "https://ghe.example.com/api/v3",
        });

        var resolved = GitHubCopilotAuthResolver.Resolve(entry);

        Assert.Equal(new Uri("https://ghe.example.com/api/v3/copilot_internal/v2/token"),
            resolved.CopilotTokenExchangeEndpoint);
    }

    [Fact]
    public void TryResolveSetupOptions_RejectsHttpHost()
    {
        var ok = GitHubCopilotAuthResolver.TryResolveSetupOptions(
            gitHubHost: "http://ghe.example.com",
            gitHubApiBase: null,
            includeAmbientEnvironment: false,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public void ToVendorOptions_DefaultOptions_ReturnsNull()
    {
        var vendorOptions = GitHubCopilotAuthResolver.ToVendorOptions(new GitHubCopilotAuthOptions());

        Assert.Null(vendorOptions);
    }
}
