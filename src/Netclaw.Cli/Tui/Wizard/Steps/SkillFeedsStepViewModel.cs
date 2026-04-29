// -----------------------------------------------------------------------
// <copyright file="SkillFeedsStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.SkillClient;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring private skill server feeds.
/// Sub-step 0: Yes/No to connect.
/// Sub-step 1: URL text input.
/// Sub-step 2: Probe (async).
/// Sub-step 3: Name input (auto-suggested from hostname).
/// Sub-step 4: Add another or continue.
/// </summary>
public sealed class SkillFeedsStepViewModel : IWizardStepViewModel, IDisposable
{
    private int _currentSubStep;
    private int _highWaterSubStep;
    private bool _wantsToConnect;

    private string _currentUrl = "";
    private string _currentName = "";
    private int _lastProbeSkillCount;
    private string? _lastProbeError;
    private bool _probing;

    private readonly List<ConfiguredFeed> _feeds = [];

    public string StepId => WizardStepIds.SkillFeeds;
    public string DisplayTitle => "Skill Feeds";

    public int CurrentSubStep => _currentSubStep;
    public bool WantsToConnect => _wantsToConnect;
    public string CurrentUrl => _currentUrl;
    public string CurrentName => _currentName;
    public int LastProbeSkillCount => _lastProbeSkillCount;
    public string? LastProbeError => _lastProbeError;
    public bool IsProbing => _probing;
    public IReadOnlyList<ConfiguredFeed> ConfiguredFeeds => _feeds;

    public int SubStepCount => _wantsToConnect ? 5 : 1;

    public bool IsApplicable(WizardContext context) => true;

    public void SetWantsToConnect(bool value)
    {
        _wantsToConnect = value;
    }

    public void SetUrl(string url)
    {
        _currentUrl = url.Trim();
        _currentName = SuggestNameFromUrl(_currentUrl);
    }

    public void SetName(string name)
    {
        _currentName = SanitizeFeedName(name.Trim());
    }

    /// <summary>
    /// Marks the probe as in-progress synchronously so the render path
    /// sees <see cref="IsProbing"/> == true before the background task starts.
    /// </summary>
    public void BeginProbe()
    {
        _probing = true;
        _lastProbeError = null;
        _lastProbeSkillCount = 0;
    }

    public async Task ProbeAsync(CancellationToken ct)
    {
        _probing = true;
        _lastProbeError = null;
        _lastProbeSkillCount = 0;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var client = new SkillServerClient(_currentUrl);
            var index = await client.GetRfcIndexAsync(cts.Token);

            if (index is null)
            {
                _lastProbeError = "Server returned empty response";
                return;
            }

            _lastProbeSkillCount = index.Skills.Count;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastProbeError = "Connection timed out";
        }
        catch (HttpRequestException ex)
        {
            _lastProbeError = ex.Message;
        }
        catch (Exception ex)
        {
            _lastProbeError = ex.Message;
        }
        finally
        {
            _probing = false;
        }
    }

    public bool ProbeSucceeded => _lastProbeError is null && !_probing;

    public void SaveCurrentFeed()
    {
        if (string.IsNullOrWhiteSpace(_currentName) || string.IsNullOrWhiteSpace(_currentUrl))
            return;

        _feeds.Add(new ConfiguredFeed(_currentName, _currentUrl, _lastProbeSkillCount));
        _currentUrl = "";
        _currentName = "";
        _lastProbeSkillCount = 0;
        _lastProbeError = null;
    }

    public void StartAddAnother()
    {
        _currentSubStep = 1;
        _highWaterSubStep = 1;
    }

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Connect to a private skill server to automatically sync skills.",
        1 => "  Enter the base URL of your skill server.",
        2 when _probing => "  Discovering skills...",
        2 when _lastProbeError is not null => "  Connection failed. Try again, edit URL, or skip.",
        2 => "  Connected successfully.",
        3 => "  Give this feed a short name for your config file.",
        4 => "  Add more feeds or continue to the next step.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && !_wantsToConnect)
            return false;

        if (_currentSubStep < SubStepCount - 1)
        {
            _currentSubStep++;
            if (_currentSubStep > _highWaterSubStep)
                _highWaterSubStep = _currentSubStep;
            return true;
        }

        return false;
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
            _currentSubStep = _highWaterSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (_feeds.Count == 0)
            return;

        builder.SkillFeedSources = _feeds
            .Select(f => new SkillFeedSource
            {
                Name = f.Name,
                Url = f.Url,
                Enabled = true
            })
            .ToList();
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public void Dispose() { }

    internal static string SuggestNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("127.", StringComparison.Ordinal)
                || string.Equals(host, "::1", StringComparison.Ordinal))
            {
                return "localhost";
            }

            return host
                .Replace('.', '-')
                .ToLowerInvariant();
        }
        catch
        {
            return "custom";
        }
    }

    internal static string SanitizeFeedName(string name)
    {
        var sanitized = new char[name.Length];
        var len = 0;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
                sanitized[len++] = char.ToLowerInvariant(c);
            else if (c is ' ' or '_' or '.')
                sanitized[len++] = '-';
        }

        // Trim leading/trailing hyphens
        var span = sanitized.AsSpan(0, len).Trim('-');
        return span.Length > 0 ? new string(span) : "custom";
    }

    public sealed record ConfiguredFeed(string Name, string Url, int SkillCount);
}
