// -----------------------------------------------------------------------
// <copyright file="TestSlackGatewayDeps.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Test defaults for fields on <see cref="Netclaw.Channels.Slack.SlackGatewayDependencies"/>
/// that Slack test fixtures don't typically exercise directly but are now
/// required by the cross-channel attachment ingress pipeline. Each field
/// is a plain default that tests can override if they need specific
/// behavior (e.g. a text-only model for modality-gap tests).
/// </summary>
internal static class TestSlackGatewayDeps
{
    public static ToolAudienceProfiles DefaultAudienceProfiles
        => ToolAudienceProfileDefaults.CreateProfiles();

    public static ModelCapabilities DefaultVisionCapableModel
        => new()
        {
            ModelId = "test-vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text
        };

    public static ModelCapabilities DefaultTextOnlyModel
        => new()
        {
            ModelId = "test-text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text,
            OutputModalities = ModelModality.Text
        };

    public static IChannelRegistry DefaultChannelRegistry
        => TestChannelRegistries.SlackWithProcessingRenderer(new NoopReplyClient());

    public static NetclawPaths NewTestPaths()
    {
        var path = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-slack-test-{Guid.NewGuid():N}"));
        path.EnsureDirectoriesExist();
        return path;
    }
}
