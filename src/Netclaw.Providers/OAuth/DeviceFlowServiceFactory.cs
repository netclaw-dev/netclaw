namespace Netclaw.Providers.OAuth;

/// <summary>
/// Selects the correct <see cref="IDeviceFlowService"/> implementation
/// based on the provider descriptor.
/// </summary>
public sealed class DeviceFlowServiceFactory
{
    private readonly OAuthDeviceFlowService _standard;
    private readonly OpenAiDeviceFlowService _openAi;

    public DeviceFlowServiceFactory(
        OAuthDeviceFlowService standard,
        OpenAiDeviceFlowService openAi)
    {
        _standard = standard;
        _openAi = openAi;
    }

    public IDeviceFlowService GetFor(OAuthAuth oauth) =>
        oauth.UseProprietaryDeviceFlow ? _openAi : _standard;
}
