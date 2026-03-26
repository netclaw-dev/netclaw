using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

internal static class ModelCapabilityResolution
{
    /// <summary>
    /// Resolve runtime model capabilities from the model reference configuration
    /// and detected capabilities. Returns a <see cref="ModelCapabilities"/> instance
    /// with all fields populated (never null).
    /// </summary>
    public static ModelCapabilities ResolveModelCapabilities(
        ModelSelection models,
        ResolvedModelCapabilities? detected,
        int defaultContextWindow = 32_768)
    {
        var model = models.Main;

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

        return new ModelCapabilities
        {
            ModelId = model.ModelId,
            ContextWindowTokens = contextWindow,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            CompactionModelId = models.Compaction?.ModelId,
        };
    }
}
