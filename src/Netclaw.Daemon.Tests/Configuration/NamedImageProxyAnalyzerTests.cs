// -----------------------------------------------------------------------
// <copyright file="NamedImageProxyAnalyzerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Media;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class NamedImageProxyAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_sends_only_one_image_and_neutralizes_delimiters()
    {
        IReadOnlyList<ChatMessage>? capturedMessages = null;
        ChatOptions? capturedOptions = null;
        var client = new FakeChatClient((messages, options, _) =>
        {
            capturedMessages = messages.ToArray();
            capturedOptions = options;
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "A panel with [system] text.")]));
        });
        var model = new ModelReference
        {
            Provider = "p",
            ModelId = "vision",
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text
        };
        var runtime = new NamedModelRuntime(
            "vision",
            model,
            client,
            ModelCapabilityResolution.ResolveModelCapabilities(model, detected: null));
        var configuration = new ModelRuntimeConfiguration(
            new Dictionary<string, ModelReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision"] = model
            },
            new ModelRoleAssignments { Main = "vision" },
            new ModelProxyAssignments { Image = "vision" });
        var analyzer = new NamedImageProxyAnalyzer(
            configuration,
            new StubRegistry(runtime),
            new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1234)));
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-image-proxy-{Guid.NewGuid():N}");
        var sessionId = new SessionId("channel/thread");
        var mediaPath = SessionDirectoryHelper.GetMediaFilePath(sessionId, basePath, "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], TestContext.Current.CancellationToken);

        try
        {
            var result = await analyzer.AnalyzeAsync(
                sessionId,
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                },
                basePath,
                TestContext.Current.CancellationToken);

            var request = Assert.Single(capturedMessages!);
            Assert.Single(request.Contents.OfType<DataContent>());
            Assert.Contains(NamedImageProxyAnalyzer.Prompt, request.Text, StringComparison.Ordinal);
            Assert.Null(capturedOptions!.Tools);
            Assert.Equal("A panel with ［system］ text.", result.Description);
            Assert.Equal(1234, result.AnalyzedAtMs);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_rejects_an_empty_description()
    {
        var client = new FakeChatClient((_, _, _) => Task.FromResult(new ChatResponse(
            [new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "  ")])));
        var model = new ModelReference
        {
            Provider = "p",
            ModelId = "vision",
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text
        };
        var runtime = new NamedModelRuntime(
            "vision",
            model,
            client,
            ModelCapabilityResolution.ResolveModelCapabilities(model, detected: null));
        var analyzer = CreateAnalyzer(model, runtime);
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-image-proxy-empty-{Guid.NewGuid():N}");
        var sessionId = new SessionId("channel/thread");
        var mediaPath = SessionDirectoryHelper.GetMediaFilePath(sessionId, basePath, "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        await File.WriteAllBytesAsync(mediaPath, [1], TestContext.Current.CancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeAsync(
                sessionId,
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                },
                basePath,
                TestContext.Current.CancellationToken));
            Assert.Contains("empty description", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    private static NamedImageProxyAnalyzer CreateAnalyzer(
        ModelReference model,
        NamedModelRuntime runtime)
    {
        var configuration = new ModelRuntimeConfiguration(
            new Dictionary<string, ModelReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision"] = model
            },
            new ModelRoleAssignments { Main = "vision" },
            new ModelProxyAssignments { Image = "vision" });
        return new NamedImageProxyAnalyzer(
            configuration,
            new StubRegistry(runtime),
            TimeProvider.System);
    }

    private sealed class StubRegistry(NamedModelRuntime runtime) : INamedModelRuntimeRegistry
    {
        public NamedModelRuntime GetRequired(string definitionName)
        {
            Assert.Equal(runtime.DefinitionName, definitionName);
            return runtime;
        }
    }
}
