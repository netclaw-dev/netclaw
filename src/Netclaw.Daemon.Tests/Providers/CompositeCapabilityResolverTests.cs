// -----------------------------------------------------------------------
// <copyright file="CompositeCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class CompositeCapabilityResolverTests
{
    [Fact]
    public async Task FirstResolver_Succeeds_ReturnsResult()
    {
        var first = new FakeResolver(new ResolvedModelCapabilities(
            "test-model", ModelModality.Text | ModelModality.Image, ModelModality.Text));
        var second = new FakeResolver(null);

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.False(second.WasCalled);
    }

    [Fact]
    public async Task FirstResolver_ReturnsNull_FallsThrough()
    {
        var first = new FakeResolver(null);
        var second = new FakeResolver(new ResolvedModelCapabilities(
            "test-model", ModelModality.Text | ModelModality.Audio, ModelModality.Text));

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Audio, result.InputModalities);
        Assert.True(second.WasCalled);
    }

    [Fact]
    public async Task AllResolvers_ReturnNull_DefaultsToText()
    {
        var first = new FakeResolver(null);
        var second = new FakeResolver(null);

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("unknown-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public async Task FirstResolver_Throws_FallsThrough()
    {
        var first = new ThrowingResolver();
        var second = new FakeResolver(new ResolvedModelCapabilities(
            "test-model", ModelModality.Text, ModelModality.Text));

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }

    private sealed class FakeResolver : IModelCapabilityResolver
    {
        private readonly ResolvedModelCapabilities? _result;
        public bool WasCalled { get; private set; }

        public FakeResolver(ResolvedModelCapabilities? result) => _result = result;

        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingResolver : IModelCapabilityResolver
    {
        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
            => throw new HttpRequestException("Network error");
    }
}
