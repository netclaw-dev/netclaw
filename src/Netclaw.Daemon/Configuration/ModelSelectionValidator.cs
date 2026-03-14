using Microsoft.Extensions.Options;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public sealed class ModelSelectionValidator : IValidateOptions<ModelSelection>
{
    private const int MinContextWindow = 4096;

    public ValidateOptionsResult Validate(string? name, ModelSelection options)
    {
        var errors = new List<string>();

        ValidateRole(nameof(options.Main), options.Main, errors);

        if (options.Fallback is not null)
            ValidateRole(nameof(options.Fallback), options.Fallback, errors);

        if (options.Compaction is not null)
            ValidateRole(nameof(options.Compaction), options.Compaction, errors);

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateRole(string role, ModelReference model, List<string> errors)
    {
        if (model.ContextWindow is int value && value < MinContextWindow)
        {
            errors.Add(
                $"Models:{role}:ContextWindow ({value}) is below minimum ({MinContextWindow}). " +
                "A context window below 4096 tokens is too small for practical use.");
        }
    }
}
