// -----------------------------------------------------------------------
// <copyright file="DeviceFlowServiceFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class DeviceFlowServiceFactoryTests
{
    [Fact]
    public void FromOAuth_ProprietaryFlow_UsesPollingAndPkceExchangeEndpoints()
    {
        var oauth = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice],
            DeviceEndpoint = new Uri("https://auth.example.com/usercode"),
            TokenEndpoint = new Uri("https://auth.example.com/oauth/token"),
            ClientId = "client-1",
            PollingEndpoint = new Uri("https://auth.example.com/deviceauth/token"),
            UseProprietaryDeviceFlow = true,
            ExtraAuthParams = new Dictionary<string, string>
            {
                ["originator"] = "netclaw"
            }
        };

        var config = OAuthDeviceFlowConfig.FromOAuth(oauth);

        Assert.Equal("https://auth.example.com/usercode", config.DeviceAuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/deviceauth/token", config.TokenEndpoint);
        Assert.Equal("https://auth.example.com/oauth/token", config.PkceExchangeEndpoint);
        Assert.NotNull(config.ExtraAuthParams);
        Assert.Equal("netclaw", config.ExtraAuthParams!["originator"]);
    }

    [Fact]
    public void Factory_ProprietaryOAuth_ReturnsOpenAiService()
    {
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(new HttpClient()),
            new OpenAiDeviceFlowService(new HttpClient()));

        var oauth = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice],
            TokenEndpoint = new Uri("https://example.com/token"),
            ClientId = "client-id",
            UseProprietaryDeviceFlow = true,
        };

        var service = factory.GetFor(oauth);

        Assert.IsType<OpenAiDeviceFlowService>(service);
    }

    [Fact]
    public void Factory_StandardOAuth_ReturnsStandardOAuthService()
    {
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(new HttpClient()),
            new OpenAiDeviceFlowService(new HttpClient()));

        var oauth = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice],
            TokenEndpoint = new Uri("https://example.com/token"),
            ClientId = "client-id",
            DeviceEndpoint = new Uri("https://example.com/device"),
            UseProprietaryDeviceFlow = false,
        };

        var service = factory.GetFor(oauth);

        Assert.IsType<OAuthDeviceFlowService>(service);
    }
}
