namespace Netclaw.App.Configuration;

/// <summary>
/// Points to a specific model on a named provider. Bound from the
/// "Models" configuration section. The <see cref="Provider"/> value
/// must match a key in the "Providers" dictionary.
/// </summary>
public sealed class ModelReference
{
    public string Provider { get; set; } = "local-ollama";
    public string ModelId { get; set; } = "qwen3:30b";
    public int? ContextWindow { get; set; }
}
