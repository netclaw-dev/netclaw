using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Thread-safe cache for the most recent update check result.
/// Singleton — injected into both <see cref="BinaryUpdateCheckService"/> (writer)
/// and <see cref="Gateway.DaemonRuntimeStatusService"/> (reader).
/// </summary>
internal sealed class UpdateCheckCache
{
    private volatile UpdateCheckResult? _result;

    public UpdateCheckResult? LastResult
    {
        get => _result;
        set => _result = value;
    }
}

/// <summary>
/// Checks for binary updates at daemon startup and logs a warning if one is available.
/// Writes the result to <see cref="UpdateCheckCache"/> so
/// <see cref="Gateway.DaemonRuntimeStatusService"/> can include update info in the status API.
/// Never blocks startup, never downloads anything.
/// Runs after <see cref="SystemSkillSyncService"/>.
/// </summary>
internal sealed class BinaryUpdateCheckService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly UpdateCheckCache _cache;
    private readonly ILogger<BinaryUpdateCheckService> _logger;
    private readonly string _currentVersion;

    public BinaryUpdateCheckService(
        HttpClient httpClient,
        UpdateCheckCache cache,
        ILogger<BinaryUpdateCheckService> logger)
        : this(httpClient, cache, logger, BuildInfo.Version)
    {
    }

    // Internal constructor for testing — allows injecting a fake version
    internal BinaryUpdateCheckService(
        HttpClient httpClient,
        UpdateCheckCache cache,
        ILogger<BinaryUpdateCheckService> logger,
        string currentVersion)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
        _currentVersion = currentVersion;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await UpdateCheckService.CheckForUpdateAsync(
                _httpClient, _currentVersion, cancellationToken);

            _cache.LastResult = result;

            if (result.IsUpdateAvailable)
            {
                _logger.LogWarning(
                    "Netclaw update available: {CurrentVersion} → {LatestVersion}. Run 'netclaw update' to upgrade.",
                    result.CurrentVersion, result.LatestVersion);
            }
            else
            {
                _logger.LogInformation("Netclaw is up to date (v{Version})", _currentVersion);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Binary update check failed — continuing normally");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
