using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.OAuth;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class DeviceFlowServiceFactoryTests
{
    [Fact]
    public void FromDescriptor_ProprietaryFlow_UsesPollingAndPkceExchangeEndpoints()
    {
        var descriptor = new TestDescriptor(
            oauthDeviceEndpoint: "https://auth.example.com/usercode",
            oauthTokenEndpoint: "https://auth.example.com/oauth/token",
            oauthDefaultClientId: "client-1",
            oauthPollingEndpoint: "https://auth.example.com/deviceauth/token",
            useProprietaryDeviceFlow: true);

        var config = OAuthDeviceFlowConfig.FromDescriptor(descriptor);

        Assert.Equal("https://auth.example.com/usercode", config.DeviceAuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/deviceauth/token", config.TokenEndpoint);
        Assert.Equal("https://auth.example.com/oauth/token", config.PkceExchangeEndpoint);
    }

    [Fact]
    public void Factory_ProprietaryDescriptor_ReturnsOpenAiService()
    {
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(new HttpClient()),
            new OpenAiDeviceFlowService(new HttpClient()));

        var descriptor = new TestDescriptor(useProprietaryDeviceFlow: true);

        var service = factory.GetFor(descriptor);

        Assert.IsType<OpenAiDeviceFlowService>(service);
    }

    [Fact]
    public void Factory_StandardDescriptor_ReturnsStandardOAuthService()
    {
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(new HttpClient()),
            new OpenAiDeviceFlowService(new HttpClient()));

        var descriptor = new TestDescriptor(useProprietaryDeviceFlow: false);

        var service = factory.GetFor(descriptor);

        Assert.IsType<OAuthDeviceFlowService>(service);
    }

    private sealed class TestDescriptor(
        string? oauthDeviceEndpoint = "https://example.com/device",
        string? oauthTokenEndpoint = "https://example.com/token",
        string? oauthDefaultClientId = "client-id",
        string? oauthPollingEndpoint = null,
        bool useProprietaryDeviceFlow = false) : IProviderDescriptor
    {
        public string TypeKey => "test";
        public string DisplayName => "Test";
        public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthDevice];
        public string DefaultEndpoint => "https://example.com";
        public string ModelListingPath => "/models";
        public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
        public string? ApiKeyGuidanceUrl => null;
        public string? OAuthDeviceEndpoint => oauthDeviceEndpoint;
        public string? OAuthTokenEndpoint => oauthTokenEndpoint;
        public string? OAuthDefaultClientId => oauthDefaultClientId;
        public string? OAuthPollingEndpoint => oauthPollingEndpoint;
        public bool UseProprietaryDeviceFlow => useProprietaryDeviceFlow;

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
