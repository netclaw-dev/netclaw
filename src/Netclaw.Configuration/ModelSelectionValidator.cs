// -----------------------------------------------------------------------
// <copyright file="ModelSelectionValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;

namespace Netclaw.Configuration;

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
        => errors.AddRange(ValidateModel($"Models:{role}", model));

    /// <summary>
    /// The configuration-only rules of one model. <paramref name="keyPath"/> is the key of the model
    /// entry, for example <c>Models:Definitions:fast</c>, and each message starts with it.
    /// </summary>
    public static IReadOnlyList<string> ValidateModel(string keyPath, ModelReference model)
    {
        var errors = new List<string>();
        if (model.ContextWindow is int value && value < MinContextWindow)
        {
            errors.Add(
                $"{keyPath}:ContextWindow ({value}) is below minimum ({MinContextWindow}). " +
                "A context window below 4096 tokens is too small for practical use.");
        }

        if (model.MaxOutputTokens is int limit)
        {
            if (limit <= 0)
            {
                errors.Add($"{keyPath}:MaxOutputTokens ({limit}) must be a positive integer.");
            }
            else if (model.ContextWindow is int window && limit >= window)
            {
                // The prompt shares the window with the output, so a limit that fills the window
                // leaves no room for the prompt and every call fails.
                errors.Add(
                    $"{keyPath}:MaxOutputTokens ({limit}) must be below the context window ({window}).");
            }
        }

        return errors;
    }
}
