using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ModelCapabilityResolutionTests
{
    [Fact]
    public void ResolveSessionConfig_UsesConfiguredContextWindowAsClamp()
    {
        var model = new ModelReference
        {
            ContextWindow = 32768,
            InputModalities = ModelModality.Text,
            OutputModalities = ModelModality.Text
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveSessionConfig(model, detected);

        Assert.Equal(32768, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveSessionConfig_ThrowsWhenConfiguredContextExceedsDetectedWindow()
    {
        var model = new ModelReference
        {
            ContextWindow = 131072,
            InputModalities = ModelModality.Text,
            OutputModalities = ModelModality.Text
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityResolution.ResolveSessionConfig(model, detected));

        Assert.Contains("ContextWindow", ex.Message);
        Assert.Contains("65536", ex.Message);
    }

    [Fact]
    public void ResolveSessionConfig_StillValidatesConfiguredContextWhenModalitiesAreManual()
    {
        var model = new ModelReference
        {
            ContextWindow = 131072,
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityResolution.ResolveSessionConfig(model, detected));
    }

    [Fact]
    public void ResolveSessionConfig_UsesDetectedContextWhenNoClampConfigured()
    {
        var model = new ModelReference();
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text | ModelModality.Image, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveSessionConfig(model, detected);

        Assert.Equal(65536, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }
}
