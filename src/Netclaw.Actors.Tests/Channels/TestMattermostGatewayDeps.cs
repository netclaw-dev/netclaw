// -----------------------------------------------------------------------
// <copyright file="TestMattermostGatewayDeps.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;

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

    /// <summary>
    /// Creates a unique temp directory (with a full <see cref="NetclawPaths"/>
    /// tree) for a Mattermost test. The returned value owns the directory and
    /// deletes it on disposal — callers must <c>await using</c> it so the
    /// <c>/tmp/netclaw-mattermost-test-*</c> tree is not leaked (issue #2266).
    /// </summary>
    public static TestSessionTempDirectory NewTestPaths()
        => TestSessionTempDirectory.Create(
            prefix: "netclaw-mattermost-test-",
            createDirectoryTree: true);
}
