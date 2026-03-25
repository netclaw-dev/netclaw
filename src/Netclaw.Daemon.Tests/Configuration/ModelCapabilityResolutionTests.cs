using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ModelCapabilityResolutionTests
{
    [Fact]
    public void ResolveModelCapabilities_UsesConfiguredContextWindowAsClamp()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 32768,
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(32768, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveModelCapabilities_ThrowsWhenConfiguredContextExceedsDetectedWindow()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 131072,
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityResolution.ResolveModelCapabilities(models, detected));

        Assert.Contains("ContextWindow", ex.Message);
        Assert.Contains("65536", ex.Message);
    }

    [Fact]
    public void ResolveModelCapabilities_StillValidatesConfiguredContextWhenModalitiesAreManual()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 131072,
                InputModalities = ModelModality.Text | ModelModality.Image,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityResolution.ResolveModelCapabilities(models, detected));
    }

    [Fact]
    public void ResolveModelCapabilities_UsesDetectedContextWhenNoClampConfigured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference()
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text | ModelModality.Image, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(65536, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }
}
