using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

public interface ISlackThreadCursorStore
{
    Task<string?> GetCursorAsync(string streamKey, CancellationToken cancellationToken = default);

    Task<bool> TryAdvanceAsync(string streamKey, string newCursor, CancellationToken cancellationToken = default);
}

public sealed class FileSlackThreadCursorStore : ISlackThreadCursorStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private Dictionary<string, string>? _cache;

    public FileSlackThreadCursorStore(NetclawPaths paths)
    {
        Directory.CreateDirectory(paths.CacheDirectory);
        _filePath = Path.Combine(paths.CacheDirectory, "slack-thread-cursors.json");
    }

    public async Task<string?> GetCursorAsync(string streamKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamKey))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadAsync(cancellationToken);
            return data.TryGetValue(streamKey, out var cursor) ? cursor : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAdvanceAsync(string streamKey, string newCursor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamKey) || !TryParseTs(newCursor, out var proposedTs))
            return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadAsync(cancellationToken);
            if (data.TryGetValue(streamKey, out var existingCursor)
                && TryParseTs(existingCursor, out var existingTs)
                && proposedTs <= existingTs)
            {
                return false;
            }

            data[streamKey] = newCursor;
            await SaveAsync(data, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
            return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return _cache;
        }

        await using var stream = File.OpenRead(_filePath);
        _cache = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _serializerOptions, cancellationToken)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return _cache;
    }

    private async Task SaveAsync(Dictionary<string, string> data, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, data, _serializerOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool TryParseTs(string? ts, out decimal value)
    {
        return decimal.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

internal sealed class NullSlackThreadCursorStore : ISlackThreadCursorStore
{
    public static readonly NullSlackThreadCursorStore Instance = new();

    private NullSlackThreadCursorStore() { }

    public Task<string?> GetCursorAsync(string streamKey, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<bool> TryAdvanceAsync(string streamKey, string newCursor, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
