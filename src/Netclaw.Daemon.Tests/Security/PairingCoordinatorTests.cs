// -----------------------------------------------------------------------
// <copyright file="PairingCoordinatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

public sealed class PairingCoordinatorTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Concurrent_exchange_allows_exactly_one_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_dir.Path);
        var registry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
        var codes = new PairingCodeService(_time);
        var coordinator = new PairingCoordinator(
            codes,
            registry,
            _time,
            NullLogger<PairingCoordinator>.Instance);
        var (code, _) = codes.GenerateCode();

        var exchanges = Enumerable.Range(0, 8)
            .Select(index => coordinator.ExchangeAsync(code, $"device-{index}", ct))
            .ToArray();
        var results = await Task.WhenAll(exchanges);

        Assert.Single(results, result => result.Status == PairingExchangeStatus.Success);
        Assert.All(
            results.Where(result => result.Status != PairingExchangeStatus.Success),
            result => Assert.Equal(PairingExchangeStatus.NoCode, result.Status));
        Assert.Single(await registry.ListAsync(ct));
    }

    [Fact]
    public async Task Registry_write_failure_preserves_pairing_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(paths.ConfigDirectory);
        Directory.CreateDirectory(paths.DevicesPath);
        var registry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
        var codes = new PairingCodeService(_time);
        var coordinator = new PairingCoordinator(
            codes,
            registry,
            _time,
            NullLogger<PairingCoordinator>.Instance);
        var (code, expiry) = codes.GenerateCode();

        var exception = await Record.ExceptionAsync(
            () => coordinator.ExchangeAsync(code, "laptop", ct));

        Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(expiry, codes.GetPendingExpiry());
        Assert.NotNull(codes.TryReserve(code));
    }

    [Fact]
    public async Task Code_expiry_after_admission_does_not_split_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var time = new SequenceTimeProvider(
            start,
            start,
            start,
            start.AddMinutes(6));
        var paths = new NetclawPaths(_dir.Path);
        var registry = new DeviceRegistry(paths, time, NullLogger<DeviceRegistry>.Instance);
        var codes = new PairingCodeService(time);
        var coordinator = new PairingCoordinator(
            codes,
            registry,
            time,
            NullLogger<PairingCoordinator>.Instance);
        var (code, expiry) = codes.GenerateCode();

        var result = await coordinator.ExchangeAsync(code, "laptop", ct);

        Assert.Equal(PairingExchangeStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        Assert.Null(codes.GetPendingExpiry());
        var device = Assert.Single(await registry.ListAsync(ct));
        Assert.True(device.CreatedAt > expiry);
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> _values = new(values);
        private DateTimeOffset _last = values[^1];

        public override DateTimeOffset GetUtcNow()
        {
            if (_values.TryDequeue(out var value))
                _last = value;

            return _last;
        }
    }
}
