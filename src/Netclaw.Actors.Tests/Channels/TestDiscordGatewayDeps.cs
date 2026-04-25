using Netclaw.Configuration;

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

    public static NetclawPaths NewTestPaths()
    {
        var path = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-discord-test-{Guid.NewGuid():N}"));
        path.EnsureDirectoriesExist();
        return path;
    }
}
