using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

internal static class ModelCapabilityResolution
{
    public static (ModelModality InputModalities, ModelModality OutputModalities, int ContextWindowTokens)
        ResolveSessionConfig(ModelReference model, ResolvedModelCapabilities? detected, int defaultContextWindow = 32_768)
    {
        if (model.ContextWindow is int configuredContextWindow
            && detected?.ContextWindowTokens is int detectedContextWindow
            && configuredContextWindow > detectedContextWindow)
        {
            throw new InvalidOperationException(
                $"Models:Main:ContextWindow ({configuredContextWindow}) exceeds the " +
                $"provider-reported effective context window ({detectedContextWindow}). " +
                "Reduce the configured ContextWindow or adjust the provider runtime settings.");
        }

        var inputModalities = model.InputModalities ?? detected?.InputModalities ?? ModelModality.Text;
        var outputModalities = model.OutputModalities ?? detected?.OutputModalities ?? ModelModality.Text;
        var contextWindow = model.ContextWindow ?? detected?.ContextWindowTokens ?? defaultContextWindow;

        return (inputModalities, outputModalities, contextWindow);
    }
}
