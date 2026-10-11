// -----------------------------------------------------------------------
// <copyright file="TestDiscordGatewayDeps.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Test defaults for fields on <see cref="Netclaw.Channels.Discord.DiscordGatewayDependencies"/>
/// that Discord test fixtures don't typically exercise directly but are now
/// required by the cross-channel attachment ingress pipeline. Each field
/// is a plain default that tests can override if they need specific
/// behavior (e.g. a text-only model for modality-gap tests).
/// </summary>
internal static class TestDiscordGatewayDeps
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

    /// <summary>
    /// Creates a unique temp directory (with a full <see cref="NetclawPaths"/>
    /// tree) for a Discord test. The returned value owns the directory and
    /// deletes it on disposal — callers must <c>await using</c> it so the
    /// <c>/tmp/netclaw-discord-test-*</c> tree is not leaked (issue #2266).
    /// </summary>
    public static TestSessionTempDirectory NewTestPaths()
        => TestSessionTempDirectory.Create(
            prefix: "netclaw-discord-test-",
            createDirectoryTree: true);
}
