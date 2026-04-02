using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Unit tests for <see cref="DeviceRegistry"/>.
/// Uses a temporary directory so no real ~/.netclaw files are touched.
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _registry;

    public DeviceRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        var paths = new NetclawPaths(_tempDir);
        _registry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (string RawToken, PairedDevice Device) MakeDevice(string name, DateTimeOffset createdAt)
        => DeviceTestHelpers.MakeDevice(name, createdAt);

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_empty_when_no_devices_file()
    {
        var devices = await _registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(devices);
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_persists_device_readable_by_List()
    {
        var (_, device) = MakeDevice("laptop", _time.GetUtcNow());

        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var result = await _registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("laptop", result[0].Name);
        Assert.Equal(device.TokenHash, result[0].TokenHash);
        Assert.Equal(device.Salt, result[0].Salt);
    }

    [Fact]
    public async Task Add_multiple_devices_all_appear_in_List()
    {
        var (_, d1) = MakeDevice("device-1", _time.GetUtcNow());
        var (_, d2) = MakeDevice("device-2", _time.GetUtcNow());

        await _registry.AddAsync(d1, TestContext.Current.CancellationToken);
        await _registry.AddAsync(d2, TestContext.Current.CancellationToken);

        var result = await _registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Add_duplicate_name_throws_case_insensitively()
    {
        var (_, original) = MakeDevice("Laptop", _time.GetUtcNow());
        var (_, duplicate) = MakeDevice("laptop", _time.GetUtcNow());

        await _registry.AddAsync(original, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _registry.AddAsync(duplicate, TestContext.Current.CancellationToken));

        Assert.Contains("already exists", ex.Message);
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_existing_device_returns_true_and_removes_it()
    {
        var (_, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var removed = await _registry.RemoveAsync("laptop", TestContext.Current.CancellationToken);

        Assert.True(removed);
        Assert.Empty(await _registry.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_nonexistent_device_returns_false()
    {
        var removed = await _registry.RemoveAsync("does-not-exist", TestContext.Current.CancellationToken);
        Assert.False(removed);
    }

    [Fact]
    public async Task Remove_is_case_insensitive()
    {
        var (_, device) = MakeDevice("Laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var removed = await _registry.RemoveAsync("laptop", TestContext.Current.CancellationToken);

        Assert.True(removed);
        Assert.Empty(await _registry.ListAsync(TestContext.Current.CancellationToken));
    }

    // ── LookupByToken ────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupByToken_valid_token_returns_matching_device()
    {
        var (rawToken, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var found = await _registry.LookupByTokenAsync(rawToken, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal("laptop", found!.Name);
    }

    [Fact]
    public async Task LookupByToken_wrong_token_returns_null()
    {
        var (_, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var wrongTokenBytes = RandomNumberGenerator.GetBytes(32);
        var wrongToken = Base64Url.EncodeToString(wrongTokenBytes);

        var found = await _registry.LookupByTokenAsync(wrongToken, TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    [Fact]
    public async Task LookupByToken_malformed_token_returns_null()
    {
        var (_, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var found = await _registry.LookupByTokenAsync("not-valid-base64url!!!", TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    [Fact]
    public async Task LookupByToken_returns_null_when_registry_empty()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);

        var found = await _registry.LookupByTokenAsync(rawToken, TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    // ── UpdateLastUsed ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLastUsed_sets_timestamp_to_current_time()
    {
        var createdAt = _time.GetUtcNow();
        var (_, device) = MakeDevice("laptop", createdAt);
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromHours(2));
        await _registry.UpdateLastUsedAsync("laptop", TestContext.Current.CancellationToken);

        var devices = await _registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_time.GetUtcNow(), devices[0].LastUsedAt);
    }

    [Fact]
    public async Task UpdateLastUsed_noop_for_unknown_device()
    {
        // Should not throw
        await _registry.UpdateLastUsedAsync("does-not-exist", TestContext.Current.CancellationToken);
    }

    // ── File round-trip ──────────────────────────────────────────────────────

    [Fact]
    public async Task Devices_survive_registry_recreation()
    {
        var (rawToken, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        // Recreate registry from the same path
        var paths = new NetclawPaths(_tempDir);
        var registry2 = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);

        var found = await registry2.LookupByTokenAsync(rawToken, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal("laptop", found!.Name);
    }

    [Fact]
    public async Task List_returns_defensive_copy()
    {
        var (_, device) = MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, TestContext.Current.CancellationToken);

        var firstResult = await _registry.ListAsync(TestContext.Current.CancellationToken);
        var mutable = Assert.IsType<List<PairedDevice>>(firstResult);
        mutable.Clear();

        var secondResult = await _registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(secondResult);
        Assert.Equal("laptop", secondResult[0].Name);
    }
}
