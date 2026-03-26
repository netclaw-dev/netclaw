using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the web search backend (DuckDuckGo/Brave/SearXNG)
/// and entering credentials if needed. Two sub-steps: backend selection, then credentials.
/// </summary>
public sealed class SearchStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _completedSubStep;

    public string StepId => "search";
    public string DisplayTitle => "Web Search";

    public SearchBackend SelectedBackend { get; set; } = SearchBackend.DuckDuckGo;
    public string? BraveApiKey { get; set; }
    public string? SearXngEndpoint { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    public int SubStepCount => NeedsCredentials ? 2 : 1;

    private bool NeedsCredentials => SelectedBackend is SearchBackend.Brave or SearchBackend.SearXng;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  DuckDuckGo works without config but may hit bot detection. Brave Search is more reliable.",
        1 when SelectedBackend == SearchBackend.Brave =>
            "  Get a free API key at https://brave.com/search/api/. Stored in secrets.json.",
        1 => "  Enter the base URL of your SearXNG instance. JSON format must be enabled in settings.yml.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && NeedsCredentials)
        {
            _currentSubStep = 1;
            _completedSubStep = 1;
            return true;
        }
        return false; // step complete
    }

    public bool TryGoBack()
    {
        if (_currentSubStep > 0)
        {
            _currentSubStep--;
            return true;
        }
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        if (direction == NavigationDirection.Back)
            _currentSubStep = _completedSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        builder.Search = new SearchConfigSection
        {
            Backend = SelectedBackend,
            SearXngEndpoint = SearXngEndpoint
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (SelectedBackend == SearchBackend.Brave && !string.IsNullOrWhiteSpace(BraveApiKey))
        {
            builder.AddSection("Search", new Dictionary<string, object>
            {
                ["BraveApiKey"] = BraveApiKey
            });
        }
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public void Dispose() { }
}
