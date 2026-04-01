using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class ModelCapabilityActorTests : TestKit
{
    private readonly ControllableResolver _resolver = new();

    public ModelCapabilityActorTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IModelCapabilityResolver>(_resolver);
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithModelCapabilityCache();
    }

    [Fact]
    public async Task FirstQuery_TriggersLookup_ReturnsCachedResult()
    {
        _resolver.SetResult("vision-model",
            new ResolvedModelCapabilities("vision-model",
                ModelModality.Text | ModelModality.Image, ModelModality.Text));

        var capActor = ActorRegistry.Get<ModelCapabilityActorKey>();
        var result = await capActor.Ask<ModelCapabilitiesResponse>(
            new GetModelCapabilities("vision-model"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Equal(1, _resolver.CallCount);
    }

    [Fact]
    public async Task SecondQuery_ReturnsCachedWithoutLookup()
    {
        _resolver.SetResult("cached-model",
            new ResolvedModelCapabilities("cached-model",
                ModelModality.Text | ModelModality.Audio, ModelModality.Text));

        var capActor = ActorRegistry.Get<ModelCapabilityActorKey>();

        // First query
        await capActor.Ask<ModelCapabilitiesResponse>(
            new GetModelCapabilities("cached-model"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Second query — should come from cache
        var result = await capActor.Ask<ModelCapabilitiesResponse>(
            new GetModelCapabilities("cached-model"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ModelModality.Text | ModelModality.Audio, result.InputModalities);
        Assert.Equal(1, _resolver.CallCount); // only called once
    }

    [Fact]
    public async Task ResolverFails_DefaultsToTextOnly()
    {
        _resolver.SetThrow("failing-model", new HttpRequestException("Network error"));

        var capActor = ActorRegistry.Get<ModelCapabilityActorKey>();
        var result = await capActor.Ask<ModelCapabilitiesResponse>(
            new GetModelCapabilities("failing-model"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    /// <summary>
    /// Test resolver that returns preconfigured results per model ID.
    /// </summary>
    private sealed class ControllableResolver : IModelCapabilityResolver
    {
        private readonly Dictionary<string, ResolvedModelCapabilities> _results = new();
        private readonly Dictionary<string, Exception> _errors = new();
        private int _callCount;

        public int CallCount => _callCount;

        public void SetResult(string modelId, ResolvedModelCapabilities result)
            => _results[modelId] = result;

        public void SetThrow(string modelId, Exception ex)
            => _errors[modelId] = ex;

        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);

            if (_errors.TryGetValue(modelId, out var ex))
                throw ex;

            _results.TryGetValue(modelId, out var result);
            return Task.FromResult(result);
        }
    }
}
