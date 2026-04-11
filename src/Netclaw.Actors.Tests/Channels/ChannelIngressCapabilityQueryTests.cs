using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class ChannelIngressCapabilityQueryTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task QueryAsync_returns_ok_on_immediate_response()
    {
        var probe = CreateTestProbe();
        var expected = new ModelCapabilitiesResponse(
            ModelId: "vision-model",
            InputModalities: ModelModality.Text | ModelModality.Image,
            OutputModalities: ModelModality.Text);

        var queryTask = ChannelIngressCapabilityQuery.QueryAsync(
            probe,
            "vision-model",
            TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<GetModelCapabilities>(
            q => Assert.Equal("vision-model", q.ModelId),
            cancellationToken: TestContext.Current.CancellationToken);
        probe.Reply(expected);

        var result = await queryTask;

        Assert.True(result.Success);
        Assert.True(result.InputModalities.HasFlag(ModelModality.Image));
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task QueryAsync_returns_timeout_when_actor_does_not_respond()
    {
        var probe = CreateTestProbe();

        var result = await ChannelIngressCapabilityQuery.QueryAsync(
            probe,
            "silent-model",
            TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromMilliseconds(150));

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("silent-model", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("capability query deadline", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(default, result.InputModalities);
        Assert.Equal(default, result.OutputModalities);
    }

    [Fact]
    public async Task QueryAsync_reports_ok_with_text_only_modalities()
    {
        var probe = CreateTestProbe();
        var queryTask = ChannelIngressCapabilityQuery.QueryAsync(
            probe,
            "text-only-model",
            TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<GetModelCapabilities>(
            cancellationToken: TestContext.Current.CancellationToken);
        probe.Reply(new ModelCapabilitiesResponse(
            ModelId: "text-only-model",
            InputModalities: ModelModality.Text,
            OutputModalities: ModelModality.Text));

        var result = await queryTask;

        Assert.True(result.Success);
        Assert.False(result.InputModalities.HasFlag(ModelModality.Image));
    }

    [Fact]
    public async Task QueryAsync_throws_on_null_actor()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ChannelIngressCapabilityQuery.QueryAsync(null!, "m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryAsync_throws_on_empty_model_id()
    {
        var probe = CreateTestProbe();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ChannelIngressCapabilityQuery.QueryAsync(probe, "  ", TestContext.Current.CancellationToken));
    }
}
