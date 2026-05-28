// -----------------------------------------------------------------------
// <copyright file="SearchEditorModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Config;

internal sealed class SearchEditorModel
{
    public SearchBackend Backend { get; set; } = SearchBackend.DuckDuckGo;

    public BraveSearchEditorModel Brave { get; } = new();

    public SearXngSearchEditorModel SearXng { get; } = new();
}

internal sealed class BraveSearchEditorModel
{
    public string? ApiKeyDraft { get; set; }

    public bool HasPersistedApiKey { get; set; }
}

internal sealed class SearXngSearchEditorModel
{
    public string? Endpoint { get; set; }
}

internal sealed class SearchEditorValidator : IValidateOptions<SearchEditorModel>
{
    public ValidateOptionsResult Validate(string? name, SearchEditorModel options)
    {
        var errors = new List<string>();

        if (options.Backend == SearchBackend.Brave
            && string.IsNullOrWhiteSpace(options.Brave.ApiKeyDraft)
            && !options.Brave.HasPersistedApiKey)
        {
            errors.Add("Brave requires an API key.");
        }

        if (options.Backend == SearchBackend.SearXng)
        {
            if (string.IsNullOrWhiteSpace(options.SearXng.Endpoint))
            {
                errors.Add("SearXNG requires an endpoint URL.");
            }
            else if (!Uri.TryCreate(options.SearXng.Endpoint, UriKind.Absolute, out _))
            {
                errors.Add("SearXNG endpoint must be an absolute URL.");
            }
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}

internal sealed class SearchEditorPersistenceMapper
{
    internal SearchEditorModel Load(NetclawPaths paths)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);

        var backend = ConfigFileHelper.TryGetPathValue(config, "Search.Backend", out var backendRaw)
            ? ParseBackend(backendRaw?.ToString())
            : SearchBackend.DuckDuckGo;

        var endpoint = ConfigFileHelper.TryGetPathValue(config, "Search.SearXngEndpoint", out var endpointRaw)
            ? endpointRaw?.ToString()
            : null;

        var persistedBraveKey = ConfigFileHelper.TryGetPathValue(secrets, "Search.BraveApiKey", out var braveRaw)
            ? ConfigFileHelper.DecryptIfEncrypted(paths, braveRaw?.ToString())
            : null;

        return new SearchEditorModel
        {
            Backend = backend,
            Brave =
            {
                HasPersistedApiKey = !string.IsNullOrWhiteSpace(persistedBraveKey),
            },
            SearXng =
            {
                Endpoint = Normalize(endpoint),
            }
        };
    }

    internal void Save(NetclawPaths paths, SearchEditorModel model)
    {
        var session = new ConfigEditorSession(paths);
        session.Apply(BuildContribution(model));
        session.Save();
    }

    internal SectionContribution BuildContribution(SearchEditorModel model)
    {
        var fieldActions = new List<SectionFieldAction>
        {
            new("Search.Backend", SectionFieldActionKind.Set, model.Backend.ToWireValue())
        };

        var endpoint = Normalize(model.SearXng.Endpoint);
        if (!string.IsNullOrWhiteSpace(endpoint))
            fieldActions.Add(new SectionFieldAction("Search.SearXngEndpoint", SectionFieldActionKind.Set, endpoint));

        var secretActions = new List<SectionSecretAction>();
        if (model.Backend == SearchBackend.Brave)
        {
            var apiKey = Normalize(model.Brave.ApiKeyDraft);
            if (!string.IsNullOrWhiteSpace(apiKey))
                secretActions.Add(new SectionSecretAction(
                    "Search.BraveApiKey",
                    SectionSecretActionKind.Set,
                    new SensitiveString(apiKey)));
            else if (model.Brave.HasPersistedApiKey)
                secretActions.Add(new SectionSecretAction("Search.BraveApiKey", SectionSecretActionKind.Preserve));
        }

        return new SectionContribution(fieldActions, secretActions);
    }

    private static SearchBackend ParseBackend(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "brave" => SearchBackend.Brave,
            "searxng" => SearchBackend.SearXng,
            _ => SearchBackend.DuckDuckGo,
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record SearchEditorValidationIssue(string? FieldId, string Message, ConfigValidationSeverity Severity);

internal sealed record SearchEditorValidationResult(IReadOnlyList<SearchEditorValidationIssue> Issues)
{
    public static readonly SearchEditorValidationResult Empty = new([]);

    public bool HasErrors => Issues.Any(static issue => issue.Severity == ConfigValidationSeverity.Error);

    public IReadOnlyList<SearchEditorValidationIssue> IssuesFor(string fieldId)
        => [.. Issues.Where(issue => string.Equals(issue.FieldId, fieldId, StringComparison.Ordinal))];
}

internal sealed class SearchEditorValidationAdapter
{
    private readonly SearchEditorValidator _validator = new();

    internal SearchEditorValidationResult Validate(SearchEditorModel model)
    {
        var result = _validator.Validate(name: null, model);
        if (result.Succeeded)
            return SearchEditorValidationResult.Empty;

        var failures = result.Failures ?? [];
        var issues = new List<SearchEditorValidationIssue>();
        foreach (var failure in failures)
        {
            issues.Add(failure switch
            {
                var message when message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                    => new SearchEditorValidationIssue("Search.BraveApiKey", message, ConfigValidationSeverity.Error),
                var message when message.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
                    => new SearchEditorValidationIssue("Search.SearXngEndpoint", message, ConfigValidationSeverity.Error),
                _ => new SearchEditorValidationIssue(null, failure, ConfigValidationSeverity.Error),
            });
        }

        return new SearchEditorValidationResult(issues);
    }
}
