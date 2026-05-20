// -----------------------------------------------------------------------
// <copyright file="TestMattermostGatewayDeps.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Channels;

internal static class TestMattermostGatewayDeps
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
        var path = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-mattermost-test-{Guid.NewGuid():N}"));
        path.EnsureDirectoriesExist();
        return path;
    }
}
